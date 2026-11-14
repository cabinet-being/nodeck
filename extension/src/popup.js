// Toolbar popup: the save list of the current tab. The background keeps it (and keeps saving) when this closes.
import { ext } from './ext.js';
import { getServerUrl } from './api.js';

const $ = (id) => document.getElementById(id);
const CATEGORIES = ['artist', 'character', 'copyright', 'general', 'meta'];
const params = new URLSearchParams(location.search);
let tabId = Number(params.get('tab')) || null;
let state = null;
let serverUrl = '';
let elapsedTimer = null;
/** Preview images by item key, reused between renders so they do not reload on every progress update. */
const previews = new Map();

if (params.get('window')) document.body.classList.add('window');
init().catch((error) => showError(error.message ?? String(error)));

async function init() {
  if (!tabId) [{ id: tabId }] = await ext.tabs.query({ active: true, currentWindow: true });
  serverUrl = await getServerUrl();
  wire();
  ext.storage.onChanged.addListener((changes, area) => {
    const change = changes[`tab:${tabId}`];
    if (area === 'session' && change?.newValue) { state = change.newValue; render(); }
  });
  state = await send('open');
  render();
}

async function send(type, extra = {}) {
  const answer = await ext.runtime.sendMessage({ type, tabId, ...extra });
  if (!answer?.ok) throw new Error(answer?.error ?? 'The extension did not answer.');
  return answer.result;
}

function showError(message) {
  $('loading').textContent = message;
  $('loading').className = 'notice warn';
}

const busy = () => state?.job?.status === 'running';
const locked = () => busy() || state?.job?.status === 'done';

/** Sends the popup's edits to the background, which stores them for this tab. */
async function update(changes) {
  if (locked()) return;
  Object.assign(state, changes);
  render();
  state = await send('update', { changes });
}

// ---------- rendering ----------

function render() {
  if (!state) return;
  $('loading').hidden = true;
  $('main').hidden = false;
  $('page').textContent = hostOf(state.pageUrl);
  $('page').title = state.pageUrl;
  const notes = [state.error, state.warning].filter(Boolean).join(' ');
  $('warning').hidden = !notes;
  $('warning').textContent = notes;
  setValue('title', state.title);
  setValue('sourceName', state.source?.name ?? '');
  setValue('sourceUrl', /^https?:/.test(state.source?.url ?? '') ? state.source.url : '');
  for (const input of document.querySelectorAll('input[name="mode"]')) input.checked = input.value === state.mode;
  $('modeHint').textContent = state.mode === 'cards' ? 'Each file becomes its own card.'
    : state.mode === 'comic' ? 'Pages in the order shown.' : 'A group, in the order shown.';
  for (const el of document.querySelectorAll('.form input, .media-bar button')) el.disabled = locked();
  renderTags();
  renderTiles();
  renderProgress();
}

function setValue(id, value) {
  const input = $(id);
  if (document.activeElement !== input && input.value !== value) input.value = value ?? '';
}

function hostOf(url) {
  try { return new URL(url).hostname.replace(/^www\./, ''); } catch { return ''; }
}

function renderTags() {
  $('tags').replaceChildren(...state.tags.map((tag, index) => {
    const chip = document.createElement('span');
    chip.className = `chip ${tag.category}`;
    chip.title = tag.category;
    const label = document.createElement('span');
    label.textContent = tag.category === 'custom' || tag.category === 'general' ? tag.name : `${tag.category}: ${tag.name}`;
    const remove = document.createElement('button');
    remove.type = 'button';
    remove.textContent = '×';
    remove.disabled = locked();
    remove.setAttribute('aria-label', `Remove tag ${tag.name}`);
    remove.addEventListener('click', () => update({ tags: state.tags.filter((_, i) => i !== index) }));
    chip.append(label, remove);
    return chip;
  }));
  $('tagCount').textContent = state.tags.length ? `(${state.tags.length})` : '— none found';
}

function renderTiles() {
  const order = state.items.filter((item) => item.selected);
  const saveable = state.items.filter((item) => item.url);
  $('count').textContent = `${order.length} of ${saveable.length} chosen`;
  $('tiles').replaceChildren(...state.items.map((item, index) => tile(item, index, order.indexOf(item) + 1)));
}

