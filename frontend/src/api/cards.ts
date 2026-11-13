import { totalFrom, type Page } from '@/api/paging';
export type CardRelation = {
  id: number;
  fromCardId: number;
  toCardId: number;
  relationType: string;
  properties: Record<string, unknown> | null;
  metadata: Record<string, unknown>;
};

export type ContainedCard = {
  id: number;
  type: string;
  title: string | null;
  previewUrl: string | null;
  contentUrl: string | null;
  isFavorite: boolean;
  position: number;
  playbackUrl?: string | null;
  posterUrl?: string | null;
  /** "image", "gif" or "video"; null when the server does not know. */
  mediaType?: string | null;
};

/** URL to play a contained video (browser-friendly copy when one exists), or null for still media. */
export function containedVideoUrl(card: Pick<ContainedCard, 'mediaType' | 'playbackUrl' | 'contentUrl'>) {
  return card.mediaType === 'video' ? card.playbackUrl || card.contentUrl : null;
}

export type Card = {
  id: number;
  type: string;
  title: string | null;
  previewUrl: string | null;
  contentUrl: string | null;
  playbackUrl?: string | null;
  posterUrl?: string | null;
  properties: Record<string, unknown> | null;
  metadata: Record<string, unknown>;
  isFavorite: boolean;
  outgoingRelations?: CardRelation[];
  incomingRelations?: CardRelation[];
  containedCards?: ContainedCard[];
  deckPosition?: number | null;
  membershipProperties?: Record<string, unknown> | null;
  /** In card lists: the comic or set this media card is a page of. */
  parentCardId?: number | null;
};

export type RelationCard = {
  id: number;
  type: string;
  title: string | null;
  previewUrl: string | null;
};

export type CardRelationEntry = {
  id: number;
  relationType: string;
  direction: 'incoming' | 'outgoing';
  relatedCard: RelationCard;
  properties: Record<string, unknown> | null;
};

export type CardRelations = {
  outgoingRelations: CardRelationEntry[];
  incomingRelations: CardRelationEntry[];
};

export type CardListParams = {
  includeTotal?: boolean;
  type?: string;
  types?: string[];
  mediaType?: string;
  tags?: number[];
  source?: number;
  catalogTags?: number[];
  catalogSource?: number;
  search?: string;
  excludeContainedMedia?: boolean;
  /** Hide pages whose comic or set is in the same list (in a deck: also a member). */
  collapsePages?: boolean;
  deckId?: number;
  sort?: 'created_at' | 'position';
  order?: 'asc' | 'desc';
  limit?: number;
  offset?: number;
};

export type CreateRelationInput = {
  toCardId: number;
  relationType: string;
  properties?: Record<string, string>;
};

export type CreateCardInput = {
  type: string;
  title?: string;
  image?: File;
  images?: File[];
  properties?: Record<string, string>;
  relations?: CreateRelationInput[];
  idempotencyKey?: string;
};

export type UpdateCardInput = {
  title?: string;
  image?: File;
  properties?: Record<string, string>;
  relations?: CreateRelationInput[];
};

export type DailyVote = {
  cardId: number;
  localDate: string;
  value: -1 | 0 | 1 | null;
  timeZone: string;
};

export type CardAnalysis = {
  state: 'pending' | 'leased' | 'done' | 'failed';
  attempts: number;
  availableAtUtc: string | null;
  lastError: string | null;
};

export class CardHttpError extends Error {
  constructor(message: string, public readonly status: number) {
    super(message);
    this.name = 'CardHttpError';
  }
}

const apiBaseUrl = import.meta.env.VITE_API_BASE_URL ?? '';

export const cardTypes = ['media', 'comic', 'set'] as const;
export const relationTypes = [
  'tagged_with',
  'sourced_from',
  'related_to',
] as const;

