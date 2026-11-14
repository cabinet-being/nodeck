// Keeps one save list per tab, reads pages and runs the saving, so work continues when the popup closes.
// The popup (popup.js) shows and edits the list; the state lives in storage.session under `tab:<id>`.
import { ext } from './ext.js';
import { Nodeck } from './api.js';
import { prepareFile } from './files.js';

const MENU_MEDIA = 'nodeck-save-media';
const MENU_GALLERY = 'nodeck-collect-page';
const CONTEXTS = ['page', 'frame', 'image', 'video', 'audio', 'link', 'selection'];
const SMALL_SIDE = 200;
const ITEM_CONCURRENCY = 4;
/** Requests from the context menu waiting for the popup to open (kept in memory: openPopup needs the click). */
const pending = new Map();
const running = new Set();

ext.runtime.onInstalled.addListener(async ({ reason }) => {
  await ext.contextMenus.removeAll();
  ext.contextMenus.create({ id: MENU_MEDIA, title: 'Save to nodeck…', contexts: CONTEXTS });
  ext.contextMenus.create({ id: MENU_GALLERY, title: 'Save page images to nodeck…', contexts: CONTEXTS });
  if (reason === 'install') ext.runtime.openOptionsPage();
});

ext.contextMenus.onClicked.addListener((info, tab) => {
  // Without an image or video under the click (a site's cover layer), the page finds what is under the pointer.
  const media = info.mediaType === 'image' || info.mediaType === 'video';
  const request = info.menuItemId === MENU_MEDIA
    ? { mode: 'single', srcUrl: media ? info.srcUrl : null, mediaType: media ? info.mediaType : null, frameId: info.frameId ?? 0 }
    : { mode: 'gallery', frameId: 0 };
  pending.set(tab.id, request);
  ext.storage.session.set({ [`pending:${tab.id}`]: request });
  openPopup(tab);
});

ext.tabs.onRemoved.addListener((tabId) => {
  pending.delete(tabId);
  if (!running.has(tabId)) ext.storage.session.remove([`tab:${tabId}`, `pending:${tabId}`]);
  ext.declarativeNetRequest.updateSessionRules({ removeRuleIds: [ruleIdFor(tabId)] }).catch(() => {});
});

async function openPopup(tab) {
  try {
    await ext.action.openPopup();
  } catch {
    // Browsers without action.openPopup (or with the window not focused): the same page in a small window.
    await ext.windows.create({
      url: ext.runtime.getURL(`src/popup.html?tab=${tab.id}&window=1`), type: 'popup', width: 800, height: 640,
    });
  }
}

ext.runtime.onMessage.addListener((message, sender, sendResponse) => {
  const handler = handlers[message?.type];
  if (!handler) return false;
  handler(message).then((result) => sendResponse({ ok: true, result }), (error) =>
    sendResponse({ ok: false, error: error?.message ?? String(error) }));
  return true;
});

const handlers = {
  /** The popup opened for a tab: a waiting menu request, the list it already has, or a fresh read of the page. */
  async open({ tabId }) {
    const stored = await ext.storage.session.get([`pending:${tabId}`, `tab:${tabId}`]);
    const request = pending.get(tabId) ?? stored[`pending:${tabId}`];
    pending.delete(tabId);
    if (request) {
      await ext.storage.session.remove(`pending:${tabId}`);
      return await scan(tabId, request);
    }
    const tab = await ext.tabs.get(tabId);
    const state = stored[`tab:${tabId}`];
    if (state && (state.pageUrl === tab.url || running.has(tabId) || state.job?.status === 'running')) return state;
    return await scan(tabId, { mode: 'gallery', frameId: 0 });
  },
  rescan: ({ tabId }) => scan(tabId, { mode: 'gallery', frameId: 0 }),
  /** Stores the popup's edits (selection, order, title, tags, source, mode). */
  async update({ tabId, changes }) {
    const state = await getState(tabId);
    if (!state) throw new Error('Nothing to save on this tab.');
    if (state.job?.status === 'running') return state;
    Object.assign(state, changes);
    await putState(tabId, state);
    return state;
  },
  async save({ tabId }) {
    runSave(tabId).catch(async (error) => {
      const state = await getState(tabId);
      if (state) await putState(tabId, { ...state, job: { ...state.job, status: 'failed', error: error.message } });
    });
    return await getState(tabId);
  },
  async retry({ tabId, key }) {
    const state = await getState(tabId);
    const item = state?.items.find((candidate) => candidate.key === key);
    if (!item) throw new Error('That file is no longer in the list.');
    Object.assign(item, { status: 'idle', message: null, progress: null, serverFailed: null });
    await putState(tabId, state);
    runSave(tabId).catch(() => {});
    return state;
  },
  /** A finished save: start over with the same page. */
  reset: ({ tabId }) => scan(tabId, { mode: 'gallery', frameId: 0 }),
};

