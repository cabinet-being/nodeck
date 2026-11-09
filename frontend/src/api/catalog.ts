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
  assignmentSource: 'manual' | 'auto' | 'confirmed';
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

export const listTags = (search = '') =>
  request<Tag[]>(`/api/tags${search ? `?search=${encodeURIComponent(search)}` : ''}`);
export const getTag = (id: number) => request<Tag>(`/api/tags/${id}`);
export const createTag = (name: string, aliases: string[] = []) =>
  request<Tag>('/api/tags', json({ name, aliases }, 'POST'));
export const updateTag = (id: number, name: string, aliases: string[]) =>
  request<Tag>(`/api/tags/${id}`, json({ name, aliases }, 'PUT'));
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
export const setCardTag = (cardId: number, tagId: number) =>
  request<void>(`/api/cards/${cardId}/tags/${tagId}`, { method: 'PUT' });
export const deleteCardTag = (cardId: number, tagId: number) =>
  request<void>(`/api/cards/${cardId}/tags/${tagId}`, { method: 'DELETE' });
export const listTagSuggestions = (cardId: number) =>
  request<TagSuggestion[]>(`/api/cards/${cardId}/tag-suggestions`);
export const decideTagSuggestion = (cardId: number, tagId: number, decision: 'accepted' | 'rejected') =>
  request<void>(`/api/cards/${cardId}/tag-suggestions/${tagId}/decision`, json({ decision }, 'PUT'));
export const decideTagSuggestions = (cardId: number, decision: 'accepted' | 'rejected', tagIds?: number[]) =>
  request<{ count: number }>(`/api/cards/${cardId}/tag-suggestions/decision`,
    json({ decision, tagIds: tagIds ?? null }, 'PUT'));

export const listCardSources = (cardId: number) =>
  request<CardSource[]>(`/api/cards/${cardId}/sources`);
export const setCardSource = (cardId: number, sourceId: number, originalUrl: string) =>
  request<void>(`/api/cards/${cardId}/sources/${sourceId}`,
    json({ originalUrl: originalUrl.trim() || null }, 'PUT'));
export const deleteCardSource = (cardId: number, sourceId: number) =>
  request<void>(`/api/cards/${cardId}/sources/${sourceId}`, { method: 'DELETE' });
