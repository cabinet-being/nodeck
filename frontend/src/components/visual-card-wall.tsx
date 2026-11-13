import * as React from 'react';
import { Grid3X3, LayoutGrid, SlidersHorizontal } from 'lucide-react';

import { CardTile, type CardTileSelection, type VisualCard } from '@/components/card-tile';
import { Button } from '@/components/ui/button';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { Segmented } from '@/components/ui/segmented';
import { Skeleton } from '@/components/ui/skeleton';
import { cn } from '@/lib/utils';

export type { VisualCard };
export type VisualCardView = 'grid' | 'brick';
export type VisualCardDensity = 'default' | 'compact';

const storageKey = 'nodeck.cardView';
const legacyStorageKey = 'nodeck.galleryView';
const listeners = new Set<() => void>();

function readView(): VisualCardView {
  try {
    const value = window.localStorage.getItem(storageKey) ?? window.localStorage.getItem(legacyStorageKey);
    return value === 'brick' ? 'brick' : 'grid';
  } catch {
    return 'grid';
  }
}

let currentView: VisualCardView = typeof window === 'undefined' ? 'grid' : readView();

function setCardView(view: VisualCardView) {
  currentView = view;
  try {
    window.localStorage.setItem(storageKey, view);
  } catch {
    // Storage can be unavailable; the choice still applies for this session.
  }
  listeners.forEach((listener) => listener());
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** One layout preference shared by every card wall. */
export function useVisualCardView() {
  const view = React.useSyncExternalStore(subscribe, () => currentView, () => 'grid' as VisualCardView);
  return [view, setCardView] as const;
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

/** Placeholder tiles that hold the wall's layout while a page loads. */
export function VisualCardWallSkeleton({
  count = 12,
  density = 'default',
}: {
  count?: number;
  density?: VisualCardDensity;
}) {
  return (
    <div
      role="status"
      aria-label="Loading"
      className={cn(
        'grid w-full max-w-full min-w-0 grid-cols-2 gap-3 sm:gap-4',
        density === 'default' && 'sm:grid-cols-3 md:grid-cols-4 xl:grid-cols-6'
      )}
    >
      {Array.from({ length: count }, (_, index) => <Skeleton key={index} className="aspect-square rounded-2xl" />)}
    </div>
  );
}

export function VisualCardWall({
  cards,
  view,
  density = 'default',
  onNavigate,
  renderCaption,
  renderActions,
  onFavoriteChange,
  selection,
}: {
  cards: VisualCard[];
  view: VisualCardView;
  density?: VisualCardDensity;
  onNavigate?: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  renderCaption?: (index: number) => React.ReactNode;
  /** Extra round port buttons for a tile. */
  renderActions?: (index: number) => React.ReactNode;
  onFavoriteChange?: (index: number, isFavorite: boolean) => void;
  /** Selection mode: tiles toggle their selection instead of opening. */
  selection?: (index: number) => CardTileSelection;
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
          onFavoriteChange={onFavoriteChange ? (isFavorite) => onFavoriteChange(index, isFavorite) : undefined}
          selection={selection?.(index)}
        />
      ))}
    </div>
  );
}
