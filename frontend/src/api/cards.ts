export type CardRelation = {
  id: number;
  fromCardId: number;
  toCardId: number;
  relationType: string;
  properties: Record<string, unknown> | null;
  metadata: Record<string, unknown>;
};

export type Card = {
  id: number;
  type: string;
  title: string | null;
  previewUrl: string | null;
  contentUrl: string | null;
  properties: Record<string, unknown> | null;
  metadata: Record<string, unknown>;
  outgoingRelations?: CardRelation[];
  incomingRelations?: CardRelation[];
};

export type CardListParams = {
  type?: string;
  mediaType?: string;
  search?: string;
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
  properties?: Record<string, string>;
  relations?: CreateRelationInput[];
};

const apiBaseUrl = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:8080';

export const cardTypes = ['media', 'comic', 'set', 'tag', 'source'] as const;
export const relationTypes = [
  'tagged_with',
  'sourced_from',
  'contains',
  'related_to',
  'preview_for',
] as const;

export async function listCards(params: CardListParams = {}) {
  const searchParams = new URLSearchParams();

  if (params.type) searchParams.set('type', params.type);
  if (params.mediaType) searchParams.set('media_type', params.mediaType);
  if (params.search) searchParams.set('search', params.search);
  if (params.sort) searchParams.set('sort', params.sort);
  if (params.order) searchParams.set('order', params.order);

  const response = await fetch(`${apiBaseUrl}/api/cards?${searchParams.toString()}`);

  return readJson<Card[]>(response);
}

export async function getCard(id: number) {
  const response = await fetch(`${apiBaseUrl}/api/cards/${id}`);

  return readJson<Card>(response);
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

function normalizeCardData(data: unknown): unknown {
  if (Array.isArray(data)) {
    return data.map(normalizeCardData);
  }

  if (!data || typeof data !== 'object') {
    return data;
  }

  const record = data as Record<string, unknown>;

  if ('Id' in record || 'PreviewUrl' in record || 'OutgoingRelations' in record) {
    return {
      id: record.Id,
      type: record.Type,
      title: record.Title,
      previewUrl: record.PreviewUrl,
      contentUrl: record.ContentUrl,
      properties: record.Properties,
      metadata: record.Metadata,
      outgoingRelations: normalizeCardData(record.OutgoingRelations),
      incomingRelations: normalizeCardData(record.IncomingRelations),
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
