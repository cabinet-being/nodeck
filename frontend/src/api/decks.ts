import { type Card } from '@/api/cards';

export type DeckSummary = {
  id: number;
  title: string;
  cardCount: number;
  properties: Record<string, unknown> | null;
  metadata: Record<string, unknown>;
  systemKey: string | null;
};

export type DeckCard = Pick<Card, 'id' | 'type' | 'title' | 'previewUrl' | 'contentUrl' | 'metadata'> & {
  position: number;
  properties: Record<string, unknown> | null;
  membershipProperties: Record<string, unknown> | null;
};

export type DeckDetails = DeckSummary & {
  cards: DeckCard[];
};

export type DeckListParams = {
  search?: string;
  sort?: 'created_at';
  order?: 'asc' | 'desc';
};

export type DeckCardInput = {
  cardId: number;
  properties?: Record<string, string>;
};

export type DeckInput = {
  title: string;
  properties?: Record<string, string>;
  cards?: DeckCardInput[];
};

const apiBaseUrl = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:8080';

export async function listDecks(params: DeckListParams = {}) {
  const searchParams = new URLSearchParams();

  if (params.search) searchParams.set('search', params.search);
  if (params.sort) searchParams.set('sort', params.sort);
  if (params.order) searchParams.set('order', params.order);

  const response = await fetch(`${apiBaseUrl}/api/decks?${searchParams.toString()}`);

  return readJson<DeckSummary[]>(response);
}

export async function getDeck(id: number) {
  const response = await fetch(`${apiBaseUrl}/api/decks/${id}`);

  return readJson<DeckDetails>(response);
}

export async function getFavorites() {
  const response = await fetch(`${apiBaseUrl}/api/favorites`);

  return readJson<DeckDetails>(response);
}

export async function createDeck(input: DeckInput) {
  const response = await fetch(`${apiBaseUrl}/api/decks`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });

  return readJson<DeckDetails>(response);
}

export async function updateDeck(id: number, input: DeckInput) {
  const response = await fetch(`${apiBaseUrl}/api/decks/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });

  return readJson<DeckDetails>(response);
}

export async function deleteDeck(id: number) {
  const response = await fetch(`${apiBaseUrl}/api/decks/${id}`, {
    method: 'DELETE',
  });

  if (!response.ok) {
    const data = await response.json().catch(() => null);
    throw new Error(data?.error ?? 'Request failed.');
  }
}

async function readJson<T>(response: Response): Promise<T> {
  const data = await response.json().catch(() => null);

  if (!response.ok) {
    throw new Error(data?.error ?? 'Request failed.');
  }

  return normalizeDeckData(data) as T;
}

function normalizeDeckData(data: unknown): unknown {
  if (Array.isArray(data)) {
    return data.map(normalizeDeckData);
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
      position: record.Position,
      properties: record.Properties,
      metadata: record.Metadata,
      membershipProperties: record.MembershipProperties,
    };
  }

  if ('Id' in record || 'CardCount' in record || 'SystemKey' in record) {
    return {
      id: record.Id,
      title: record.Title,
      cardCount: record.CardCount ?? (Array.isArray(record.Cards) ? record.Cards.length : 0),
      properties: record.Properties,
      metadata: record.Metadata,
      systemKey: record.SystemKey,
      cards: normalizeDeckData(record.Cards),
    };
  }

  return data;
}
