// Downloads a file in the browser (when nodeck cannot fetch it itself) and makes it uploadable.
import { ext } from './ext.js';

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

/** Downloads item.url (or its fallback) with progress, then checks or converts the format. */
export async function prepareFile(item, state, onProgress) {
  let lastError = null;
  for (const url of [item.url, item.fallbackUrl].filter(Boolean)) {
    const ways = url.startsWith('blob:') ? [viaPage] : [direct, viaPage];
    for (const way of ways) {
      try {
        const blob = await way(url, state, onProgress);
        return { ...await normalize(blob, url), usedFallback: url !== item.url };
      } catch (error) { lastError = error; }
    }
  }
  throw lastError ?? new Error('Could not download the file.');
}

/** The extension's own request: cookies of the site are sent, and the page is set as Referer by a session rule. */
async function direct(url, state, onProgress) {
  const response = await fetch(url, { credentials: 'include' });
  if (!response.ok) throw new Error(`The site answered ${response.status}.`);
  const total = Number(response.headers.get('Content-Length')) || 0;
  if (!response.body) return await response.blob();
  const reader = response.body.getReader();
  const chunks = [];
  let loaded = 0;
  onProgress({ loaded, total });
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    chunks.push(value);
    loaded += value.length;
    onProgress({ loaded, total });
  }
  return new Blob(chunks, { type: response.headers.get('Content-Type') ?? '' });
}

/** Through the page itself (its cookies and origin); also the only way to read blob: URLs. */
async function viaPage(url, state, onProgress) {
  onProgress({ loaded: 0, total: 0 });
  const [result] = await ext.scripting.executeScript({
    target: { tabId: state.tabId, frameIds: [0] },
    func: (fileUrl) => globalThis.nodeckPage?.fetchAsDataUrl(fileUrl),
    args: [url],
  });
  if (!result?.result) throw new Error('The page could not download the file.');
  return await (await fetch(result.result)).blob();
}

/** nodeck accepts JPEG, PNG, WebP, GIF, MP4, WebM and MOV; other images (AVIF, BMP, SVG…) become PNG. */
async function normalize(blob, url) {
  if (blob.size === 0) throw new Error('The file is empty.');
  const head = new Uint8Array(await blob.slice(0, 16).arrayBuffer());
  let kind = SIGNATURES.find((signature) => signature.test(head));
  let file = kind ? new Blob([blob], { type: kind.type }) : null;
  if (!kind) {
    let bitmap;
    try { bitmap = await createImageBitmap(blob); } catch {
      throw new Error(`Unsupported file type (${blob.type || 'unknown'}).`);
    }
    const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
    canvas.getContext('2d').drawImage(bitmap, 0, 0);
    bitmap.close();
    file = await canvas.convertToBlob({ type: 'image/png' });
    kind = SIGNATURES[1];
  }
  return { blob: file, type: kind.type, fileName: fileName(url, kind.ext) };
}

function fileName(url, extension) {
  let base = 'file';
  try {
    const last = decodeURIComponent(new URL(url).pathname.split('/').filter(Boolean).pop() ?? '');
    base = last.replace(/\.[a-z0-9]{2,5}$/i, '').replace(/[^\p{L}\p{N}._ -]+/gu, '_').slice(0, 80) || base;
  } catch { /* keep default */ }
  return `${base}.${extension}`;
}
