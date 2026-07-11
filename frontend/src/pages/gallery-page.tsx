import * as React from 'react';
import { Grid3X3, LayoutGrid } from 'lucide-react';

import { listCards, resolveApiUrl, type Card } from '@/api/cards';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';

type GalleryView = 'grid' | 'brick';

const galleryViewStorageKey = 'nodeck.galleryView';

export function GalleryPage({
  onNavigate,
}: {
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const [cards, setCards] = React.useState<Card[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [view, setView] = React.useState<GalleryView>(() => {
    return window.localStorage.getItem(galleryViewStorageKey) === 'brick' ? 'brick' : 'grid';
  });

  React.useEffect(() => {
    listCards({
      type: 'media',
      mediaType: 'image',
      sort: 'created_at',
      order: 'desc',
    })
      .then(setCards)
      .catch((requestError) => setError(requestError.message));
  }, []);

  React.useEffect(() => {
    window.localStorage.setItem(galleryViewStorageKey, view);
  }, [view]);

  return (
    <section className="flex flex-col gap-4 p-6">
      <div className="flex justify-end">
        <div className="bg-muted flex rounded-lg p-1">
          <Button
            type="button"
            variant={view === 'grid' ? 'secondary' : 'ghost'}
            size="icon"
            aria-label="Grid view"
            onClick={() => setView('grid')}
          >
            <Grid3X3 className="size-4" />
          </Button>
          <Button
            type="button"
            variant={view === 'brick' ? 'secondary' : 'ghost'}
            size="icon"
            aria-label="Brick view"
            onClick={() => setView('brick')}
          >
            <LayoutGrid className="size-4" />
          </Button>
        </div>
      </div>

      {error ? <div className="text-destructive mb-4 text-sm">{error}</div> : null}

      <div
        className={cn(
          view === 'grid'
            ? 'grid grid-cols-2 gap-3 sm:grid-cols-3 md:grid-cols-4 xl:grid-cols-6'
            : 'columns-2 gap-3 sm:columns-3 md:columns-4 xl:columns-6'
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
            <img
              src={resolveApiUrl(card.previewUrl)}
              alt=""
              className={cn(
                'w-full object-cover',
                view === 'grid' ? 'size-full' : 'h-auto'
              )}
              loading="lazy"
            />
          </a>
        ))}
      </div>
    </section>
  );
}