const getState = async (tabId) => (await ext.storage.session.get(`tab:${tabId}`))[`tab:${tabId}`] ?? null;
const putState = (tabId, state) => ext.storage.session.set({ [`tab:${tabId}`]: state });

// ---------- reading the page ----------

async function scan(tabId, { mode, srcUrl, mediaType, frameId }) {
  const tab = await ext.tabs.get(tabId);
  let page;
  try {
    const target = { tabId, frameIds: [frameId ?? 0] };
    await ext.scripting.executeScript({ target, files: ['src/sites.js', 'src/page.js'] });
    const hints = await readPageHints(target);
    const [result] = await ext.scripting.executeScript({
      target,
      func: (mode, srcUrl, mediaType, hints) => mode === 'single'
        ? globalThis.nodeckPage.describe(srcUrl, mediaType, hints)
        : globalThis.nodeckPage.collect(hints),
      args: [mode, srcUrl ?? null, mediaType ?? null, hints],
    });
    page = result?.result;
    if (!page) throw new Error('The page returned nothing.');
  } catch (error) {
    // Pages the extension cannot read (browser pages, the extension store, some PDFs).
    const host = safeHost(tab.url);
    page = {
      mode,
      pageUrl: tab.url ?? '',
      title: tab.title ?? '',
      tags: [],
      source: host ? { name: host, url: tab.url } : { name: '', url: '' },
      items: srcUrl ? [{ url: srcUrl, mediaType: mediaType ?? 'image', thumbUrl: srcUrl }] : [],
      warning: `Could not read this page (${error?.message ?? error}). Tags were not filled in.`,
    };
  }
  const items = (page.items ?? []).map((item) => {
    const small = item.tiny || (item.width && item.height && Math.min(item.width, item.height) < SMALL_SIDE);
    return {
      ...item,
      key: crypto.randomUUID(),
      browserKey: crypto.randomUUID(),
      selected: !!item.url && (page.mode === 'single' || !small),
      status: item.url ? 'idle' : 'unsupported',
      message: item.url ? null : item.error ?? 'Cannot be saved.',
      progress: null,
      cardId: null,
    };
  });
  const state = {
    tabId,
    origin: page.mode,
    pageUrl: page.pageUrl || tab.url,
    pageTitle: tab.title ?? '',
    title: page.title ?? '',
    tags: page.tags ?? [],
    source: page.source ?? { name: '', url: '' },
    needsSession: !!page.needsSession,
    warning: page.warning ?? null,
    error: page.error ?? null,
    mode: 'cards',
    items,
    job: { status: 'idle' },
    scannedAt: Date.now(),
  };
  await putState(tabId, state);
  await allowReferer(tabId, state);
  return state;
}

/**
 * Data that only the page's own scripts hold: the video list of players that build their stream in
 * script (flashvars mediaDefinitions) and the X video files seen by x-hook.js.
 */
