import type { Card } from '@/api/cards';

export type RecommendationMode = 'collections' | 'pages';

export type Recommendation = {
  card: Pick<Card, 'id' | 'type' | 'title' | 'previewUrl' | 'isFavorite'>;
  score: number;
};

export type RecommendationResponse = {
  items: Recommendation[];
  mode: RecommendationMode;
};

export type DailyRecommendationResponse = {
  items: Recommendation[];
  localDate: string;
  timeZone: string;
};

const apiBaseUrl = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5080';

export async function getRecommendations({
  mode = 'collections',
  limit = 20,
  contextCardId,
}: {
  mode?: RecommendationMode;
  limit?: number;
  contextCardId?: number;
} = {}): Promise<RecommendationResponse> {
  const params = new URLSearchParams({ mode, limit: String(limit) });
  if (contextCardId !== undefined) params.set('contextCardId', String(contextCardId));

  const response = await fetch(`${apiBaseUrl}/api/recommendations?${params}`);
  const body: unknown = await response.json().catch(() => null);
  if (!response.ok) {
    const message = body && typeof body === 'object' && 'error' in body
      ? String(body.error)
      : 'Unable to load recommendations.';
    throw new Error(message);
  }
  return body as RecommendationResponse;
}

export async function getDailyRecommendations(limit = 30): Promise<DailyRecommendationResponse> {
  const timeZone = Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  const params = new URLSearchParams({ timeZone, limit: String(limit) });
  const response = await fetch(`${apiBaseUrl}/api/recommendations/daily?${params}`);
  const body: unknown = await response.json().catch(() => null);
  if (!response.ok) {
    const message = body && typeof body === 'object' && 'error' in body
      ? String(body.error)
      : 'Unable to load today’s picks.';
    throw new Error(message);
  }
  return body as DailyRecommendationResponse;
}
