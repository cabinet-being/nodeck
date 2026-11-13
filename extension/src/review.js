// Review window: shows what will be saved, lets the user edit it, then saves it to nodeck.
import { ext } from './ext.js';
import { Nodeck } from './api.js';

const $ = (id) => document.getElementById(id);
const CATEGORIES = ['artist', 'character', 'copyright', 'general', 'meta'];
const SMALL_SIDE = 300;
const jobId = new URLSearchParams(location.search).get('job');

let job;
let api;
let ruleId = null;
let saving = false;
let saved = false;
/** Pages in order: { url, fallbackUrl, key, cardId, state, blob, objectUrl, width, height, error, removed }. */
let items = [];

init().catch((error) => showFatal(error.message ?? String(error)));

async function init() {
  job = (await ext.storage.session.get(`job:${jobId}`))[`job:${jobId}`];
  if (!job) return showFatal('This save window has expired. Start again from the page.');
  api = await Nodeck.fromSettings();
  $('server').textContent = api.baseUrl.replace(/^https?:\/\//, '');
  const gallery = job.mode === 'gallery';
  document.body.classList.toggle('gallery', gallery);
  $('heading').textContent = gallery ? 'Save pages to nodeck' : `Save ${job.mediaType === 'video' ? 'video' : 'image'} to nodeck`;
  if (job.warning) { $('warning').textContent = job.warning; $('warning').hidden = false; }
  $('title').value = job.title ?? '';
  $('sourceName').value = job.source?.name ?? '';
  $('sourceUrl').value = /^https?:/.test(job.source?.url ?? '') ? job.source.url : '';
  $('kindField').hidden = !gallery;
  $('pagesSection').hidden = !gallery;
  $('preview').hidden = gallery;
  job.tags ??= [];
  renderTags();
  wireForm();

  items = job.items.map((item, index) => ({ ...item, key: job.keys?.[index] ?? crypto.randomUUID(), state: 'loading' }));
  job.keys = items.map((item) => item.key);
  await persist();
  if (items.length === 0) return showFatal(gallery ? 'No images were found on this page.' : 'Nothing to save.');
  await allowReferer();
  render();
  await loadAll();
}

function showFatal(message) {
  $('status').textContent = message;
  $('status').className = 'status error';
  $('save').disabled = true;
}

async function persist() {
  await ext.storage.session.set({ [`job:${jobId}`]: job });
}

// ---------- tags ----------

function renderTags() {
  const box = $('tags');
  box.replaceChildren(...job.tags.map((tag, index) => {
    const chip = document.createElement('span');
    chip.className = `chip ${tag.category}`;
    chip.title = tag.category;
    const label = document.createElement('span');
    label.textContent = tag.category === 'custom' || tag.category === 'general' ? tag.name : `${tag.category}: ${tag.name}`;
    const remove = document.createElement('button');
    remove.type = 'button';
    remove.textContent = '×';
    remove.setAttribute('aria-label', `Remove tag ${tag.name}`);
    remove.addEventListener('click', () => { job.tags.splice(index, 1); renderTags(); persist(); });
    chip.append(label, remove);
    return chip;
  }));
  $('tagCount').textContent = job.tags.length ? `(${job.tags.length})` : '— none found, add your own';
}

function addTag(text) {
  const match = text.match(/^\s*(\w+)\s*:\s*(.+)$/);
  const category = match && CATEGORIES.includes(match[1].toLowerCase()) ? match[1].toLowerCase() : 'custom';
  const name = (category === 'custom' ? text : match[2]).replace(/\s+/g, ' ').trim();
  if (!name) return;
  if (!job.tags.some((tag) => tag.name.toLowerCase() === name.toLowerCase())) job.tags.push({ name, category });
  renderTags();
  persist();
}

function wireForm() {
  $('tagInput').addEventListener('keydown', (event) => {
    if (event.key !== 'Enter' && event.key !== ',') return;
    event.preventDefault();
    addTag(event.target.value);
    event.target.value = '';
  });
  $('tagInput').addEventListener('blur', (event) => {
    if (event.target.value.trim()) { addTag(event.target.value); event.target.value = ''; }
  });
  $('save').addEventListener('click', save);
  $('cancel').addEventListener('click', () => window.close());
  $('reverse').addEventListener('click', () => { if (!saving) { items.reverse(); render(); } });
  $('removeSmall').addEventListener('click', () => {
    if (saving) return;
    for (const item of items)
      if (item.state === 'ready' && item.width && Math.min(item.width, item.height) < SMALL_SIDE) item.removed = true;
    render();
  });
}

// ---------- fetching files ----------

/** Lets the extension send the page as Referer when it downloads the images (hotlink protection). */
async function allowReferer() {
  if (!/^https?:/.test(job.pageUrl ?? '')) return;
  const hosts = [...new Set(items.flatMap((item) => [item.url, item.fallbackUrl])
    .filter((url) => /^https?:/.test(url ?? '')).map((url) => new URL(url).hostname))];
  if (hosts.length === 0) return;
  ruleId = 1 + Math.floor(Math.random() * 2_000_000_000);
  try {
    await ext.declarativeNetRequest.updateSessionRules({
      addRules: [{
        id: ruleId,
        priority: 1,
        action: { type: 'modifyHeaders', requestHeaders: [{ header: 'referer', operation: 'set', value: job.pageUrl }] },
        condition: {
          requestDomains: hosts,
          initiatorDomains: [new URL(ext.runtime.getURL('')).hostname],
          resourceTypes: ['xmlhttprequest', 'other', 'media', 'image'],
        },
      }],
    });
  } catch { ruleId = null; }
}

window.addEventListener('pagehide', () => {
  if (ruleId) ext.declarativeNetRequest.updateSessionRules({ removeRuleIds: [ruleId] });
});

async function loadAll() {
  let next = 0;
  const worker = async () => {
    while (next < items.length) {
      const item = items[next++];
      try {
        await loadItem(item);
        item.state = 'ready';
        if (job.mode === 'gallery' && item.width && Math.min(item.width, item.height) < 120) item.removed = true;
      } catch (error) {
        item.state = 'error';
        item.error = error.message ?? String(error);
        if (job.mode === 'gallery') item.removed = true;
      }
      render();
    }
  };
  await Promise.all(Array.from({ length: 4 }, worker));
  updateSave();
}

async function loadItem(item) {
  let lastError = null;
  for (const url of [item.url, item.fallbackUrl].filter(Boolean)) {
    for (const viaPage of url.startsWith('blob:') ? [true] : [false, true]) {
      try {
        const blob = viaPage ? await fetchViaPage(url) : await fetchDirect(url);
        const file = await normalize(blob, url);
        Object.assign(item, file, { objectUrl: URL.createObjectURL(file.blob), loadedFrom: url });
        return;
      } catch (error) { lastError = error; }
    }
  }
  throw lastError ?? new Error('Could not download the file.');
}

async function fetchDirect(url) {
  const response = await fetch(url, { credentials: 'include' });
  if (!response.ok) throw new Error(`The site answered ${response.status}.`);
  return await response.blob();
}

async function fetchViaPage(url) {
  const [result] = await ext.scripting.executeScript({
    target: { tabId: job.tabId, frameIds: [job.frameId ?? 0] },
    func: (fileUrl) => globalThis.nodeckPage?.fetchAsDataUrl(fileUrl),
    args: [url],
  });
  if (!result?.result) throw new Error('The page could not download the file.');
  return await (await fetch(result.result)).blob();
}

const SIGNATURES = [
  { type: 'image/jpeg', ext: 'jpg', test: (b) => b[0] === 0xff && b[1] === 0xd8 && b[2] === 0xff },
  { type: 'image/png', ext: 'png', test: (b) => b[0] === 0x89 && b[1] === 0x50 && b[2] === 0x4e && b[3] === 0x47 },
  { type: 'image/gif', ext: 'gif', test: (b) => b[0] === 0x47 && b[1] === 0x49 && b[2] === 0x46 },
  { type: 'image/webp', ext: 'webp', test: (b) => ascii(b, 0, 4) === 'RIFF' && ascii(b, 8, 12) === 'WEBP' },
  { type: 'video/webm', ext: 'webm', test: (b) => b[0] === 0x1a && b[1] === 0x45 && b[2] === 0xdf && b[3] === 0xa3 },
  { type: 'video/quicktime', ext: 'mov', test: (b) => ascii(b, 4, 8) === 'ftyp' && ascii(b, 8, 10) === 'qt' },
  { type: 'video/mp4', ext: 'mp4', test: (b) => ascii(b, 4, 8) === 'ftyp' && !/^(avif|avis|heic|heix|mif1)/.test(ascii(b, 8, 12)) },
];

function ascii(bytes, start, end) {
  return String.fromCharCode(...bytes.slice(start, end));
}

/** nodeck accepts JPEG, PNG, WebP, GIF, MP4, WebM and MOV; other images (AVIF, BMP, SVG…) become PNG. */
async function normalize(blob, url) {
  if (blob.size === 0) throw new Error('The file is empty.');
  const head = new Uint8Array(await blob.slice(0, 16).arrayBuffer());
  let kind = SIGNATURES.find((signature) => signature.test(head));
  let file = kind ? new Blob([blob], { type: kind.type }) : null;
  let bitmap = null;
  if (!kind || kind.type.startsWith('image/')) {
    try { bitmap = await createImageBitmap(blob); } catch { /* not decodable here */ }
  }
  if (!kind) {
    if (!bitmap) throw new Error(`Unsupported file type (${blob.type || 'unknown'}).`);
    const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
    canvas.getContext('2d').drawImage(bitmap, 0, 0);
    file = await canvas.convertToBlob({ type: 'image/png' });
    kind = SIGNATURES[1];
  }
  const width = bitmap?.width ?? 0;
  const height = bitmap?.height ?? 0;
  bitmap?.close();
  return { blob: file, type: kind.type, fileName: fileName(url, kind.ext), width, height };
}

function fileName(url, extension) {
  let base = 'file';
  try {
    const last = decodeURIComponent(new URL(url).pathname.split('/').filter(Boolean).pop() ?? '');
    base = last.replace(/\.[a-z0-9]{2,5}$/i, '').replace(/[^\p{L}\p{N}._ -]+/gu, '_').slice(0, 80) || base;
  } catch { /* keep default */ }
  return `${base}.${extension}`;
}

// ---------- rendering ----------

function render() {
  if (job.mode === 'single') return renderSingle();
  const kept = items.filter((item) => !item.removed);
  const removed = items.filter((item) => item.removed);
  $('grid').replaceChildren(...kept.map((item, index) => pageTile(item, index, kept.length)));
  $('removed').replaceChildren(...removed.map((item) => removedTile(item)));
  $('removedBox').hidden = removed.length === 0;
  $('removedHeading').textContent = `Removed (${removed.length}) — click one to put it back`;
  const loading = items.filter((item) => item.state === 'loading').length;
  $('pagesHeading').textContent = `Pages (${kept.length})${loading ? ` — loading ${loading}…` : ''}`;
  updateSave();
}

function renderSingle() {
  const item = items[0];
  const box = $('preview');
  if (item.state === 'loading') {
    box.textContent = 'Loading…';
  } else if (item.state === 'error') {
    box.textContent = `Could not load the file: ${item.error}`;
    box.classList.add('error');
  } else {
    const media = document.createElement(item.type.startsWith('video/') ? 'video' : 'img');
    media.src = item.objectUrl;
    if (media.tagName === 'VIDEO') { media.controls = true; media.muted = true; }
    const info = document.createElement('small');
    info.textContent = [item.width && `${item.width}×${item.height}`, item.type.split('/')[1], formatSize(item.blob.size)]
      .filter(Boolean).join(' · ');
    box.replaceChildren(media, info);
  }
  updateSave();
}

function formatSize(bytes) {
  return bytes > 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.ceil(bytes / 1024)} KB`;
}

function pageTile(item, index, count) {
  const li = document.createElement('li');
  li.className = `page ${item.state}`;
  li.draggable = !saving && !saved;
  li.dataset.index = String(index);
  const thumb = document.createElement('div');
  thumb.className = 'thumb';
  if (item.state === 'ready') {
    const img = document.createElement('img');
    img.src = item.objectUrl;
    img.alt = `Page ${index + 1}`;
    img.draggable = false;
    thumb.append(img);
  } else {
    thumb.textContent = 'Loading…';
  }
  const number = document.createElement('span');
  number.className = 'number';
  number.textContent = String(index + 1);
  const info = document.createElement('small');
  info.textContent = item.cardId ? 'saved' : item.width ? `${item.width}×${item.height}` : '';
  const tools = document.createElement('div');
  tools.className = 'page-tools';
  tools.append(
    iconButton('←', `Move page ${index + 1} earlier`, index === 0, () => move(item, -1)),
    iconButton('→', `Move page ${index + 1} later`, index === count - 1, () => move(item, 1)),
    iconButton('×', `Remove page ${index + 1}`, false, () => { item.removed = true; render(); }, 'remove'),
  );
  li.append(thumb, number, info, tools);
  li.addEventListener('dragstart', (event) => {
    event.dataTransfer.setData('text/plain', String(index));
    event.dataTransfer.effectAllowed = 'move';
    li.classList.add('dragging');
  });
  li.addEventListener('dragend', () => li.classList.remove('dragging'));
  li.addEventListener('dragover', (event) => { event.preventDefault(); li.classList.add('over'); });
  li.addEventListener('dragleave', () => li.classList.remove('over'));
  li.addEventListener('drop', (event) => {
    event.preventDefault();
    li.classList.remove('over');
    const from = Number(event.dataTransfer.getData('text/plain'));
    if (!Number.isNaN(from)) moveTo(from, index);
  });
  return li;
}

function removedTile(item) {
  const li = document.createElement('li');
  li.className = 'page removed-page';
  const button = document.createElement('button');
  button.type = 'button';
  button.title = item.error ? `Could not load: ${item.error}` : 'Put this page back';
  if (item.state === 'ready') {
    const img = document.createElement('img');
    img.src = item.objectUrl;
    img.alt = 'Removed page';
    button.append(img);
  } else {
    button.textContent = item.state === 'error' ? 'Failed' : '…';
  }
  button.disabled = item.state !== 'ready' || saving || saved;
  button.addEventListener('click', () => { item.removed = false; render(); });
  li.append(button);
  return li;
}

function iconButton(text, label, disabled, onClick, extraClass = '') {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = `icon ${extraClass}`;
  button.textContent = text;
  button.title = label;
  button.setAttribute('aria-label', label);
  button.disabled = disabled || saving || saved;
  button.addEventListener('click', onClick);
  return button;
}

function move(item, delta) {
  const kept = items.filter((candidate) => !candidate.removed);
  const index = kept.indexOf(item);
  moveTo(index, index + delta);
}

/** Moves the kept page at position `from` to position `to` (positions among kept pages). */
function moveTo(from, to) {
  if (saving || saved) return;
  const kept = items.filter((item) => !item.removed);
  if (from === to || to < 0 || to >= kept.length) return;
  const [moved] = kept.splice(from, 1);
  kept.splice(to, 0, moved);
  items = [...kept, ...items.filter((item) => item.removed)];
  render();
}

function updateSave() {
  const button = $('save');
  if (saved) return;
  if (job.mode === 'single') {
    button.disabled = saving || items[0]?.state !== 'ready';
    button.textContent = saving ? 'Saving…' : 'Save';
    return;
  }
  const kept = items.filter((item) => !item.removed);
  const kind = document.querySelector('input[name="kind"]:checked').value;
  button.disabled = saving || kept.length === 0 || kept.some((item) => item.state !== 'ready');
  button.textContent = saving ? 'Saving…' : `Save ${kept.length} page${kept.length === 1 ? '' : 's'} as a ${kind}`;
}
document.addEventListener('change', (event) => { if (event.target.name === 'kind') updateSave(); });

// ---------- saving ----------

async function save() {
  if (saving || saved) return;
  saving = true;
  setStatus('');
  render();
  const warnings = [];
  try {
    const title = $('title').value.trim();
    let card;
    if (job.mode === 'single') {
      card = await uploadItem(items[0], title);
    } else {
      const kept = items.filter((item) => !item.removed);
      let done = kept.filter((item) => item.cardId).length;
      showProgress(done, kept.length + 1);
      let next = 0;
      const worker = async () => {
        while (next < kept.length) {
          const item = kept[next++];
          if (item.cardId) continue;
          await uploadItem(item, null);
          showProgress(++done, kept.length + 1);
          render();
        }
      };
      await Promise.all(Array.from({ length: 3 }, worker));
      const kind = document.querySelector('input[name="kind"]:checked').value;
      job.collectionKey ??= crypto.randomUUID();
      await persist();
      card = await api.createCollection(kind, title, kept.map((item) => item.cardId), job.collectionKey);
      showProgress(kept.length + 1, kept.length + 1);
    }
    warnings.push(...await applyCatalog(card.id));
    saved = true;
    job.savedCardId = card.id;
    await persist();
    showSaved(card, warnings);
  } catch (error) {
    setStatus(`Not saved: ${error.message}${hasSavedPages() ? ' Pages already uploaded are kept; Save again continues.' : ''}`, 'error');
  } finally {
    saving = false;
    render();
  }
}

function hasSavedPages() {
  return job.mode === 'gallery' && items.some((item) => item.cardId);
}

async function uploadItem(item, title) {
  const card = await api.uploadMedia(item.blob, item.fileName, title, item.key);
  item.cardId = card.id;
  return card;
}

/** Adds the tags and the source; failures here do not undo the saved card. */
async function applyCatalog(cardId) {
  const warnings = [];
  for (const tag of job.tags) {
    try {
      const entry = await api.ensureTag(tag);
      await api.setCardTag(cardId, entry.id);
    } catch (error) {
      warnings.push(`tag “${tag.name}”: ${error.message}`);
    }
  }
  const sourceName = $('sourceName').value.trim();
  const sourceUrl = $('sourceUrl').value.trim();
  if (sourceName) {
    try {
      const source = await api.ensureSource(sourceName);
      await api.setCardSource(cardId, source.id, /^https?:\/\//i.test(sourceUrl) ? sourceUrl : '');
    } catch (error) {
      warnings.push(`source: ${error.message}`);
    }
  }
  return warnings;
}

function showProgress(done, total) {
  $('progress').hidden = false;
  $('progressBar').style.width = `${Math.round((done / total) * 100)}%`;
}

function showSaved(card, warnings) {
  const status = $('status');
  status.className = 'status ok';
  status.replaceChildren();
  status.append(`Saved${card.title ? ` “${card.title}”` : ''}. `);
  const link = document.createElement('a');
  link.href = api.cardUrl(card.id);
  link.target = '_blank';
  link.rel = 'noopener';
  link.id = 'cardLink';
  link.textContent = 'Open in nodeck ↗';
  status.append(link);
  if (warnings.length) {
    const list = document.createElement('small');
    list.className = 'warnings';
    list.textContent = `Not applied — ${warnings.join('; ')}`;
    status.append(list);
  }
  $('save').disabled = true;
  $('save').textContent = 'Saved';
  $('cancel').textContent = 'Done';
}

function setStatus(text, kind = '') {
  $('status').textContent = text;
  $('status').className = `status ${kind}`;
}
