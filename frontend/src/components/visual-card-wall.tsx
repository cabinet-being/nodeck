import * as React from 'react';
import { Grid3X3, LayoutGrid } from 'lucide-react';

import { resolveApiUrl, type Card } from '@/api/cards';
import { FavoriteToggle } from '@/components/favorite-toggle';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';

export type VisualCardView = 'grid' | 'brick';
export type VisualCardDensity = 'default' | 'compact';

export const visualCardViewStorageKey = 'nodeck.visualCardView';

export type VisualCard = Pick<Card, 'id' | 'type' | 'title' | 'previewUrl' | 'isFavorite'>;

export function useVisualCardView(storageKey = visualCardViewStorageKey) {
  const [view, setView] = React.useState<VisualCardView>(() => {
    return window.localStorage.getItem(storageKey) === 'brick' ? 'brick' : 'grid';
  });

  React.useEffect(() => {
    window.localStorage.setItem(storageKey, view);
  }, [storageKey, view]);

  return [view, setView] as const;
}

export function VisualCardViewToggle({
  view,
  onChange,
}: {
  view: VisualCardView;
  onChange: (view: VisualCardView) => void;
}) {
  return (
    <div className="bg-muted flex rounded-lg p-1">
      <Button
        type="button"
        variant={view === 'grid' ? 'secondary' : 'ghost'}
        size="icon"
        aria-label="Grid view"
        onClick={() => onChange('grid')}
      >
        <Grid3X3 className="size-4" />
      </Button>
      <Button
        type="button"
        variant={view === 'brick' ? 'secondary' : 'ghost'}
        size="icon"
        aria-label="Brick view"
        onClick={() => onChange('brick')}
      >
        <LayoutGrid className="size-4" />
      </Button>
    </div>
  );
}

export function VisualCardWall({
  cards,
  view,
  density = 'default',
  onNavigate,
}: {
  cards: VisualCard[];
  view: VisualCardView;
  density?: VisualCardDensity;
  onNavigate?: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const [favoriteOverrides, setFavoriteOverrides] = React.useState<Record<number, boolean>>({});

  return (
    <div
      className={cn(
        'w-full max-w-full min-w-0',
        view === 'grid'
          ? cn(
              'grid grid-cols-2 gap-3',
              density === 'compact'
                ? 'grid-cols-2 sm:grid-cols-2 md:grid-cols-2 lg:grid-cols-2 xl:grid-cols-2'
                : 'sm:grid-cols-3 md:grid-cols-4 xl:grid-cols-6'
            )
          : cn(
              'columns-2 gap-3',
              density === 'compact'
                ? 'columns-2 sm:columns-2 md:columns-2 lg:columns-2 xl:columns-2'
                : 'sm:columns-3 md:columns-4 xl:columns-6'
            )
      )}
    >
      {cards.map((card) => (
        <div
          key={card.id}
          className={cn(
            'bg-muted relative w-full max-w-full min-w-0 overflow-hidden rounded-lg border',
            view === 'grid' ? 'block aspect-square' : 'mb-3 block break-inside-avoid'
          )}
        >
          <a
            href={`/cards/${card.id}`}
            onClick={onNavigate}
            className={cn('block', view === 'grid' ? 'size-full' : 'w-full')}
          >
            {card.previewUrl ? (
              <img
                src={resolveApiUrl(card.previewUrl)}
                alt={card.title ?? ''}
                className={cn(
                  'w-full object-cover',
                  view === 'grid' ? 'size-full' : 'h-auto'
                )}
                loading="lazy"
              />
            ) : null}
          </a>
          <FavoriteToggle
            cardId={card.id}
            isFavorite={favoriteOverrides[card.id] ?? card.isFavorite}
            className="absolute right-2 top-2 bg-background/90"
            onChange={(isFavorite) =>
              setFavoriteOverrides((current) => ({ ...current, [card.id]: isFavorite }))
            }
          />
        </div>
      ))}
    </div>
  );
}