async function readPageHints(target) {
  try {
    const [result] = await ext.scripting.executeScript({
      target,
      world: 'MAIN',
      func: () => {
        const hints = {};
        try {
          for (const key of Object.keys(window)) {
            const definitions = key.startsWith('flashvars_') ? window[key]?.mediaDefinitions : null;
            if (Array.isArray(definitions)) {
              hints.mediaDefinitions = definitions.map((d) => ({ format: d.format, videoUrl: d.videoUrl, quality: d.quality }));
              break;
            }
          }
        } catch { /* ignore */ }
        try { if (window.__nodeckXMedia) hints.xMedia = JSON.parse(JSON.stringify(window.__nodeckXMedia)); } catch { /* ignore */ }
        return hints;
      },
    });
    return result?.result ?? {};
  } catch { return {}; }
}

const ruleIdFor = (tabId) => (Math.abs(tabId) % 1_000_000) + 1;

/** The extension's own requests for this tab's files (previews and downloads) carry the page as Referer. */
async function allowReferer(tabId, state) {
  const id = ruleIdFor(tabId);
  const hosts = [...new Set(state.items.flatMap((item) => [item.url, item.fallbackUrl, item.thumbUrl])
    .filter((url) => /^https?:/.test(url ?? '')).map((url) => new URL(url).hostname))];
  try {
    await ext.declarativeNetRequest.updateSessionRules({
      removeRuleIds: [id],
      addRules: hosts.length && /^https?:/.test(state.pageUrl) ? [{
        id,
        priority: 1,
        action: { type: 'modifyHeaders', requestHeaders: [{ header: 'referer', operation: 'set', value: state.pageUrl }] },
        condition: {
          requestDomains: hosts,
          initiatorDomains: [new URL(ext.runtime.getURL('')).hostname],
          resourceTypes: ['xmlhttprequest', 'image', 'media', 'other'],
        },
      }] : [],
    });
  } catch { /* previews may then be refused by hotlink protection; saving still tries nodeck first */ }
}

// ---------- saving ----------

/** Saves the selected files in order: each as a card, then the comic or set; tags and source are added. */
async function runSave(tabId) {
  if (running.has(tabId)) return;
  running.add(tabId);
  const keepAlive = setInterval(() => ext.runtime.getPlatformInfo().catch(() => {}), 20_000);
  let state = await getState(tabId);
  try {
    const api = await Nodeck.fromSettings();
    const selected = state.items.filter((item) => item.selected && item.url);
    if (selected.length === 0) throw new Error('Choose at least one file.');
    state.job = { ...state.job, status: 'running', error: null, startedAt: state.job?.startedAt ?? Date.now(),
      collectionKey: state.job?.collectionKey ?? crypto.randomUUID() };
    await putState(tabId, state);

    const write = throttle(() => putState(tabId, state), 250);
    const catalog = resolveCatalog(api, state);
    const cardTitle = (index) => (state.mode === 'cards' && selected.length > 1 && state.title
      ? `${state.title} (${index + 1})` : state.mode === 'cards' ? state.title : null) || null;

    let next = 0;
    const worker = async () => {
      while (next < selected.length) {
        const index = next++;
        const item = selected[index];
        if (item.status === 'done') continue;
        try {
          await saveItem(api, state, item, cardTitle(index), write);
          if (state.mode === 'cards') {
            item.status = 'tagging';
            write();
            item.catalogWarnings = await applyCatalog(api, await catalog, item.cardId);
          }
          item.status = 'done';
          item.message = null;
        } catch (error) {
          item.status = 'failed';
          item.message = error.message ?? String(error);
        }
        item.progress = null;
        write();
      }
    };
    await Promise.all(Array.from({ length: ITEM_CONCURRENCY }, worker));
    await write.flush();

    const failed = selected.filter((item) => item.status !== 'done');
    if (failed.length) {
      state.job.status = 'failed';
      state.job.error = `${failed.length} of ${selected.length} not saved. Retry them, or remove them and save again.`;
    } else if (state.mode === 'cards') {
      state.job.status = 'done';
      state.job.cardIds = selected.map((item) => item.cardId);
    } else {
      state.job.status = 'running';
      state.job.step = `Making the ${state.mode}…`;
      await putState(tabId, state);
      const card = await api.createCollection(state.mode, state.title, selected.map((item) => item.cardId), state.job.collectionKey);
      state.job.step = 'Adding tags and source…';
      await putState(tabId, state);
      state.job.catalogWarnings = await applyCatalog(api, await catalog, card.id);
      state.job.status = 'done';
      state.job.collectionId = card.id;
      state.job.cardIds = selected.map((item) => item.cardId);
    }
    state.job.step = null;
    state.job.finishedAt = Date.now();
    state.job.serverUrl = api.baseUrl;
  } catch (error) {
    state.job = { ...state.job, status: 'failed', error: error.message ?? String(error), step: null };
  } finally {
    clearInterval(keepAlive);
    running.delete(tabId);
    await putState(tabId, state);
  }
}

