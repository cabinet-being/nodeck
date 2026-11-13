import { ext } from './ext.js';
import { FALLBACK_SERVER_URL, Nodeck, getServerUrl, normalizeServerUrl, packagedDefaults } from './api.js';

const $ = (id) => document.getElementById(id);

async function init() {
  $('serverUrl').value = await getServerUrl();
  const { serverUrl: packaged } = await packagedDefaults();
  $('defaultHint').textContent = packaged
    ? `This copy was downloaded from ${packaged}; that is the default.`
    : `Default: ${FALLBACK_SERVER_URL}. Enter the address you open nodeck at.`;
  $('saveSettings').addEventListener('click', async () => {
    let url;
    try {
      url = normalizeServerUrl($('serverUrl').value);
    } catch {
      return show('That is not a valid address.', 'error');
    }
    if (!url) {
      await ext.storage.local.remove('serverUrl');
      $('serverUrl').value = await getServerUrl();
    } else {
      await ext.storage.local.set({ serverUrl: url });
      $('serverUrl').value = url;
    }
    await test();
  });
  $('test').addEventListener('click', test);
}

async function test() {
  let api;
  try {
    api = new Nodeck($('serverUrl').value);
  } catch {
    return show('That is not a valid address.', 'error');
  }
  show(`Connecting to ${api.baseUrl}…`);
  try {
    const health = await api.health();
    if (health?.status !== 'ok') throw new Error('Unexpected answer.');
    await api.request('/api/sources?limit=1');
    show(`Connected to nodeck ${health.version ?? ''} at ${api.baseUrl}.`, 'ok');
  } catch (error) {
    show(`Not connected: ${error.message}`, 'error');
  }
}

function show(text, kind = '') {
  $('status').textContent = text;
  $('status').className = `status ${kind}`;
}

init();
