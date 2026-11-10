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

  // Target of an in-flight smooth scroll, so quick repeated clicks keep moving forward
  // instead of measuring from a half-finished position.
  const pendingRef = React.useRef<{ left: number; timer: number } | null>(null);

  function tileOffsets(rail: HTMLDivElement) {
    const railLeft = rail.getBoundingClientRect().left;
    return Array.from(rail.querySelectorAll<HTMLElement>('[role=listitem] > *')).map((tile) => {
      const rect = tile.getBoundingClientRect();
      return { left: rect.left - railLeft + rail.scrollLeft, right: rect.right - railLeft + rail.scrollLeft };
    });
  }

  function scroll(direction: -1 | 1) {
    const rail = railRef.current;
    if (!rail) return;
    const base = pendingRef.current?.left ?? rail.scrollLeft;
    const max = rail.scrollWidth - rail.clientWidth;
    const tiles = tileOffsets(rail);
    const padding = 4;
    let target: number;
    if (direction === 1) {
      // Page forward: the first tile that is cut off on the right becomes the first visible one.
      const next = tiles.find((tile) => tile.right > base + rail.clientWidth + 1);
      target = next ? next.left - padding : max;
    } else {
      // Page back by one screen, aligned to a tile edge.
      const goal = base - rail.clientWidth;
      const previous = tiles.find((tile) => tile.left >= goal - 1);
      target = previous ? previous.left - padding : 0;
    }
    target = Math.max(0, Math.min(max, target));
    if (Math.abs(target - base) < 1) return;
    if (pendingRef.current) window.clearTimeout(pendingRef.current.timer);
    const reduceMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
    pendingRef.current = { left: target, timer: window.setTimeout(() => { pendingRef.current = null; }, 700) };
    rail.scrollTo({ left: target, behavior: reduceMotion ? 'auto' : 'smooth' });
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
            className="-mx-1 flex gap-3 overflow-x-auto px-1 pt-1 pb-3 [scrollbar-width:none] pointer-coarse:snap-x pointer-coarse:snap-mandatory [&::-webkit-scrollbar]:hidden"
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
            // Always mounted (hidden when it can't scroll) so a click is never lost to a re-mount.
            // A tall edge strip is the target, not a small round button; touch screens swipe instead.
            <button
              key={side}
              type="button"
              aria-label={`Scroll ${title} ${side}`}
              title={`Scroll ${side}`}
              disabled={!canScroll[side]}
              onClick={() => scroll(side === 'left' ? -1 : 1)}
              className={`group/edge absolute inset-y-0 z-10 hidden w-16 items-center outline-none focus-visible:outline-none transition-opacity duration-[var(--dur-fast)] pointer-fine:flex disabled:pointer-events-none disabled:opacity-0 ${side === 'left'
                ? 'left-0 justify-start bg-gradient-to-r from-background/90 to-transparent pl-1'
                : 'right-0 justify-end bg-gradient-to-l from-background/90 to-transparent pr-1'}`}
            >
              <span className="text-foreground inline-flex size-11 items-center justify-center opacity-70 drop-shadow-[0_1px_2px_var(--background)] transition-[opacity,transform] duration-[var(--dur-fast)] group-hover/edge:scale-110 group-hover/edge:opacity-100 group-focus-visible/edge:opacity-100 rounded-full group-focus-visible/edge:ring-2 group-focus-visible/edge:ring-ring">
                {side === 'left' ? <ChevronLeft className="size-7" strokeWidth={2.5} /> : <ChevronRight className="size-7" strokeWidth={2.5} />}
              </span>
            </button>
          ))}
        </div>
      ) : null}
    </section>
  );
}
