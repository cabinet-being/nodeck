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
import {
  useVisualCardView,
  VisualCardViewToggle,
  VisualCardWall,
  VisualCardWallSkeleton,
} from '@/components/visual-card-wall';
import { pageCountOf, Pagination, usePageScroll } from '@/components/pagination';
import { useLocalDay } from '@/hooks/use-local-day';

const pageSize = 48;

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
  const pageValue = Number(params.get('page'));
  const page = Number.isSafeInteger(pageValue) && pageValue > 0 ? pageValue : 1;
  const today = useLocalDay();
  const [items, setItems] = React.useState<Recommendation[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [loading, setLoading] = React.useState(true);
  const [requestVersion, retry] = React.useReducer((value: number) => value + 1, 0);
  const [view, setView] = useVisualCardView();
  const [total, setTotal] = React.useState(0);
  usePageScroll(page);

  React.useEffect(() => {
    let active = true;
    setItems([]);
    setError(null);
    setLoading(true);
    const offset = (page - 1) * pageSize;
    const request = kind === 'daily'
      ? getDailyRecommendations(pageSize, { offset, includeTotal: true })
      : getRecommendations({ mode, contextCardId, limit: pageSize, offset, includeTotal: true });
    request.then((response) => {
      if (!active) return;
      setItems(response.items);
      setTotal(response.total ?? response.items.length);
    })
      .catch((cause) => {
        if (active) setError(cause instanceof Error ? cause.message : 'Unable to load cards.');
      })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [kind, mode, contextCardId, today, requestVersion, page]);

  function selectMode(nextMode: RecommendationMode) {
    const query = new URLSearchParams({ mode: nextMode });
    if (contextCardId) query.set('contextCardId', String(contextCardId));
    onNavigateTo(`/feed/recommended?${query}`);
  }

  function goToPage(nextPage: number) {
    const query = new URLSearchParams(currentSearch);
    if (nextPage <= 1) query.delete('page');
    else query.set('page', String(nextPage));
    const text = query.toString();
    onNavigateTo(`/feed/${kind}${text ? `?${text}` : ''}`);
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
      {loading ? <VisualCardWallSkeleton count={12} /> : null}
      {!loading && !error && items.length === 0 ? (
        page > 1 ? (
          <EmptyState icon={Sparkles} title="Nothing on this page"
            action={<Button type="button" variant="outline" size="sm" onClick={() => goToPage(1)}>Back to the start</Button>} />
        ) : <EmptyState icon={Sparkles} title="Nothing here yet" />
      ) : null}
      {items.length > 0 ? (
        <VisualCardWall cards={items.map(({ card }) => card)} view={view} onNavigate={onNavigate} />
      ) : null}
      {!error ? (
        <Pagination page={page} pageCount={Math.max(pageCountOf(total, pageSize), page)} total={total}
          disabled={loading} onPageChange={goToPage} />
      ) : null}
    </section>
  );
}
