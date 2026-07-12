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
};

export type Card = {
  id: number;
  type: string;
  title: string | null;
  previewUrl: string | null;
  contentUrl: string | null;
  properties: Record<string, unknown> | null;
  metadata: Record<string, unknown>;
  isFavorite: boolean;
  outgoingRelations?: CardRelation[];
  incomingRelations?: CardRelation[];
  containedCards?: ContainedCard[];
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
  type?: string;
  types?: string[];
  mediaType?: string;
  tags?: number[];
  source?: number;
  search?: string;
  excludeContainedMedia?: boolean;
  sort?: 'created_at';
  order?: 'asc' | 'desc';
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
};

export type UpdateCardInput = {
  title?: string;
  image?: File;
  properties?: Record<string, string>;
  relations?: CreateRelationInput[];
};

const apiBaseUrl = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:8080';

export const cardTypes = ['media', 'comic', 'set', 'tag', 'source'] as const;
export const relationTypes = [
  'tagged_with',
  'sourced_from',
  'related_to',
] as const;

export async function listCards(params: CardListParams = {}) {
  const searchParams = new URLSearchParams();

  if (params.type) searchParams.set('type', params.type);
  if (params.types && params.types.length > 0) searchParams.set('types', params.types.join(','));
  if (params.mediaType) searchParams.set('media_type', params.mediaType);
  if (params.tags && params.tags.length > 0) searchParams.set('tags', params.tags.join(','));
  if (params.source) searchParams.set('source', String(params.source));
  if (params.search) searchParams.set('search', params.search);
  if (params.excludeContainedMedia) searchParams.set('exclude_contained_media', 'true');
  if (params.sort) searchParams.set('sort', params.sort);
  if (params.order) searchParams.set('order', params.order);

  const response = await fetch(`${apiBaseUrl}/api/cards?${searchParams.toString()}`);

  return readJson<Card[]>(response);
}

export async function getCard(id: number) {
  const response = await fetch(`${apiBaseUrl}/api/cards/${id}`);

  return readJson<Card>(response);
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

export async function createCard(input: CreateCardInput) {
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
    body: formData,
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

export async function deleteCard(id: number) {
  const response = await fetch(`${apiBaseUrl}/api/cards/${id}`, {
    method: 'DELETE',
  });

  if (!response.ok) {
    const data = await response.json().catch(() => null);
    throw new Error(data?.error ?? 'Request failed.');
  }
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
    throw new Error(data?.error ?? 'Request failed.');
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
    };
  }

  if ('Id' in record || 'PreviewUrl' in record || 'OutgoingRelations' in record) {
    return {
      id: record.Id,
      type: record.Type,
      title: record.Title,
      previewUrl: record.PreviewUrl,
      contentUrl: record.ContentUrl,
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
