import * as React from 'react';

import { listCards, resolveApiUrl, type Card } from '@/api/cards';

export function GalleryPage({
  onNavigate,
}: {
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const [cards, setCards] = React.useState<Card[]>([]);
  const [error, setError] = React.useState<string | null>(null);

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

  return (
    <section className="p-6">
      {error ? <div className="text-destructive mb-4 text-sm">{error}</div> : null}

      <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 md:grid-cols-4 xl:grid-cols-6">
        {cards.map((card) => (
          <a
            key={card.id}
            href={`/cards/${card.id}`}
            onClick={onNavigate}
            className="bg-muted block aspect-square overflow-hidden rounded-lg border"
          >
            <img
              src={resolveApiUrl(card.previewUrl)}
              alt=""
              className="size-full object-cover"
              loading="lazy"
            />
          </a>
        ))}
      </div>
    </section>
  );
}