function tile(item, index, position) {
  const li = document.createElement('li');
  const unsupported = !item.url;
  li.className = `tile ${unsupported ? 'unsupported' : item.selected ? 'selected' : 'unselected'}`;
  li.draggable = !locked() && !unsupported;
  const pick = document.createElement('button');
  pick.type = 'button';
  pick.className = 'pick';
  pick.disabled = unsupported || locked();
  pick.title = unsupported ? item.message : item.selected ? 'Chosen — click to leave out' : 'Click to choose';
  pick.setAttribute('aria-pressed', String(!!item.selected));
  pick.setAttribute('aria-label', `${item.mediaType === 'video' ? 'Video' : 'Image'} ${index + 1}`);
  if (item.thumbUrl) {
    let preview = previews.get(item.key);
    if (!preview) {
      const img = document.createElement('img');
      img.src = item.thumbUrl;
      img.alt = '';
      img.loading = 'lazy';
      img.draggable = false;
      img.addEventListener('error', () => {
        const ph = placeholder(item);
        img.replaceWith(ph);
        previews.set(item.key, ph);
      });
      preview = img;
      previews.set(item.key, preview);
    }
    pick.append(preview);
  } else {
    pick.append(placeholder(item));
  }
  pick.addEventListener('click', () => {
    item.selected = !item.selected;
    update({ items: state.items });
  });
  li.append(pick);
  if (item.selected) {
    const badge = document.createElement('span');
    badge.className = 'badge';
    badge.textContent = String(position);
    li.append(badge);
  }
  if (item.mediaType === 'video') {
    const kind = document.createElement('span');
    kind.className = 'kind';
    kind.textContent = '▶ video';
    li.append(kind);
  }
  li.append(...itemStatus(item));
  if (!locked() && !unsupported) {
    const row = document.createElement('div');
    row.className = 'row';
    row.append(
      smallButton('←', `Move ${index + 1} earlier`, index === 0, () => moveTo(index, index - 1)),
      smallButton('→', `Move ${index + 1} later`, index === state.items.length - 1, () => moveTo(index, index + 1)),
    );
    li.append(row);
  }
  li.addEventListener('dragstart', (event) => {
    event.dataTransfer.setData('text/plain', String(index));
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

function placeholder(item) {
  const span = document.createElement('span');
  span.className = 'ph';
  span.textContent = item.mediaType === 'video' ? '▶ video' : hostOf(item.url ?? '') || 'image';
  return span;
}

const LABELS = {
  preparing: 'Preparing…',
  importing: 'nodeck is downloading…',
  downloading: 'Downloading…',
  uploading: 'Sending to nodeck…',
  tagging: 'Adding tags…',
};

function itemStatus(item) {
  const nodes = [];
  const text = document.createElement('small');
  if (!item.url) {
    text.textContent = item.message ?? 'Cannot be saved';
    text.className = 'failed';
    return [text];
  }
  if (item.status === 'done') {
    text.className = 'done';
    const link = document.createElement('a');
    link.href = cardUrl(item.cardId);
    link.target = '_blank';
    link.textContent = item.note ? `✓ Saved (${item.note})` : '✓ Saved';
    if (item.note) link.title = 'The full-size file could not be downloaded, so the copy shown on the page was saved.';
    text.append(link);
  } else if (item.status === 'failed') {
    text.className = 'failed';
    text.textContent = `✗ ${item.message ?? 'Failed'}`;
  } else if (LABELS[item.status]) {
    const { loaded = 0, total = 0 } = item.progress ?? {};
    text.textContent = item.status === 'downloading' && loaded
      ? `Downloading ${total ? `${Math.round((loaded / total) * 100)}%` : formatSize(loaded)}`
      : LABELS[item.status];
    const bar = document.createElement('div');
    bar.className = `bar-mini${total ? '' : ' busy'}`;
    const fill = document.createElement('div');
    if (total) fill.style.width = `${Math.round((loaded / total) * 100)}%`;
    bar.append(fill);
    nodes.push(bar);
  } else if (item.width) {
    text.textContent = `${item.width}×${item.height}`;
  }
  nodes.unshift(text);
  if (item.status === 'failed' && !busy()) {
    const retry = document.createElement('button');
    retry.type = 'button';
    retry.className = 'retry';
    retry.textContent = 'Retry';
    retry.addEventListener('click', async () => { state = await send('retry', { key: item.key }); render(); });
    nodes.push(retry);
  }
  return nodes;
}

function smallButton(text, label, disabled, onClick) {
  const button = document.createElement('button');
  button.type = 'button';
  button.textContent = text;
  button.title = label;
  button.setAttribute('aria-label', label);
  button.disabled = disabled;
  button.addEventListener('click', onClick);
  return button;
}

function moveTo(from, to) {
  if (locked() || to < 0 || to >= state.items.length || from === to) return;
  const items = [...state.items];
  const [moved] = items.splice(from, 1);
  items.splice(to, 0, moved);
  update({ items });
}

function renderProgress() {
  const chosen = state.items.filter((item) => item.selected && item.url);
  const job = state.job ?? { status: 'idle' };
  const save = $('save');
  const word = state.mode === 'cards' ? (chosen.length === 1 ? 'as a card' : 'as cards') : `as a ${state.mode}`;
  save.disabled = chosen.length === 0 || busy() || job.status === 'done';
  save.textContent = busy() ? 'Saving…' : job.status === 'done' ? 'Saved'
    : job.status === 'failed' ? `Save again (${chosen.length})` : `Save ${chosen.length} ${word}`;

  const done = chosen.filter((item) => item.status === 'done').length;
  const partial = chosen.reduce((sum, item) => sum + (item.progress?.total ? item.progress.loaded / item.progress.total : 0), 0);
  const steps = chosen.length + (state.mode === 'cards' ? 0 : 1);
  const finished = done + (job.status === 'done' && state.mode !== 'cards' ? 1 : 0);
  $('progress').hidden = job.status === 'idle';
  $('progress').classList.toggle('busy', busy() && finished === 0 && partial === 0);
  $('progressBar').style.width = `${Math.round(((finished + partial) / Math.max(steps, 1)) * 100)}%`;

  const status = $('status');
  status.className = `status ${job.status === 'failed' ? 'error' : job.status === 'done' ? 'ok' : ''}`;
  status.replaceChildren();
  clearInterval(elapsedTimer);
  if (busy()) {
    const line = () => `${job.step ?? `Saving ${done} of ${chosen.length}…`} ${Math.round((Date.now() - job.startedAt) / 1000)} s`;
    status.textContent = line();
    elapsedTimer = setInterval(() => { status.textContent = line(); }, 1000);
  } else if (job.status === 'done') {
    const ids = job.cardIds ?? [];
    status.append(job.collectionId
      ? `Saved the ${state.mode} with ${ids.length} page${ids.length === 1 ? '' : 's'}. `
      : `Saved ${ids.length} card${ids.length === 1 ? '' : 's'}. `);
    const links = document.createElement('div');
    links.className = 'links';
    if (job.collectionId) links.append(link(cardUrl(job.collectionId), `Open the ${state.mode} ↗`, 'cardLink'));
    else ids.slice(0, 12).forEach((id, i) => links.append(link(cardUrl(id), ids.length === 1 ? 'Open in nodeck ↗' : `#${i + 1} ↗`, i ? null : 'cardLink')));
    const again = document.createElement('button');
    again.type = 'button';
    again.className = 'retry';
    again.textContent = 'Save more from this page';
    again.addEventListener('click', async () => { state = await send('reset'); render(); });
    links.append(again);
    status.append(links);
    const warnings = [...(job.catalogWarnings ?? []), ...chosen.flatMap((item) => item.catalogWarnings ?? [])];
    if (warnings.length) {
      const small = document.createElement('small');
      small.className = 'warnings';
      small.textContent = `Not applied — ${[...new Set(warnings)].join('; ')}`;
      status.append(small);
    }
  } else if (job.status === 'failed') {
    status.textContent = job.error ?? 'Not saved.';
  }
}

function link(href, text, id) {
  const a = document.createElement('a');
  a.href = href;
  a.target = '_blank';
  a.rel = 'noopener';
  a.textContent = text;
  if (id) a.id = id;
  return a;
}

const cardUrl = (id) => `${state.job?.serverUrl ?? serverUrl}/cards/${id}`;

function formatSize(bytes) {
  return bytes > 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.ceil(bytes / 1024)} KB`;
}

// ---------- editing ----------

function wire() {
  for (const input of document.querySelectorAll('input[name="mode"]'))
    input.addEventListener('change', () => update({ mode: input.value }));
  $('title').addEventListener('change', (event) => update({ title: event.target.value.trim() }));
  const source = () => update({ source: { name: $('sourceName').value.trim(), url: $('sourceUrl').value.trim() } });
  $('sourceName').addEventListener('change', source);
  $('sourceUrl').addEventListener('change', source);
  $('tagInput').addEventListener('keydown', (event) => {
    if (event.key !== 'Enter' && event.key !== ',') return;
    event.preventDefault();
    addTag(event.target.value);
    event.target.value = '';
  });
  $('tagInput').addEventListener('blur', (event) => {
    if (event.target.value.trim()) { addTag(event.target.value); event.target.value = ''; }
  });
  $('all').addEventListener('click', () => update({ items: state.items.map((item) => ({ ...item, selected: !!item.url })) }));
  $('none').addEventListener('click', () => update({ items: state.items.map((item) => ({ ...item, selected: false })) }));
  $('reverse').addEventListener('click', () => update({ items: [...state.items].reverse() }));
  $('rescan').addEventListener('click', async () => {
    $('loading').hidden = false;
    $('loading').textContent = 'Reading the page…';
    state = await send('rescan');
    render();
  });
  $('save').addEventListener('click', async () => {
    // Edits typed but not yet confirmed with Enter or leaving the field.
    await update({
      title: $('title').value.trim(),
      source: { name: $('sourceName').value.trim(), url: $('sourceUrl').value.trim() },
    });
    if ($('tagInput').value.trim()) { await addTag($('tagInput').value); $('tagInput').value = ''; }
    state = await send('save');
    render();
  });
}

function addTag(text) {
  const match = text.match(/^\s*(\w+)\s*:\s*(.+)$/);
  const category = match && CATEGORIES.includes(match[1].toLowerCase()) ? match[1].toLowerCase() : 'custom';
  const name = (category === 'custom' ? text : match[2]).replace(/\s+/g, ' ').trim();
  if (!name || state.tags.some((tag) => tag.name.toLowerCase() === name.toLowerCase())) return Promise.resolve();
  return update({ tags: [...state.tags, { name, category }] });
}
