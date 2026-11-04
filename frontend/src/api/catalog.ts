export type Tag = {
  id: number;
  name: string;
  category: string;
  origin: string;
  nameConflict: boolean;
  aliases: string[];
  referenceCount: number;
};

export type Source = {
  id: number;
  name: string;
  isLegacy: boolean;
  nameConflict: boolean;
  referenceCount: number;
};

export type CardTag = Pick<Tag, 'id' | 'name' | 'category' | 'origin'>;
export type CardSource = Pick<Source, 'id' | 'name'> & { originalUrl: string | null };

const baseUrl = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:8080';

async function request<T>(path: string, options?: RequestInit): Promise<T> {
  const response = await fetch(`${baseUrl}${path}`, options);
  if (response.status === 204) return undefined as T;
  const data = await response.json().catch(() => null);
  if (!response.ok) throw new Error(data?.error ?? 'Request failed.');
  return data as T;
}

function json(body: unknown, method: string): RequestInit {
  return { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) };
}

export const listTags = (search = '') =>
  request<Tag[]>(`/api/tags${search ? `?search=${encodeURIComponent(search)}` : ''}`);
export const createTag = (name: string, aliases: string[] = []) =>
  request<Tag>('/api/tags', json({ name, aliases }, 'POST'));
export const updateTag = (id: number, name: string, aliases: string[]) =>
  request<Tag>(`/api/tags/${id}`, json({ name, aliases }, 'PUT'));
export const deleteTag = (id: number) =>
  request<void>(`/api/tags/${id}`, { method: 'DELETE' });

export const listSources = (search = '') =>
  request<Source[]>(`/api/sources${search ? `?search=${encodeURIComponent(search)}` : ''}`);
export const createSource = (name: string) =>
  request<Source>('/api/sources', json({ name }, 'POST'));
export const updateSource = (id: number, name: string) =>
  request<Source>(`/api/sources/${id}`, json({ name }, 'PUT'));
export const deleteSource = (id: number) =>
  request<void>(`/api/sources/${id}`, { method: 'DELETE' });

export const listCardTags = (cardId: number) => request<CardTag[]>(`/api/cards/${cardId}/tags`);
export const setCardTag = (cardId: number, tagId: number) =>
  request<void>(`/api/cards/${cardId}/tags/${tagId}`, { method: 'PUT' });
export const deleteCardTag = (cardId: number, tagId: number) =>
  request<void>(`/api/cards/${cardId}/tags/${tagId}`, { method: 'DELETE' });

export const listCardSources = (cardId: number) =>
  request<CardSource[]>(`/api/cards/${cardId}/sources`);
export const setCardSource = (cardId: number, sourceId: number, originalUrl: string) =>
  request<void>(`/api/cards/${cardId}/sources/${sourceId}`,
    json({ originalUrl: originalUrl.trim() || null }, 'PUT'));
export const deleteCardSource = (cardId: number, sourceId: number) =>
  request<void>(`/api/cards/${cardId}/sources/${sourceId}`, { method: 'DELETE' });
