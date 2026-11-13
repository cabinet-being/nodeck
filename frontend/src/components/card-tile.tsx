import * as React from 'react';
import { BookOpen, Film, Layers, Play } from 'lucide-react';


import { type Card } from '@/api/cards';
import { CardPreview } from '@/components/card-preview';
import { FavoriteToggle } from '@/components/favorite-toggle';
import { cn } from '@/lib/utils';

export type VisualCard = Pick<Card, 'id' | 'type' | 'title' | 'previewUrl'> & {
  isFavorite?: Card['isFavorite'];
  contentUrl?: Card['contentUrl'];
  containedCards?: Card['containedCards'];
  metadata?: Card['metadata'];
  parentCardId?: Card['parentCardId'];
};

/** Selection state of one tile while a wall is in selection mode. */
export type CardTileSelection = {
  /** 1-based place in the selection, or null when the card is not selected. */
  order: number | null;
  /** Why the card cannot be selected; null when it can. */
  disabledReason: string | null;
  onToggle: () => void;
};

export type CardTileLayout = 'grid' | 'brick' | 'rail';

export function cardLabel(card: Pick<VisualCard, 'type' | 'title'>) {
  const title = card.title?.trim();
  if (title) return title;
  if (card.type === 'comic') return 'Untitled comic';
  if (card.type === 'set') return 'Untitled set';
  return 'Untitled';
}

function metadataRatio(card: VisualCard) {
  const width = Number(card.metadata?.width);
  const height = Number(card.metadata?.height);
  return Number.isFinite(width) && Number.isFinite(height) && width > 0 && height > 0 ? width / height : null;
}

function typeBadge(card: VisualCard) {
  if (card.type === 'comic') return { Icon: BookOpen, text: 'Comic', count: card.containedCards?.length };
  if (card.type === 'set') return { Icon: Layers, text: 'Set', count: card.containedCards?.length };
  const url = card.contentUrl?.toLowerCase().split('?')[0] ?? '';
  if (/\.(mp4|webm|mov|m4v|mkv)$/.test(url)) return { Icon: Play, text: 'Video', count: undefined };
  if (url.endsWith('.gif')) return { Icon: Film, text: 'GIF', count: undefined };
  return null;
}

