import * as React from 'react';
import { ChevronLeft, ChevronRight } from 'lucide-react';

import { type Card } from '@/api/cards';
import { getRecommendations, type Recommendation, type RecommendationMode } from '@/api/recommendations';
import { CardPreview } from '@/components/card-preview';
import { Button } from '@/components/ui/button';

export function RecommendationRail({
  contextCardId,
  onNavigate,
}: {
  contextCardId?: number;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const [mode, setMode] = React.useState<RecommendationMode>('collections');
  const [items, setItems] = React.useState<Recommendation[]>([]);
  const [loading, setLoading] = React.useState(true);
  const [error, setError] = React.useState<string | null>(null);
  const [requestVersion, retry] = React.useReducer((value: number) => value + 1, 0);

  React.useEffect(() => {
    let active = true;
    setLoading(true);
    setError(null);
    setItems([]);
    getRecommendations({ mode, contextCardId, limit: 20 })
      .then((response) => { if (active) setItems(response.items); })
      .catch((cause) => {
        if (active) setError(cause instanceof Error ? cause.message : 'Unable to load recommendations.');
      })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [contextCardId, mode, requestVersion]);

  return (
    <CardRail title="Recommended" cards={items.map(({ card }) => card)} onNavigate={onNavigate}
      loading={loading} error={error} onRetry={retry}
      viewAllHref={`/feed/recommended?mode=${mode}${contextCardId ? `&contextCardId=${contextCardId}` : ''}`}
      controls={
        <div className="bg-muted flex rounded-lg p-1" role="group" aria-label="Recommendation scope">
            <Button type="button" size="sm" variant={mode === 'collections' ? 'secondary' : 'ghost'}
              aria-pressed={mode === 'collections'} onClick={() => setMode('collections')}>
              Collections
            </Button>
            <Button type="button" size="sm" variant={mode === 'pages' ? 'secondary' : 'ghost'}
              aria-pressed={mode === 'pages'} onClick={() => setMode('pages')}>
              Pages
            </Button>
        </div>
      }
    />
  );
}

export type RailCard = Pick<Card, 'id' | 'type' | 'title' | 'previewUrl'>;

export function CardRail({
  title,
  cards,
  onNavigate,
  loading = false,
  error = null,
  onRetry,
  controls,
  viewAllHref,
}: {
  title: string;
  cards: RailCard[];
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  loading?: boolean;
  error?: string | null;
  onRetry?: () => void;
  controls?: React.ReactNode;
  viewAllHref?: string;
}) {
  const railRef = React.useRef<HTMLDivElement>(null);

  function scroll(direction: -1 | 1) {
    const rail = railRef.current;
    if (!rail) return;
    rail.scrollBy({ left: direction * Math.max(rail.clientWidth * 0.8, 180), behavior: 'smooth' });
  }

  return (
    <section className="min-w-0 space-y-3" aria-label={title}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="text-lg font-semibold">{title}</h2>
        <div className="flex items-center gap-2">
          {viewAllHref ? <a href={viewAllHref} onClick={onNavigate}
            className="text-muted-foreground hover:text-foreground whitespace-nowrap text-sm">View all</a> : null}
          {controls}
          <Button type="button" size="icon" variant="outline" aria-label={`Scroll ${title} left`}
            disabled={cards.length === 0} onClick={() => scroll(-1)}>
            <ChevronLeft className="size-4" />
          </Button>
          <Button type="button" size="icon" variant="outline" aria-label={`Scroll ${title} right`}
            disabled={cards.length === 0} onClick={() => scroll(1)}>
            <ChevronRight className="size-4" />
          </Button>
        </div>
      </div>
      {error ? (
        <div className="text-destructive flex items-center gap-2 text-sm" role="alert">
          <span>{error}</span>
          {onRetry ? <Button type="button" size="sm" variant="outline" onClick={onRetry}>Retry</Button> : null}
        </div>
      ) : null}
      {loading ? <div className="text-muted-foreground text-sm">Loading…</div> : null}
      {!loading && !error && cards.length === 0 ? (
        <div className="text-muted-foreground text-sm">Nothing to show yet.</div>
      ) : null}
      {cards.length > 0 ? (
        <div ref={railRef} className="flex snap-x snap-mandatory gap-3 overflow-x-auto pb-2" tabIndex={0}
          onKeyDown={(event) => {
            if (event.altKey || event.ctrlKey || event.metaKey) return;
            if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
              event.preventDefault();
              scroll(event.key === 'ArrowLeft' ? -1 : 1);
            }
          }}>
          {cards.map((card) => (
            <a key={card.id} href={`/cards/${card.id}`} onClick={onNavigate}
              className="bg-muted group w-40 shrink-0 snap-start overflow-hidden rounded-lg border sm:w-44">
              <div className="aspect-square overflow-hidden">
                <CardPreview url={card.previewUrl} title={card.title} type={card.type}
                  imageClassName="size-full object-cover transition-transform group-hover:scale-105"
                  fallbackClassName="size-full text-xs" />
              </div>
              <div className="truncate px-2 py-2 text-sm" title={card.title ?? `Card #${card.id}`}>
                {card.title || `Card #${card.id}`}
              </div>
            </a>
          ))}
        </div>
      ) : null}
    </section>
  );
}