async function fetchCardList(params: CardListParams) {
  const searchParams = new URLSearchParams();

  if (params.type) searchParams.set('type', params.type);
  if (params.types && params.types.length > 0) searchParams.set('types', params.types.join(','));
  if (params.mediaType) searchParams.set('media_type', params.mediaType);
  if (params.tags && params.tags.length > 0) searchParams.set('tags', params.tags.join(','));
  if (params.source) searchParams.set('source', String(params.source));
  if (params.catalogTags && params.catalogTags.length > 0) searchParams.set('catalog_tags', params.catalogTags.join(','));
  if (params.catalogSource) searchParams.set('catalog_source', String(params.catalogSource));
  if (params.search) searchParams.set('search', params.search);
  if (params.excludeContainedMedia) searchParams.set('exclude_contained_media', 'true');
  if (params.collapsePages) searchParams.set('collapse_pages', 'true');
  if (params.deckId) searchParams.set('deck_id', String(params.deckId));
  if (params.sort) searchParams.set('sort', params.sort);
  if (params.order) searchParams.set('order', params.order);
  if (params.limit) searchParams.set('limit', String(params.limit));
  if (params.offset) searchParams.set('offset', String(params.offset));
  if (params.includeTotal) searchParams.set('include_total', 'true');

  return fetch(`${apiBaseUrl}/api/cards?${searchParams.toString()}`);
}

export async function listCards(params: CardListParams = {}) {
  return readJson<Card[]>(await fetchCardList(params));
}

/** One page of cards (1-based) with the number of all matching cards. */
export async function listCardsPage(params: Omit<CardListParams, 'limit' | 'offset' | 'includeTotal'>,
  page: number, pageSize: number): Promise<Page<Card>> {
  const response = await fetchCardList({
    ...params, limit: pageSize, offset: (Math.max(1, page) - 1) * pageSize, includeTotal: true,
  });
  const items = await readJson<Card[]>(response);
  return { items, total: totalFrom(response, items.length) };
}

export async function getCard(id: number) {
  const response = await fetch(`${apiBaseUrl}/api/cards/${id}`);

  return readJson<Card>(response);
}

export async function getCardAnalysis(id: number): Promise<CardAnalysis | null> {
  const response = await fetch(`${apiBaseUrl}/api/cards/${id}/analysis`);
  if (response.status === 404) return null;
  return readJson<CardAnalysis>(response);
}

export async function recordCardView(id: number, eventId: string, durationMs: number) {
  const response = await fetch(`${apiBaseUrl}/api/cards/${id}/views`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ eventId, durationMs }),
  });

  if (!response.ok) {
    const data = await response.json().catch(() => null);
    throw new Error(data?.error ?? 'Could not record view.');
  }
}

export async function getDailyVote(id: number, timeZone: string) {
  const query = new URLSearchParams({ timeZone });
  const response = await fetch(`${apiBaseUrl}/api/cards/${id}/vote?${query}`);
  return readJson<DailyVote>(response);
}

export async function putDailyVote(id: number, value: -1 | 0 | 1, timeZone: string) {
  const response = await fetch(`${apiBaseUrl}/api/cards/${id}/vote`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ value, timeZone }),
  });
  return readJson<DailyVote>(response);
}

export async function getCardRelations(id: number) {
  const response = await fetch(`${apiBaseUrl}/api/cards/${id}/relations`);

  return readRelationJson<CardRelations>(response);
}

export async function createCardRelation(
  cardId: number,
  input: { toCardId: number; relationType: string; properties?: Record<string, unknown> | null }
) {
  const response = await fetch(`${apiBaseUrl}/api/cards/${cardId}/relations`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });

  return readRelationJson<CardRelationEntry>(response);
}

