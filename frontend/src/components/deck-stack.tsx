import * as React from 'react';
import { Heart, Layers3 } from 'lucide-react';

import { listCards } from '@/api/cards';
import { type DeckSummary } from '@/api/decks';
import { CardPreview } from '@/components/card-preview';
import { cn } from '@/lib/utils';

type Cover = { id: number; title: string | null; previewUrl: string | null };

const coverCache = new Map<number, Cover[]>();
const maxInFlight = 4;
let inFlight = 0;
const waiting: Array<() => void> = [];

function acquire() {
  return new Promise<void>((resolve) => {
    if (inFlight < maxInFlight) {
      inFlight += 1;
      resolve();
    } else {
      waiting.push(() => { inFlight += 1; resolve(); });
    }
  });
}

function release() {
  inFlight -= 1;
  waiting.shift()?.();
}

const pending = new Map<number, Promise<Cover[]>>();

function loadCovers(deckId: number) {
  const existing = pending.get(deckId);
  if (existing) return existing;
  const promise = acquire()
    .then(() => listCards({ deckId, sort: 'position', limit: 3 }))
    .then((cards) => {
      const covers = cards.map((card) => ({ id: card.id, title: card.title, previewUrl: card.previewUrl }));
      coverCache.set(deckId, covers);
      return covers;
    })
    .finally(() => { release(); pending.delete(deckId); });
  pending.set(deckId, promise);
  return promise;
}

/** Loads up to three cover cards once the element becomes visible; cached per deck for the session. */
function useDeckCovers(deckId: number, enabled: boolean) {
  const ref = React.useRef<HTMLDivElement>(null);
  const [covers, setCovers] = React.useState<Cover[] | null>(() => coverCache.get(deckId) ?? null);
  const [failed, setFailed] = React.useState(false);

  React.useEffect(() => {
    const cached = coverCache.get(deckId);
    setFailed(false);
    if (cached) { setCovers(cached); return; }
    setCovers(null);
    const element = ref.current;
    if (!enabled || !element) return;
    let active = true;
    const start = () => {
      loadCovers(deckId)
        .then((result) => { if (active) setCovers(result); })
        .catch(() => { if (active) setFailed(true); });
    };
    if (typeof IntersectionObserver === 'undefined') {
      start();
      return () => { active = false; };
    }
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) {
        observer.disconnect();
        start();
      }
    }, { rootMargin: '200px' });
    observer.observe(element);
    return () => { active = false; observer.disconnect(); };
  }, [deckId, enabled]);

  return { ref, covers, failed };
}

const fan = [
  'z-30 motion-safe:group-hover/deck:-translate-y-1 motion-safe:group-focus-within/deck:-translate-y-1',
  'z-20 translate-x-3 rotate-[5deg] motion-safe:group-hover/deck:translate-x-5 motion-safe:group-hover/deck:rotate-[9deg] motion-safe:group-focus-within/deck:translate-x-5 motion-safe:group-focus-within/deck:rotate-[9deg]',
  'z-10 -translate-x-3 -rotate-[5deg] motion-safe:group-hover/deck:-translate-x-5 motion-safe:group-hover/deck:-rotate-[9deg] motion-safe:group-focus-within/deck:-translate-x-5 motion-safe:group-focus-within/deck:-rotate-[9deg]',
];

export function deckCardCountLabel(count: number) {
  return `${count} ${count === 1 ? 'card' : 'cards'}`;
}

export function DeckStack({
  deck,
  href,
  onNavigate,
  actions,
  className,
}: {
  deck: Pick<DeckSummary, 'id' | 'title' | 'cardCount' | 'systemKey'>;
  href: string;
  onNavigate?: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  /** Overlay slot (for example a menu button) placed over the top-right corner, outside the link. */
  actions?: React.ReactNode;
  className?: string;
}) {
  const { ref, covers, failed } = useDeckCovers(deck.id, deck.cardCount > 0);
  const isFavorites = deck.systemKey === 'favorites';
  const shown = (covers ?? []).slice(0, 3);
  const countLabel = deckCardCountLabel(deck.cardCount);

  return (
    <div className={cn('group/deck relative min-w-0', className)}>
      <a
        href={href}
        onClick={onNavigate}
        aria-label={`${deck.title}, ${countLabel}`}
        className="focus-visible:ring-ring/50 block rounded-2xl outline-none focus-visible:ring-3"
      >
        <div ref={ref} className="relative mx-auto aspect-square w-[82%] max-w-60 pt-2">
          {shown.length > 0 ? (
            shown.map((cover, index) => (
              <div
                key={cover.id}
                className={cn(
                  'bg-muted shadow-2 absolute inset-x-0 top-2 bottom-0 overflow-hidden rounded-2xl border transition-transform duration-[var(--dur-base)] ease-out motion-reduce:transition-none',
                  fan[index]
                )}
              >
                <CardPreview
                  url={cover.previewUrl}
                  title={cover.title}
                  type="No preview"
                  imageClassName="size-full object-cover"
                  fallbackClassName="size-full"
                />
              </div>
            ))
          ) : (
            [0, 1, 2].map((index) => (
              <div
                key={index}
                className={cn(
                  'bg-muted text-muted-foreground shadow-1 absolute inset-x-0 top-2 bottom-0 flex items-center justify-center rounded-2xl border transition-transform duration-[var(--dur-base)] ease-out motion-reduce:transition-none',
                  fan[index],
                  covers === null && !failed && deck.cardCount > 0 && 'animate-pulse'
                )}
              >
                {index === 0 ? (isFavorites ? <Heart className="size-8" aria-hidden="true" /> : <Layers3 className="size-8" aria-hidden="true" />) : null}
              </div>
            ))
          )}
          {isFavorites ? (
            <span className="bg-primary text-primary-foreground shadow-1 absolute bottom-2 left-2 z-40 flex size-7 items-center justify-center rounded-full">
              <Heart className="size-3.5 fill-current" aria-hidden="true" />
            </span>
          ) : null}
        </div>
        <div className="mt-3 min-w-0 px-1 text-center">
          <div className="truncate text-sm font-medium" title={deck.title}>{deck.title}</div>
          <div className="text-muted-foreground text-xs tabular-nums">{countLabel}</div>
        </div>
      </a>
      {actions ? <div className="absolute top-0 right-0 z-40">{actions}</div> : null}
    </div>
  );
}
