import * as React from 'react';
import { ChevronLeft, ChevronRight } from 'lucide-react';

import { cn } from '@/lib/utils';

/**
 * Manga-reader tap zones over the whole media stage: the left third turns back, the right
 * third turns forward, the middle opens the current page's own card. The zones cover the
 * full stage (not just the image), so a narrow portrait page on a wide screen still has
 * large targets. Chevrons are hints inside the zones, not separate small buttons.
 */
export function StagePager({
  canPrev,
  canNext,
  onPrev,
  onNext,
  openHref,
  openLabel,
  onOpen,
  prevLabel = 'Previous page',
  nextLabel = 'Next page',
  consumeSwipeClick,
  className,
}: {
  canPrev: boolean;
  canNext: boolean;
  onPrev: () => void;
  onNext: () => void;
  openHref?: string;
  openLabel?: string;
  onOpen?: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  prevLabel?: string;
  nextLabel?: string;
  /** Returns true once when the click directly follows a swipe (the swipe already turned the page). */
  consumeSwipeClick?: () => boolean;
  className?: string;
}) {
  const swallowed = (event: React.MouseEvent) => {
    if (consumeSwipeClick?.()) {
      event.preventDefault();
      return true;
    }
    return false;
  };

  const sideClass =
    'group/zone relative flex h-full items-center outline-none focus-visible:bg-stage-foreground/5 disabled:cursor-default';
  const hintClass =
    'bg-stage/70 text-stage-foreground pointer-events-none inline-flex size-11 items-center justify-center rounded-full backdrop-blur transition-opacity duration-[var(--dur-fast)] [&_svg]:size-5';

  return (
    <div className={cn('absolute inset-0 z-[5] grid grid-cols-3', className)}>
      <button
        type="button"
        aria-label={prevLabel}
        title={prevLabel}
        disabled={!canPrev}
        onClick={(event) => { if (!swallowed(event)) onPrev(); }}
        className={cn(sideClass, 'cursor-w-resize justify-start pl-3')}
      >
        <span className={cn(hintClass, canPrev
          ? 'opacity-0 group-hover/zone:opacity-100 group-focus-visible/zone:opacity-100 pointer-coarse:opacity-50'
          : 'opacity-0')}>
          <ChevronLeft />
        </span>
      </button>
      {openHref ? (
        <a
          href={openHref}
          aria-label={openLabel}
          title={openLabel}
          onClick={(event) => {
            if (swallowed(event)) return;
            onOpen?.(event);
          }}
          className="h-full cursor-pointer outline-none focus-visible:bg-stage-foreground/5"
        />
      ) : <div aria-hidden />}
      <button
        type="button"
        aria-label={nextLabel}
        title={nextLabel}
        disabled={!canNext}
        onClick={(event) => { if (!swallowed(event)) onNext(); }}
        className={cn(sideClass, 'cursor-e-resize justify-end pr-3')}
      >
        <span className={cn(hintClass, canNext
          ? 'opacity-0 group-hover/zone:opacity-100 group-focus-visible/zone:opacity-100 pointer-coarse:opacity-50'
          : 'opacity-0')}>
          <ChevronRight />
        </span>
      </button>
    </div>
  );
}
