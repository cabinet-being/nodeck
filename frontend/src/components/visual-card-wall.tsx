import * as React from 'react';
import { Grid3X3, LayoutGrid } from 'lucide-react';

import { type Card } from '@/api/cards';
import { CardPreview } from '@/components/card-preview';
import { FavoriteToggle } from '@/components/favorite-toggle';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';

export type VisualCardView = 'grid' | 'brick';
export type VisualCardDensity = 'default' | 'compact';

export const visualCardViewStorageKey = 'nodeck.visualCardView';

export type VisualCard = Pick<Card, 'id' | 'type' | 'title' | 'previewUrl'> & {
  isFavorite?: Card['isFavorite'];
};

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
    <div className="bg-muted flex rounded-lg p-1" role="group" aria-label="Gallery layout">
      <Button
        type="button"
        variant={view === 'grid' ? 'secondary' : 'ghost'}
        size="icon"
        aria-label="Grid view"
        title="Grid view"
        aria-pressed={view === 'grid'}
        onClick={() => onChange('grid')}
      >
        <Grid3X3 className="size-4" />
      </Button>
      <Button
        type="button"
        variant={view === 'brick' ? 'secondary' : 'ghost'}
        size="icon"
        aria-label="Brick view"
        title="Brick view"
        aria-pressed={view === 'brick'}
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
  renderCaption,
}: {
  cards: VisualCard[];
  view: VisualCardView;
  density?: VisualCardDensity;
  onNavigate?: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  renderCaption?: (index: number) => React.ReactNode;
}) {
  const [favoriteOverrides, setFavoriteOverrides] = React.useState<Record<number, boolean>>({});

  return (
    <div
      className={cn(
        'w-full max-w-full min-w-0',
        view === 'grid'
          ? cn(
              'grid grid-cols-2 gap-2 sm:gap-3',
              density === 'compact'
                ? 'grid-cols-2 sm:grid-cols-2 md:grid-cols-2 lg:grid-cols-2 xl:grid-cols-2'
                : 'sm:grid-cols-3 md:grid-cols-4 xl:grid-cols-6'
            )
          : cn(
              'columns-2 gap-2 sm:gap-3',
              density === 'compact'
                ? 'columns-2 sm:columns-2 md:columns-2 lg:columns-2 xl:columns-2'
                : 'sm:columns-3 md:columns-4 xl:columns-6'
            )
      )}
    >
      {cards.map((card, index) => (
        <div
          key={renderCaption ? `${card.id}-${index}` : card.id}
          className={cn(
            'bg-muted group relative w-full max-w-full min-w-0 overflow-hidden rounded-xl border',
            view === 'grid'
              ? !renderCaption && 'block aspect-square'
              : 'mb-3 block break-inside-avoid'
          )}
        >
          <a
            href={`/cards/${card.id}`}
            onClick={onNavigate}
            aria-label={`Open ${card.title || card.type} #${card.id}`}
            className={cn(
              'block',
              view === 'grid'
                ? renderCaption ? 'aspect-square w-full' : 'size-full'
                : 'w-full'
            )}
          >
            <CardPreview url={card.previewUrl} title={card.title} type={card.type}
              imageClassName={cn('w-full object-cover', view === 'grid' ? 'size-full' : 'h-auto')}
              fallbackClassName="aspect-square" />
          </a>
          {!renderCaption ? <div aria-hidden="true" className="pointer-events-none absolute inset-x-0 bottom-0 truncate bg-gradient-to-t from-black/75 to-transparent px-3 pb-2 pt-7 text-sm text-white opacity-0 transition-opacity group-hover:opacity-100 group-focus-within:opacity-100">
            {card.title || card.type}
          </div> : null}
          {card.isFavorite !== undefined ? (
            <FavoriteToggle
              cardId={card.id}
              isFavorite={favoriteOverrides[card.id] ?? card.isFavorite}
              className="absolute right-2 top-2 bg-background/90"
              onChange={(isFavorite) =>
                setFavoriteOverrides((current) => ({ ...current, [card.id]: isFavorite }))
              }
            />
          ) : null}
          {renderCaption ? renderCaption(index) : null}
        </div>
      ))}
    </div>
  );
}