/**
 * One file: nodeck downloads it itself when it can (the bytes do not pass through the browser);
 * otherwise, or for sites that serve files only to the logged-in browser, the browser sends it.
 */
async function saveItem(api, state, item, title, write) {
  item.status = 'preparing';
  item.message = null;
  write();
  const serverCanFetch = !state.needsSession && /^https?:/.test(item.url);
  if (serverCanFetch && !item.serverFailed) {
    item.status = 'importing';
    item.via = 'server';
    write();
    try {
      const card = await api.importUrl({ url: item.url, referer: state.pageUrl, pageUrl: state.pageUrl, title }, item.key);
      item.cardId = card.id;
      return;
    } catch (error) {
      if (error.status === 413) throw error;
      // nodeck could not get it (a refused or private address, an old nodeck, a file type to convert): send it from here.
      item.serverFailed = error.message;
    }
  }
  item.via = 'browser';
  const file = await prepareFile(item, state, (progress) => {
    item.status = 'downloading';
    item.progress = progress;
    write();
  });
  item.status = 'uploading';
  item.progress = null;
  write();
  const card = await api.uploadMedia(file.blob, file.fileName, title, item.browserKey);
  item.cardId = card.id;
  // The full file could not be downloaded; the copy the page shows was saved instead.
  item.note = file.usedFallback ? 'smaller copy' : null;
}

/** Looks up (or creates) every tag and the source once per save, a few at a time. */
async function resolveCatalog(api, state) {
  const warnings = [];
  const tagIds = [];
  await pool(state.tags, 6, async (tag) => {
    try { tagIds.push((await api.ensureTag(tag)).id); } catch (error) { warnings.push(`tag “${tag.name}”: ${error.message}`); }
  });
  let source = null;
  const name = state.source?.name?.trim();
  if (name) {
    try { source = await api.ensureSource(name); } catch (error) { warnings.push(`source: ${error.message}`); }
  }
  return { tagIds, sourceId: source?.id ?? null, sourceUrl: state.source?.url ?? '', warnings };
}

async function applyCatalog(api, catalog, cardId) {
  const warnings = [...catalog.warnings];
  await pool(catalog.tagIds, 6, async (tagId) => {
    try { await api.setCardTag(cardId, tagId); } catch (error) { warnings.push(error.message); }
  });
  if (catalog.sourceId) {
    try {
      await api.setCardSource(cardId, catalog.sourceId, /^https?:\/\//i.test(catalog.sourceUrl) ? catalog.sourceUrl : '');
    } catch (error) { warnings.push(`source: ${error.message}`); }
  }
  return [...new Set(warnings)];
}

async function pool(values, size, fn) {
  let next = 0;
  await Promise.all(Array.from({ length: Math.min(size, values.length) }, async () => {
    while (next < values.length) await fn(values[next++]);
  }));
}

function throttle(fn, wait) {
  let timer = null;
  let last = Promise.resolve();
  const run = () => { timer = null; last = fn(); return last; };
  const throttled = () => { if (!timer) timer = setTimeout(run, wait); };
  throttled.flush = async () => { if (timer) { clearTimeout(timer); await run(); } else await last; };
  return throttled;
}

function safeHost(url) {
  try {
    const { protocol, hostname } = new URL(url);
    return /^https?:$/.test(protocol) ? hostname.replace(/^www\./, '') : null;
  } catch { return null; }
}

// Lets automated checks drive the same steps as the popup.
globalThis.nodeckHandlers = handlers;
