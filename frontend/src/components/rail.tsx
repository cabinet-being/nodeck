import * as React from 'react';
import { ChevronLeft, ChevronRight } from 'lucide-react';

import { CardTile, type VisualCard } from '@/components/card-tile';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';

export type RailCard = VisualCard;

export function Rail({
  title,
  subtitle,
  cards,
  onNavigate,
  loading = false,
  error = null,
  onRetry,
  controls,
  viewAllHref,
}: {
  title: string;
  subtitle?: string;
  cards: RailCard[];
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  loading?: boolean;
  error?: string | null;
  onRetry?: () => void;
  controls?: React.ReactNode;
  viewAllHref?: string;
}) {
  const railRef = React.useRef<HTMLDivElement>(null);

  const [canScroll, setCanScroll] = React.useState({ left: false, right: false });

  const updateScroll = React.useCallback(() => {
    const rail = railRef.current;
    if (!rail) return;
    const left = rail.scrollLeft > 4;
    const right = rail.scrollLeft + rail.clientWidth < rail.scrollWidth - 4;
    setCanScroll((current) => (current.left === left && current.right === right ? current : { left, right }));
  }, []);

  React.useEffect(() => {
    const rail = railRef.current;
    if (!rail) return;
    updateScroll();
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(updateScroll);
    observer?.observe(rail);
    return () => observer?.disconnect();
  }, [updateScroll, cards, loading]);

  function scroll(direction: -1 | 1) {
    const rail = railRef.current;
    if (!rail) return;
    rail.scrollBy({ left: direction * Math.max(rail.clientWidth * 0.8, 180), behavior: 'smooth' });
  }

  return (
    <section className="group/rail min-w-0 space-y-3" aria-label={title}>
      <div className="flex flex-wrap items-end justify-between gap-2">
        <div className="min-w-0">
          <h2 className="text-xl font-semibold tracking-tight">{title}</h2>
          {subtitle ? <p className="text-muted-foreground text-sm">{subtitle}</p> : null}
        </div>
        <div className="flex items-center gap-2">
          {controls}
          {viewAllHref ? (
            <a
              href={viewAllHref}
              onClick={onNavigate}
              aria-label={`See all ${title}`}
              className="text-muted-foreground hover:text-foreground inline-flex h-8 items-center rounded-md px-2 text-sm font-medium transition-colors pointer-coarse:h-11"
            >
              See all →
            </a>
          ) : null}
        </div>
      </div>
      {error ? (
        <div className="text-destructive flex items-center gap-2 text-sm" role="alert">
          <span>{error}</span>
          {onRetry ? <Button type="button" size="sm" variant="outline" onClick={onRetry}>Try again</Button> : null}
        </div>
      ) : null}
      {loading ? (
        <div className="flex gap-3 overflow-hidden pb-2" role="status" aria-label={`Loading ${title}`}>
          {Array.from({ length: 6 }, (_, index) => (
            <Skeleton key={index} className="aspect-square w-40 shrink-0 rounded-2xl sm:w-44" />
          ))}
        </div>
      ) : null}
      {!loading && !error && cards.length === 0 ? (
        <p className="text-muted-foreground text-sm">Nothing here yet.</p>
      ) : null}
      {!loading && cards.length > 0 ? (
        <div className="relative">
          <div
            ref={railRef}
            onScroll={updateScroll}
            className="-mx-1 flex snap-x snap-mandatory gap-3 overflow-x-auto px-1 pt-1 pb-3 [scrollbar-width:none] [&::-webkit-scrollbar]:hidden"
            role="list"
            aria-label={`${title} cards`}
          >
            {cards.map((card) => (
              <div key={card.id} role="listitem" className="contents">
                <CardTile card={card} layout="rail" onNavigate={onNavigate} />
              </div>
            ))}
          </div>
          {(['left', 'right'] as const).map((side) => (
            canScroll[side] ? (
              <Button key={side} type="button" size="icon" variant="secondary"
                aria-label={`Scroll ${title} ${side}`} title={`Scroll ${side}`}
                onClick={() => scroll(side === 'left' ? -1 : 1)}
                className={`bg-surface/95 shadow-2 absolute top-[calc(50%-0.375rem)] z-10 hidden size-10 -translate-y-1/2 rounded-full opacity-0 transition-opacity focus-visible:opacity-100 group-focus-within/rail:opacity-100 group-hover/rail:opacity-100 pointer-fine:flex ${side === 'left' ? 'left-1' : 'right-1'}`}
              >
                {side === 'left' ? <ChevronLeft className="size-5" /> : <ChevronRight className="size-5" />}
              </Button>
            ) : null
          ))}
        </div>
      ) : null}
    </section>
  );
}
