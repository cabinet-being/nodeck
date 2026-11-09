import * as React from 'react';

import {
  getDailyRecommendations,
  getRecommendations,
  type Recommendation,
  type RecommendationMode,
} from '@/api/recommendations';
import { Button } from '@/components/ui/button';
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
    <section className="grid gap-5 p-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-2xl font-semibold">{kind === 'daily' ? "Today's picks" : 'Recommended'}</h1>
        <div className="flex items-center gap-2">
          {kind === 'recommended' ? (
            <div className="bg-muted flex rounded-lg p-1" role="group" aria-label="Recommendation scope">
              <Button type="button" size="sm" variant={mode === 'collections' ? 'secondary' : 'ghost'}
                aria-pressed={mode === 'collections'} onClick={() => selectMode('collections')}>
                Collections
              </Button>
              <Button type="button" size="sm" variant={mode === 'pages' ? 'secondary' : 'ghost'}
                aria-pressed={mode === 'pages'} onClick={() => selectMode('pages')}>
                Pages
              </Button>
            </div>
          ) : null}
          <VisualCardViewToggle view={view} onChange={setView} />
        </div>
      </div>

      {error ? (
        <div className="text-destructive flex items-center gap-2 text-sm" role="alert">
          <span>{error}</span>
          <Button type="button" size="sm" variant="outline" onClick={retry}>Retry</Button>
        </div>
      ) : null}
      {loading ? <p className="text-muted-foreground text-sm">Loading…</p> : null}
      {!loading && !error && items.length === 0 ? (
        <p className="text-muted-foreground rounded-lg border p-6 text-center text-sm">Nothing to show yet.</p>
      ) : null}
      {items.length > 0 ? (
        <VisualCardWall cards={items.map(({ card }) => card)} view={view} onNavigate={onNavigate} />
      ) : null}
    </section>
  );
}
