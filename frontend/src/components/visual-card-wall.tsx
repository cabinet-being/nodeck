import * as React from 'react';
import { Grid3X3, LayoutGrid, SlidersHorizontal } from 'lucide-react';

import { CardTile, type VisualCard } from '@/components/card-tile';
import { Button } from '@/components/ui/button';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { Segmented } from '@/components/ui/segmented';
import { cn } from '@/lib/utils';

export type { VisualCard };
export type VisualCardView = 'grid' | 'brick';
export type VisualCardDensity = 'default' | 'compact';

export const visualCardViewStorageKey = 'nodeck.visualCardView';

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
    <Popover>
      <PopoverTrigger render={<Button type="button" variant="outline" size="sm" />}>
        <SlidersHorizontal className="size-3.5" aria-hidden="true" />
        View
      </PopoverTrigger>
      <PopoverContent>
        <Segmented
          ariaLabel="Layout"
          value={view}
          onValueChange={onChange}
          options={[
            { value: 'grid', label: 'Grid', icon: <Grid3X3 /> },
            { value: 'brick', label: 'Masonry', icon: <LayoutGrid /> },
          ]}
        />
      </PopoverContent>
    </Popover>
  );
}

export function VisualCardWall({
  cards,
  view,
  density = 'default',
  onNavigate,
  renderCaption,
  renderActions,
}: {
  cards: VisualCard[];
  view: VisualCardView;
  density?: VisualCardDensity;
  onNavigate?: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  renderCaption?: (index: number) => React.ReactNode;
  /** Extra round port buttons for a tile. */
  renderActions?: (index: number) => React.ReactNode;
}) {
  return (
    <div
      className={cn(
        'w-full max-w-full min-w-0',
        view === 'grid'
          ? cn(
              'grid grid-cols-2 gap-3 sm:gap-4',
              density === 'default' && 'sm:grid-cols-3 md:grid-cols-4 xl:grid-cols-6'
            )
          : cn(
              'columns-2 gap-3 sm:gap-4',
              density === 'default' && 'sm:columns-3 md:columns-4 xl:columns-6'
            )
      )}
    >
      {cards.map((card, index) => (
        <CardTile
          key={renderCaption || renderActions ? `${card.id}-${index}` : card.id}
          card={card}
          layout={view}
          onNavigate={onNavigate}
          caption={renderCaption ? renderCaption(index) : undefined}
          actions={renderActions ? renderActions(index) : undefined}
        />
      ))}
    </div>
  );
}
