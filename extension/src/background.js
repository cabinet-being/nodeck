// Context menu, toolbar button and the review window. The saving itself happens in review.js.
import { ext } from './ext.js';

const MENU_MEDIA = 'nodeck-save-media';
const MENU_GALLERY = 'nodeck-collect-page';
const CONTEXTS = ['page', 'frame', 'image', 'video', 'audio', 'link', 'selection'];

ext.runtime.onInstalled.addListener(async ({ reason }) => {
  await ext.contextMenus.removeAll();
  ext.contextMenus.create({
    id: MENU_MEDIA,
    title: 'Save to nodeck…',
    contexts: CONTEXTS,
  });
  ext.contextMenus.create({
    id: MENU_GALLERY,
    title: 'Save page images to nodeck…',
    contexts: CONTEXTS,
  });
  if (reason === 'install') ext.runtime.openOptionsPage();
});

ext.contextMenus.onClicked.addListener((info, tab) => {
  // Without an image or video under the click (a site's cover layer), the page finds what is under the pointer.
  const media = info.mediaType === 'image' || info.mediaType === 'video';
  if (info.menuItemId === MENU_MEDIA)
    startJob(tab, { mode: 'single', srcUrl: media ? info.srcUrl : null, mediaType: media ? info.mediaType : null,
      frameId: info.frameId ?? 0 });
  else if (info.menuItemId === MENU_GALLERY)
    startJob(tab, { mode: 'gallery', frameId: 0 });
});

ext.action.onClicked.addListener((tab) => startJob(tab, { mode: 'gallery', frameId: 0 }));

/** Reads the page, stores the job for the review window and opens that window. */
async function startJob(tab, { mode, srcUrl, mediaType, frameId }) {
  const id = crypto.randomUUID();
  let job;
  try {
    const target = { tabId: tab.id, frameIds: [frameId] };
    await ext.scripting.executeScript({ target, files: ['src/sites.js', 'src/page.js'] });
    const hints = await readPageHints(target);
    const [result] = await ext.scripting.executeScript({
      target,
      func: (mode, srcUrl, mediaType, hints) => mode === 'single'
        ? globalThis.nodeckPage.describe(srcUrl, mediaType, hints)
        : globalThis.nodeckPage.collect(hints),
      args: [mode, srcUrl ?? null, mediaType ?? null, hints],
    });
    job = result?.result;
    if (!job) throw new Error('The page returned nothing.');
  } catch (error) {
    // Pages the extension cannot read (browser pages, the extension store, some PDFs).
    const host = safeHost(tab.url);
    job = {
      mode,
      mediaType: mediaType ?? 'image',
      pageUrl: tab.url ?? '',
      title: tab.title ?? '',
      tags: [],
      source: host ? { name: host, url: tab.url } : { name: '', url: '' },
      items: srcUrl ? [{ url: srcUrl, mediaType: mediaType ?? 'image', fallbackUrl: null }] : [],
      warning: `Could not read this page (${error?.message ?? error}). Tags were not filled in.`,
    };
  }
  job.id = id;
  job.tabId = tab.id;
  job.frameId = frameId;
  await ext.storage.session.set({ [`job:${id}`]: job });
  const gallery = mode === 'gallery';
  await ext.windows.create({
    url: ext.runtime.getURL(`src/review.html?job=${id}`),
    type: 'popup',
    width: gallery ? 1100 : 560,
    height: gallery ? 860 : 780,
  });
  return id;
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

function safeHost(url) {
  try {
    const { protocol, hostname } = new URL(url);
    return /^https?:$/.test(protocol) ? hostname.replace(/^www\./, '') : null;
  } catch { return null; }
}

// Lets automated checks start a job without clicking the browser's own context menu.
globalThis.nodeckStartJob = startJob;
