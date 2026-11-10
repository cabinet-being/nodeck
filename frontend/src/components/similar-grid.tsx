import * as React from 'react';

import { getRecommendations, type Recommendation, type RecommendationMode } from '@/api/recommendations';
import { RecommendationModeControl } from '@/components/recommendation-rail';
import { Button } from '@/components/ui/button';
import { VisualCardWall, VisualCardWallSkeleton, useVisualCardView } from '@/components/visual-card-wall';

const loadLimit = 50;
const batchSize = 24;

/** Similar cards as a wall below the stage; more tiles are revealed while scrolling. */
export function SimilarGrid({
  contextCardId,
  onNavigate,
  title = 'Similar',
}: {
  contextCardId: number;
  title?: string;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const [mode, setMode] = React.useState<RecommendationMode>('collections');
  const [items, setItems] = React.useState<Recommendation[]>([]);
  const [loading, setLoading] = React.useState(true);
  const [error, setError] = React.useState<string | null>(null);
  const [visibleCount, setVisibleCount] = React.useState(batchSize);
  const [requestVersion, retry] = React.useReducer((value: number) => value + 1, 0);
  const [view] = useVisualCardView();
  const sentinelRef = React.useRef<HTMLDivElement>(null);

  React.useEffect(() => {
    let active = true;
    setLoading(true);
    setError(null);
    setItems([]);
    setVisibleCount(batchSize);
    getRecommendations({ mode, contextCardId, limit: loadLimit })
      .then((response) => { if (active) setItems(response.items); })
      .catch((cause) => {
        if (active) setError(cause instanceof Error ? cause.message : 'Unable to load similar cards.');
      })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [contextCardId, mode, requestVersion]);

  const hasMore = visibleCount < items.length;

  React.useEffect(() => {
    const sentinel = sentinelRef.current;
    if (!sentinel || !hasMore || typeof IntersectionObserver === 'undefined') return;
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) {
        setVisibleCount((count) => count + batchSize);
      }
    }, { rootMargin: '600px 0px' });
    observer.observe(sentinel);
    return () => observer.disconnect();
  }, [hasMore, visibleCount]);

  const shown = items.slice(0, visibleCount).map(({ card }) => card);

  return (
    <section className="min-w-0 space-y-3" aria-label={title}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="text-xl font-semibold tracking-tight">{title}</h2>
        <RecommendationModeControl value={mode} onChange={setMode} />
      </div>
      {error ? (
        <div className="text-destructive flex items-center gap-2 text-sm" role="alert">
          <span>{error}</span>
          <Button type="button" size="sm" variant="outline" onClick={retry}>Try again</Button>
        </div>
      ) : null}
      {loading ? <VisualCardWallSkeleton count={batchSize} /> : null}
      {!loading && !error && items.length === 0 ? (
        <p className="text-muted-foreground text-sm">Nothing here yet.</p>
      ) : null}
      {shown.length > 0 ? <VisualCardWall cards={shown} view={view} onNavigate={onNavigate} /> : null}
      {hasMore ? <div ref={sentinelRef} aria-hidden="true" className="h-px" /> : null}
    </section>
  );
}
