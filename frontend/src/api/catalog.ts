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

export type CardTag = Pick<Tag, 'id' | 'name' | 'category' | 'origin'> & {
  assignmentSource: 'manual' | 'auto' | 'confirmed' | 'system';
};
export type TagSuggestion = {
  tagId: number;
  name: string;
  category: string;
  origin: string;
  confidence: number;
  modelKey: string | null;
};
export type CardSource = Pick<Source, 'id' | 'name'> & { originalUrl: string | null };

const baseUrl = import.meta.env.VITE_API_BASE_URL ?? '';

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

/** Most used tags first; without a search only tags in use are listed (up to 200). */
export type TagCategoryFilter = 'general' | 'artist' | 'character' | 'copyright' | 'meta' | 'custom';

export const listTags = (search = '', sort: 'usage' | 'name' = 'usage', category?: TagCategoryFilter) => {
  const params = new URLSearchParams({ sort });
  if (search) params.set('search', search);
  if (category) params.set('category', category);
  return request<Tag[]>(`/api/tags?${params.toString()}`);
};
export const getTag = (id: number) => request<Tag>(`/api/tags/${id}`);
export const createTag = (name: string, aliases: string[] = [], category?: TagCategoryFilter) =>
  request<Tag>('/api/tags', json({ name, aliases, ...(category ? { category } : {}) }, 'POST'));
export const updateTag = (id: number, name: string, aliases: string[], category?: TagCategoryFilter) =>
  request<Tag>(`/api/tags/${id}`, json({ name, aliases, ...(category ? { category } : {}) }, 'PUT'));
export const deleteTag = (id: number) =>
  request<void>(`/api/tags/${id}`, { method: 'DELETE' });

export const listSources = (search = '') =>
  request<Source[]>(`/api/sources${search ? `?search=${encodeURIComponent(search)}` : ''}`);
export const getSource = (id: number) => request<Source>(`/api/sources/${id}`);
export const createSource = (name: string) =>
  request<Source>('/api/sources', json({ name }, 'POST'));
export const updateSource = (id: number, name: string) =>
  request<Source>(`/api/sources/${id}`, json({ name }, 'PUT'));
export const deleteSource = (id: number) =>
  request<void>(`/api/sources/${id}`, { method: 'DELETE' });

export const listCardTags = (cardId: number) => request<CardTag[]>(`/api/cards/${cardId}/tags`);
export const setCardTag = (cardId: number, tagId: number, signal?: AbortSignal) =>
  request<void>(`/api/cards/${cardId}/tags/${tagId}`, { method: 'PUT', signal });
export const deleteCardTag = (cardId: number, tagId: number) =>
  request<void>(`/api/cards/${cardId}/tags/${tagId}`, { method: 'DELETE' });
export const listTagSuggestions = (cardId: number) =>
  request<TagSuggestion[]>(`/api/cards/${cardId}/tag-suggestions`);
export const decideTagSuggestion = (cardId: number, tagId: number, decision: 'accepted' | 'rejected') =>
  request<void>(`/api/cards/${cardId}/tag-suggestions/${tagId}/decision`, json({ decision }, 'PUT'));
export const correctTagSuggestion = (cardId: number, tagId: number, replacementTagId: number) =>
  request<void>(`/api/cards/${cardId}/tag-suggestions/${tagId}/correction`,
    json({ replacementTagId }, 'PUT'));
export const decideTagSuggestions = (cardId: number, decision: 'accepted' | 'rejected', tagIds?: number[]) =>
  request<{ count: number }>(`/api/cards/${cardId}/tag-suggestions/decision`,
    json({ decision, tagIds: tagIds ?? null }, 'PUT'));

export const listCardSources = (cardId: number) =>
  request<CardSource[]>(`/api/cards/${cardId}/sources`);
export const setCardSource = (cardId: number, sourceId: number, originalUrl: string, signal?: AbortSignal) =>
  request<void>(`/api/cards/${cardId}/sources/${sourceId}`,
    { ...json({ originalUrl: originalUrl.trim() || null }, 'PUT'), signal });
export const deleteCardSource = (cardId: number, sourceId: number) =>
  request<void>(`/api/cards/${cardId}/sources/${sourceId}`, { method: 'DELETE' });
