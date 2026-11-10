import * as React from 'react';
import { Sparkles } from 'lucide-react';

import {
  getDailyRecommendations,
  getRecommendations,
  type Recommendation,
  type RecommendationMode,
} from '@/api/recommendations';
import { EmptyState } from '@/components/empty-state';
import { RecommendationModeControl } from '@/components/recommendation-rail';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import {
  useVisualCardView,
  VisualCardViewToggle,
  VisualCardWall,
} from '@/components/visual-card-wall';
import { useLocalDay } from '@/hooks/use-local-day';

export function FeedPage({
  kind,
  currentSearch,
  onNavigate,
  onNavigateTo,
}: {
  kind: 'recommended' | 'daily';
  currentSearch: string;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onNavigateTo: (path: string) => void;
}) {
  const params = new URLSearchParams(currentSearch);
  const mode: RecommendationMode = params.get('mode') === 'pages' ? 'pages' : 'collections';
  const contextValue = Number(params.get('contextCardId'));
  const contextCardId = Number.isSafeInteger(contextValue) && contextValue > 0
    ? contextValue : undefined;
  const today = useLocalDay();
  const [items, setItems] = React.useState<Recommendation[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [loading, setLoading] = React.useState(true);
  const [requestVersion, retry] = React.useReducer((value: number) => value + 1, 0);
  const [view, setView] = useVisualCardView('nodeck.feedView');

  React.useEffect(() => {
    let active = true;
    setItems([]);
    setError(null);
    setLoading(true);
    const request = kind === 'daily'
      ? getDailyRecommendations(30)
      : getRecommendations({ mode, contextCardId, limit: 50 });
    request.then((response) => { if (active) setItems(response.items); })
      .catch((cause) => {
        if (active) setError(cause instanceof Error ? cause.message : 'Unable to load cards.');
      })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [kind, mode, contextCardId, today, requestVersion]);

  function selectMode(nextMode: RecommendationMode) {
    const query = new URLSearchParams({ mode: nextMode });
    if (contextCardId) query.set('contextCardId', String(contextCardId));
    onNavigateTo(`/feed/recommended?${query}`);
  }

  return (
    <section className="mx-auto grid max-w-screen-2xl gap-5 px-4 py-6 sm:px-6 sm:py-8">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-2xl font-semibold tracking-tight">{kind === 'daily' ? "Today's picks" : 'For you'}</h1>
        <div className="flex items-center gap-2">
          {kind === 'recommended' ? <RecommendationModeControl value={mode} onChange={selectMode} /> : null}
          <VisualCardViewToggle view={view} onChange={setView} />
        </div>
      </div>

      {error ? (
        <div className="text-destructive flex items-center gap-2 text-sm" role="alert">
          <span>{error}</span>
          <Button type="button" size="sm" variant="outline" onClick={retry}>Try again</Button>
        </div>
      ) : null}
      {loading ? (
        <div role="status" aria-label="Loading"
          className="grid grid-cols-2 gap-3 sm:grid-cols-3 sm:gap-4 md:grid-cols-4 xl:grid-cols-6">
          {Array.from({ length: 12 }, (_, index) => <Skeleton key={index} className="aspect-square rounded-2xl" />)}
        </div>
      ) : null}
      {!loading && !error && items.length === 0 ? (
        <EmptyState icon={Sparkles} title="Nothing here yet" />
      ) : null}
      {items.length > 0 ? (
        <VisualCardWall cards={items.map(({ card }) => card)} view={view} onNavigate={onNavigate} />
      ) : null}
    </section>
  );
}