export async function updateCardRelation(
  relationId: number,
  input: { properties?: Record<string, unknown> | null }
) {
  const response = await fetch(`${apiBaseUrl}/api/card-relations/${relationId}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });

  return readRelationJson<CardRelationEntry>(response);
}

export async function deleteCardRelation(relationId: number) {
  const response = await fetch(`${apiBaseUrl}/api/card-relations/${relationId}`, {
    method: 'DELETE',
  });

  if (!response.ok) {
    const data = await response.json().catch(() => null);
    throw new Error(data?.error ?? 'Request failed.');
  }
}

export async function createCard(input: CreateCardInput, signal?: AbortSignal) {
  const formData = new FormData();
  formData.set('type', input.type);

  if (input.title) {
    formData.set('title', input.title);
  }

  if (input.image) {
    formData.set('image', input.image);
  }

  if (input.images) {
    input.images.forEach((image) => formData.append('images', image));
  }

  if (input.properties && Object.keys(input.properties).length > 0) {
    formData.set('properties', JSON.stringify(input.properties));
  }

  if (input.relations && input.relations.length > 0) {
    formData.set('relations', JSON.stringify(input.relations));
  }

  const response = await fetch(`${apiBaseUrl}/api/cards`, {
    method: 'POST',
    headers: input.idempotencyKey ? { 'Idempotency-Key': input.idempotencyKey } : undefined,
    body: formData,
    signal,
  });

  return readJson<Card>(response);
}

export async function createCollectionFromCards(input: {
  type: 'comic' | 'set';
  title?: string;
  childCardIds: number[];
  idempotencyKey?: string;
}) {
  const response = await fetch(`${apiBaseUrl}/api/cards/collections`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...(input.idempotencyKey ? { 'Idempotency-Key': input.idempotencyKey } : {}) },
    body: JSON.stringify({ type: input.type, title: input.title, childCardIds: input.childCardIds }),
  });
  return readJson<Card>(response);
}

export async function getCardCreationRequest(scope: 'media' | 'collection', key: string, signal?: AbortSignal) {
  const response = await fetch(`${apiBaseUrl}/api/cards/creation-requests/${scope}/${encodeURIComponent(key)}`, { signal });
  return readJson<Card>(response);
}

export type DuplicateMatch = { sha256: string; cardIds: number[] };

export async function findDuplicatesByHash(sha256: string[], signal?: AbortSignal): Promise<DuplicateMatch[]> {
  const matches: DuplicateMatch[] = [];
  for (let start = 0; start < sha256.length; start += 500) {
    const response = await fetch(`${apiBaseUrl}/api/cards/duplicates`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sha256: sha256.slice(start, start + 500) }),
      signal,
    });
    const data = await response.json().catch(() => null);
    if (!response.ok) throw new CardHttpError(data?.error ?? 'Request failed.', response.status);
    if (Array.isArray(data?.matches)) matches.push(...(data.matches as DuplicateMatch[]));
  }
  return matches;
}

export async function moveCardChild(
  parentId: number,
  childId: number,
  targetParentId: number,
  position: number | null,
) {
  const response = await fetch(`${apiBaseUrl}/api/cards/${parentId}/children/${childId}/move`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ targetParentId, position }),
  });
  if (!response.ok) {
    const data = await response.json().catch(() => null);
    throw new CardHttpError(data?.error ?? 'Request failed.', response.status);
  }
}

export async function updateCollectionChildren(id: number, childCardIds: number[]) {
  const response = await fetch(`${apiBaseUrl}/api/cards/${id}/children`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ childCardIds }),
  });
  return readJson<Card>(response);
}

export async function updateCard(id: number, input: UpdateCardInput) {
  const formData = new FormData();

  if (input.title) {
    formData.set('title', input.title);
  }

  if (input.image) {
    formData.set('image', input.image);
  }

  if (input.properties && Object.keys(input.properties).length > 0) {
    formData.set('properties', JSON.stringify(input.properties));
  }

  formData.set('relations', JSON.stringify(input.relations ?? []));

  const response = await fetch(`${apiBaseUrl}/api/cards/${id}`, {
    method: 'PUT',
    body: formData,
  });

  return readJson<Card>(response);
}

export async function updateCardPreview(id: number, startSeconds: number | null) {
  const response = await fetch(`${apiBaseUrl}/api/cards/${id}/preview`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ startSeconds }),
  });

  return readJson<Card>(response);
}

export async function deleteCard(id: number, deleteContents = false) {
  const query = deleteContents ? '?delete_contents=true' : '';
  const response = await fetch(`${apiBaseUrl}/api/cards/${id}${query}`, {
    method: 'DELETE',
  });

  if (!response.ok) {
    const data = await response.json().catch(() => null);
    throw new Error(data?.error ?? 'Request failed.');
  }
}

export type CardDeletionImpact = {
  parentOnly: { cardCount: number; deckMembershipCount: number };
  withContents: { cardCount: number; deckMembershipCount: number };
};

export async function getCardDeletionImpact(id: number) {
  const response = await fetch(`${apiBaseUrl}/api/cards/${id}/deletion-impact`);
  return readJson<CardDeletionImpact>(response);
}

export async function addFavorite(cardId: number) {
  const response = await fetch(`${apiBaseUrl}/api/favorites/${cardId}`, {
    method: 'PUT',
  });

  return readJson<unknown>(response);
}

export async function removeFavorite(cardId: number) {
  const response = await fetch(`${apiBaseUrl}/api/favorites/${cardId}`, {
    method: 'DELETE',
  });

  return readJson<unknown>(response);
}

export function resolveApiUrl(path: string | null | undefined) {
  if (!path) {
    return '';
  }

  if (path.startsWith('http://') || path.startsWith('https://')) {
    return path;
  }

  return `${apiBaseUrl}${path}`;
}

async function readJson<T>(response: Response): Promise<T> {
  const data = await response.json().catch(() => null);

  if (!response.ok) {
    throw new CardHttpError(data?.error ?? 'Request failed.', response.status);
  }

  return normalizeCardData(data) as T;
}

async function readRelationJson<T>(response: Response): Promise<T> {
  const data = await response.json().catch(() => null);

  if (!response.ok) {
    throw new Error(data?.error ?? 'Request failed.');
  }

  return normalizeRelationData(data) as T;
}

function normalizeCardData(data: unknown): unknown {
  if (Array.isArray(data)) {
    return data.map(normalizeCardData);
  }

  if (!data || typeof data !== 'object') {
    return data;
  }

  const record = data as Record<string, unknown>;

  if ('Position' in record) {
    return {
      id: record.Id,
      type: record.Type,
      title: record.Title,
      previewUrl: record.PreviewUrl,
      contentUrl: record.ContentUrl,
      isFavorite: Boolean(record.IsFavorite),
      position: record.Position,
      playbackUrl: record.PlaybackUrl ?? null,
      posterUrl: record.PosterUrl ?? null,
      mediaType: record.MediaType ?? null,
    };
  }

  if ('Id' in record || 'PreviewUrl' in record || 'OutgoingRelations' in record) {
    return {
      id: record.Id,
      type: record.Type,
      title: record.Title,
      previewUrl: record.PreviewUrl,
      contentUrl: record.ContentUrl,
      playbackUrl: record.PlaybackUrl,
      posterUrl: record.PosterUrl,
      properties: record.Properties,
      metadata: record.Metadata,
      isFavorite: Boolean(record.IsFavorite),
      outgoingRelations: normalizeCardData(record.OutgoingRelations),
      incomingRelations: normalizeCardData(record.IncomingRelations),
      containedCards: normalizeCardData(record.ContainedCards),
    };
  }

  if ('RelationType' in record) {
    return {
      id: record.Id,
      fromCardId: record.FromCardId,
      toCardId: record.ToCardId,
      relationType: record.RelationType,
      properties: record.Properties,
      metadata: record.Metadata,
    };
  }

  return data;
}

function normalizeRelationData(data: unknown): unknown {
  if (Array.isArray(data)) {
    return data.map(normalizeRelationData);
  }

  if (!data || typeof data !== 'object') {
    return data;
  }

  const record = data as Record<string, unknown>;

  if ('OutgoingRelations' in record && 'IncomingRelations' in record) {
    return {
      outgoingRelations: normalizeRelationData(record.OutgoingRelations),
      incomingRelations: normalizeRelationData(record.IncomingRelations),
    };
  }

  if ('RelatedCard' in record) {
    const relatedCard = record.RelatedCard as Record<string, unknown>;

    return {
      id: record.Id,
      relationType: record.RelationType,
      direction: String(record.Direction).toLowerCase() as 'incoming' | 'outgoing',
      relatedCard: {
        id: relatedCard.Id,
        type: relatedCard.Type,
        title: relatedCard.Title,
        previewUrl: relatedCard.PreviewUrl,
      },
      properties: record.Properties,
    };
  }

  return data;
}