export function CardTile({
  card,
  layout,
  onNavigate,
  caption,
  actions,
  showFavorite = true,
  onFavoriteChange,
  selection,
  className,
}: {
  card: VisualCard;
  layout: CardTileLayout;
  onNavigate?: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  /** Optional block rendered below the media (the tile then shows no overlay title). */
  caption?: React.ReactNode;
  /** Extra round port buttons shown next to the favorite button. */
  actions?: React.ReactNode;
  showFavorite?: boolean;
  /** Called after the favorite button added or removed the card. */
  onFavoriteChange?: (isFavorite: boolean) => void;
  /** Selection mode: the tile toggles its selection instead of opening the card. */
  selection?: CardTileSelection;
  className?: string;
}) {
  const [favoriteOverride, setFavoriteOverride] = React.useState<boolean | null>(null);
  const label = cardLabel(card);
  const badge = typeBadge(card);
  const isFavorite = favoriteOverride ?? card.isFavorite ?? false;
  const [loadedUrl, setLoadedUrl] = React.useState<string | null>(null);
  const ratio = metadataRatio(card);
  const loaded = loadedUrl === card.previewUrl;
  const hasFavorite = !selection && showFavorite && card.isFavorite !== undefined;
  const selected = selection?.order != null;

  return (
    <div
      className={cn(
        'group bg-surface shadow-1 relative min-w-0 rounded-2xl border transition-[transform,box-shadow] duration-[var(--dur-fast)] ease-out motion-safe:hover:-translate-y-0.5 hover:shadow-2 focus-within:shadow-2',
        layout === 'rail' && 'w-40 shrink-0 snap-start sm:w-44',
        layout === 'brick' && 'mb-3 break-inside-avoid',
        selected && 'ring-primary ring-3',
        selection?.disabledReason && 'opacity-45',
        className
      )}
    >
      <div className={cn('bg-muted relative overflow-hidden', !loaded && card.previewUrl && 'animate-pulse motion-reduce:animate-none', caption ? 'rounded-t-2xl' : 'rounded-2xl')}>
        <a
          href={`/cards/${card.id}`}
          onClick={onNavigate}
          aria-label={`Open ${label}`}
          title={label}
          tabIndex={selection ? -1 : undefined}
          aria-hidden={selection ? true : undefined}
          className={cn('block rounded-[inherit]', layout !== 'brick' && 'aspect-square')}
          style={layout === 'brick' && (ratio !== null || !loaded) ? { aspectRatio: ratio ?? 1 } : undefined}
        >
          <CardPreview
            url={card.previewUrl}
            title={card.title}
            type="No preview"
            imageClassName={cn('w-full object-cover', layout === 'brick' && ratio === null ? 'h-auto' : 'size-full')}
            fallbackClassName={layout === 'brick' ? 'aspect-square' : 'size-full'}
            onLoad={() => setLoadedUrl(card.previewUrl)}
          />
          {!caption ? (
            <span
              aria-hidden="true"
              className="pointer-events-none absolute inset-x-0 bottom-0 block truncate bg-gradient-to-t from-black/70 to-transparent px-3 pt-8 pb-2 text-sm text-white opacity-0 transition-opacity group-hover:opacity-100 group-focus-within:opacity-100 pointer-coarse:opacity-100"
            >
              {label}
            </span>
          ) : null}
        </a>
        {badge ? (
          <span className="pointer-events-none absolute top-2 left-2 inline-flex h-6 items-center gap-1 rounded-full bg-black/60 px-2 text-xs font-medium text-white tabular-nums backdrop-blur-sm">
            <badge.Icon className="size-3" aria-hidden="true" />
            <span className="sr-only">{badge.text}</span>
            {badge.count !== undefined ? <span>{badge.count}</span> : null}
          </span>
        ) : null}
        {selection ? (
          <button
            type="button"
            aria-pressed={selected}
            aria-disabled={selection.disabledReason ? true : undefined}
            aria-label={selection.disabledReason ? `${label}: ${selection.disabledReason}` : `Select ${label}`}
            title={selection.disabledReason ?? label}
            onClick={(event) => {
              event.preventDefault();
              if (!selection.disabledReason) selection.onToggle();
            }}
            className={cn(
              'absolute inset-0 rounded-[inherit] outline-none focus-visible:ring-ring/60 focus-visible:ring-3 focus-visible:ring-inset',
              selection.disabledReason ? 'cursor-not-allowed' : 'cursor-pointer',
              selected && 'bg-primary/15'
            )}
          >
            {selection.disabledReason ? null : (
              <span
                aria-hidden="true"
                className={cn(
                  'absolute top-2 right-2 flex size-8 items-center justify-center rounded-full border-2 text-sm font-semibold tabular-nums shadow-1 pointer-coarse:size-9',
                  selected ? 'bg-primary border-primary text-primary-foreground' : 'border-white bg-black/30'
                )}
              >
                {selection.order ?? ''}
              </span>
            )}
          </button>
        ) : null}
      </div>
      {!selection && (hasFavorite || actions) ? (
        <>
          <span
            aria-hidden="true"
            className="bg-primary pointer-events-none absolute top-3 -right-1 size-2 rounded-full opacity-0 shadow-[0_0_0_3px_color-mix(in_oklch,var(--primary)_25%,transparent)] transition-opacity duration-[var(--dur-base)] group-hover:opacity-100 group-focus-within:opacity-0"
          />
          <div
            className={cn(
              'absolute top-2 right-2 flex items-center gap-1.5 transition-opacity duration-[var(--dur-base)]',
              'opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 pointer-coarse:opacity-100',
              isFavorite && 'opacity-100'
            )}
          >
            {actions}
            {hasFavorite ? (
              <FavoriteToggle
                cardId={card.id}
                isFavorite={isFavorite}
                onChange={(next) => { setFavoriteOverride(next); onFavoriteChange?.(next); }}
                className="bg-surface/95 text-foreground shadow-1 size-8 rounded-full pointer-coarse:size-11"
              />
            ) : null}
          </div>
        </>
      ) : null}
      {caption}
    </div>
  );
}
