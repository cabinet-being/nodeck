import type { Card } from '@/api/cards';

export type RecommendationMode = 'collections' | 'pages';

export type Recommendation = {
  card: Pick<Card, 'id' | 'type' | 'title' | 'previewUrl' | 'isFavorite'>;
  score: number;
};

export type RecommendationResponse = {
  items: Recommendation[];
  mode: RecommendationMode;
  /** Present when requested with `includeTotal`. */
  total?: number | null;
};

export type DailyRecommendationResponse = {
  items: Recommendation[];
  localDate: string;
  timeZone: string;
  total?: number | null;
};

const apiBaseUrl = import.meta.env.VITE_API_BASE_URL ?? '';

export async function getRecommendations({
  mode = 'collections',
  limit = 20,
  offset = 0,
  includeTotal = false,
  contextCardId,
}: {
  mode?: RecommendationMode;
  limit?: number;
  offset?: number;
  includeTotal?: boolean;
  contextCardId?: number;
} = {}): Promise<RecommendationResponse> {
  const params = new URLSearchParams({ mode, limit: String(limit) });
  if (offset > 0) params.set('offset', String(offset));
  if (includeTotal) params.set('include_total', 'true');
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

export async function getDailyRecommendations(limit = 30,
  { offset = 0, includeTotal = false }: { offset?: number; includeTotal?: boolean } = {}): Promise<DailyRecommendationResponse> {
  const timeZone = Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  const params = new URLSearchParams({ timeZone, limit: String(limit) });
  if (offset > 0) params.set('offset', String(offset));
  if (includeTotal) params.set('include_total', 'true');
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
