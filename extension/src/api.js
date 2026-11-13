// Small client for the nodeck API and the extension's settings.
import { ext } from './ext.js';

export const FALLBACK_SERVER_URL = 'http://localhost:5080';

export function normalizeServerUrl(value) {
  let text = String(value ?? '').trim();
  if (!text) return '';
  if (!/^https?:\/\//i.test(text)) text = `http://${text}`;
  const url = new URL(text);
  return `${url.origin}${url.pathname.replace(/\/+$/, '')}`;
}

/** The server URL from the settings; otherwise the one nodeck wrote into the package it served. */
export async function getServerUrl() {
  const { serverUrl } = await ext.storage.local.get('serverUrl');
  if (serverUrl) return serverUrl;
  return (await packagedDefaults()).serverUrl ?? FALLBACK_SERVER_URL;
}

export async function packagedDefaults() {
  try {
    const response = await fetch(ext.runtime.getURL('defaults.json'));
    if (response.ok) {
      const data = await response.json();
      return { serverUrl: data.serverUrl ? normalizeServerUrl(data.serverUrl) : null };
    }
  } catch { /* not packaged by nodeck */ }
  return { serverUrl: null };
}

export class Nodeck {
  constructor(baseUrl) {
    this.baseUrl = normalizeServerUrl(baseUrl);
  }

  static async fromSettings() {
    return new Nodeck(await getServerUrl());
  }

  cardUrl(id) {
    return `${this.baseUrl}/cards/${id}`;
  }

  async request(path, options = {}) {
    let response;
    try {
      response = await fetch(`${this.baseUrl}${path}`, options);
    } catch (error) {
      throw new Error(`Cannot reach nodeck at ${this.baseUrl} (${error.message}).`);
    }
    if (response.status === 204) return undefined;
    const data = await response.json().catch(() => null);
    if (!response.ok) {
      const error = new Error(data?.error ?? `nodeck answered ${response.status}.`);
      error.status = response.status;
      throw error;
    }
    return data;
  }

  json(method, body, headers = {}) {
    return { method, headers: { 'Content-Type': 'application/json', ...headers }, body: JSON.stringify(body) };
  }

  health() {
    return this.request('/health');
  }

  uploadMedia(blob, fileName, title, idempotencyKey) {
    const form = new FormData();
    form.set('type', 'media');
    if (title) form.set('title', title);
    form.set('file', blob, fileName);
    return this.request('/api/cards', { method: 'POST', headers: { 'Idempotency-Key': idempotencyKey }, body: form });
  }

  createCollection(type, title, childCardIds, idempotencyKey) {
    return this.request('/api/cards/collections', this.json('POST',
      { type, title: title || null, childCardIds, moveFromOtherCollections: false },
      { 'Idempotency-Key': idempotencyKey }));
  }

  /** Finds a tag by name or alias (also with "_" and spaces swapped); creates it if missing. */
  async ensureTag({ name, category }) {
    const variants = [...new Set([name, name.replace(/_/g, ' '), name.replace(/ /g, '_')])];
    const find = async () => {
      for (const variant of variants) {
        const key = variant.toLowerCase();
        const found = (await this.request(`/api/tags?search=${encodeURIComponent(variant)}`))
          .find((tag) => tag.name.toLowerCase() === key || tag.aliases?.some((alias) => alias.toLowerCase() === key));
        if (found) return found;
      }
      return null;
    };
    const existing = await find();
    if (existing) return existing;
    try {
      return await this.request('/api/tags', this.json('POST',
        { name, aliases: [], ...(category && category !== 'custom' ? { category } : {}) }));
    } catch (error) {
      if (error.status === 409) {
        const raced = await find();
        if (raced) return raced;
      }
      throw error;
    }
  }

  async ensureSource(name) {
    const key = name.toLowerCase();
    const find = async () => (await this.request(`/api/sources?search=${encodeURIComponent(name)}`))
      .find((source) => source.name.toLowerCase() === key) ?? null;
    const existing = await find();
    if (existing) return existing;
    try {
      return await this.request('/api/sources', this.json('POST', { name }));
    } catch (error) {
      if (error.status === 409) {
        const raced = await find();
        if (raced) return raced;
      }
      throw error;
    }
  }

  setCardTag(cardId, tagId) {
    return this.request(`/api/cards/${cardId}/tags/${tagId}`, { method: 'PUT' });
  }

  setCardSource(cardId, sourceId, originalUrl) {
    return this.request(`/api/cards/${cardId}/sources/${sourceId}`,
      this.json('PUT', { originalUrl: originalUrl?.trim() || null }));
  }
}
