import { CardHttpError } from '@/api/cards';

const apiBaseUrl = import.meta.env.VITE_API_BASE_URL ?? '';

export type DuplicateKind = 'all' | 'exact' | 'similar';

export type DuplicateCard = {
  id: number;
  title: string | null;
  mediaType: string | null;
  mimeType: string | null;
  width: number | null;
  height: number | null;
  fileSize: number | null;
  duration: number | null;
  createdAt: string | null;
  originalFileName: string | null;
  contentSha256: string | null;
  tagCount: number;
  sourceCount: number;
  decks: string[];
  isFavorite: boolean;
  viewCount: number;
  parentId: number | null;
  parentTitle: string | null;
  parentType: string | null;
};

export type DuplicateGroup = {
  key: string;
  kind: 'exact' | 'similar';
  maxDistance: number | null;
  suggestedSurvivorId: number;
  conflict: string | null;
  cards: DuplicateCard[];
};

export type DuplicateGroupPage = {
  groups: DuplicateGroup[];
  total: number;
  exactGroups: number;
  similarGroups: number;
  exactRedundantCards: number;
  pendingHashes: number;
};

export type MergeResult = { survivorId: number; mergedCardIds: number[]; skippedCardIds: number[] };

async function readJson<T>(response: Response): Promise<T> {
  const data = await response.json().catch(() => null);
  if (!response.ok) throw new CardHttpError(data?.error ?? 'Request failed.', response.status);
  return data as T;
}

export async function listDuplicateGroups(kind: DuplicateKind, limit: number, offset: number, signal?: AbortSignal) {
  const query = new URLSearchParams({ kind, limit: String(limit), offset: String(offset) });
  return readJson<DuplicateGroupPage>(await fetch(`${apiBaseUrl}/api/duplicates?${query}`, { signal }));
}

export async function mergeDuplicates(survivorId: number, cardIds: number[]) {
  return readJson<MergeResult>(await fetch(`${apiBaseUrl}/api/duplicates/merge`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ survivorId, cardIds }),
  }));
}

export async function dismissDuplicates(cardIds: number[]) {
  const response = await fetch(`${apiBaseUrl}/api/duplicates/dismiss`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ cardIds }),
  });
  if (!response.ok) {
    const data = await response.json().catch(() => null);
    throw new CardHttpError(data?.error ?? 'Request failed.', response.status);
  }
}
