import * as React from 'react';
import { ArrowUpRight, ChevronLeft, ChevronRight, Images, Layers3 } from 'lucide-react';

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
            <Button type="button" size="icon" variant={mode === 'collections' ? 'secondary' : 'ghost'}
              aria-label="Recommend collections" title="Collections"
              aria-pressed={mode === 'collections'} onClick={() => setMode('collections')}>
              <Layers3 className="size-4" />
            </Button>
            <Button type="button" size="icon" variant={mode === 'pages' ? 'secondary' : 'ghost'}
              aria-label="Recommend individual pages" title="Pages"
              aria-pressed={mode === 'pages'} onClick={() => setMode('pages')}>
              <Images className="size-4" />
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
        <h2 className="text-xl font-semibold tracking-tight">{title}</h2>
        <div className="flex items-center gap-1 sm:gap-2">
          {viewAllHref ? <a href={viewAllHref} onClick={onNavigate}
            aria-label={`View all ${title}`} title={`View all ${title}`}
            className="text-muted-foreground hover:text-foreground inline-flex size-9 items-center justify-center rounded-md transition-colors hover:bg-muted"><ArrowUpRight className="size-4" /></a> : null}
          {controls}
          <Button type="button" size="icon" variant="ghost" aria-label={`Scroll ${title} left`} title="Scroll left"
            disabled={cards.length === 0} onClick={() => scroll(-1)}>
            <ChevronLeft className="size-4" />
          </Button>
          <Button type="button" size="icon" variant="ghost" aria-label={`Scroll ${title} right`} title="Scroll right"
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
          aria-label={`${title} cards`}
          onKeyDown={(event) => {
            if (event.altKey || event.ctrlKey || event.metaKey) return;
            if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
              event.preventDefault();
              scroll(event.key === 'ArrowLeft' ? -1 : 1);
            }
          }}>
          {cards.map((card) => (
            <a key={card.id} href={`/cards/${card.id}`} onClick={onNavigate}
              aria-label={`Open ${card.title || card.type}`}
              title={card.title || card.type}
              className="bg-muted group relative w-40 shrink-0 snap-start overflow-hidden rounded-xl border sm:w-44">
              <div className="aspect-square overflow-hidden">
                <CardPreview url={card.previewUrl} title={card.title} type={card.type}
                  imageClassName="size-full object-cover transition-transform group-hover:scale-105"
                  fallbackClassName="size-full text-xs" />
              </div>
              <div aria-hidden="true" className="pointer-events-none absolute inset-x-0 bottom-0 truncate bg-gradient-to-t from-black/75 to-transparent px-3 pb-2 pt-7 text-sm text-white opacity-0 transition-opacity group-hover:opacity-100 group-focus-visible:opacity-100">
                {card.title || card.type}
              </div>
            </a>
          ))}
        </div>
      ) : null}
    </section>
  );
}
