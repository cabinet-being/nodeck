import * as React from 'react';
import { Grid3X3, LayoutGrid } from 'lucide-react';

import { resolveApiUrl, type Card } from '@/api/cards';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';

export type VisualCardView = 'grid' | 'brick';
export type VisualCardDensity = 'default' | 'compact';

export const visualCardViewStorageKey = 'nodeck.visualCardView';

export type VisualCard = Pick<Card, 'id' | 'type' | 'title' | 'previewUrl'>;

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
  return (
    <div
      className={cn(
        view === 'grid'
          ? cn(
              'grid grid-cols-2 gap-3 sm:grid-cols-3',
              density === 'compact' ? 'xl:grid-cols-4' : 'md:grid-cols-4 xl:grid-cols-6'
            )
          : cn(
              'columns-2 gap-3 sm:columns-3',
              density === 'compact' ? 'xl:columns-4' : 'md:columns-4 xl:columns-6'
            )
      )}
    >
      {cards.map((card) => (
        <a
          key={card.id}
          href={`/cards/${card.id}`}
          onClick={onNavigate}
          className={cn(
            'bg-muted overflow-hidden rounded-lg border',
            view === 'grid' ? 'block aspect-square' : 'mb-3 block break-inside-avoid'
          )}
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
      ))}
    </div>
  );
}
