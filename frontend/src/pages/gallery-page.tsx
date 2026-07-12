import * as React from 'react';

import { listCards, type Card } from '@/api/cards';
import {
  useVisualCardView,
  VisualCardViewToggle,
  VisualCardWall,
} from '@/components/visual-card-wall';

export function GalleryPage({
  onNavigate,
}: {
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const [cards, setCards] = React.useState<Card[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [view, setView] = useVisualCardView('nodeck.galleryView');

  React.useEffect(() => {
    listCards({
      types: ['media', 'comic', 'set'],
      excludeContainedMedia: true,
      sort: 'created_at',
      order: 'desc',
    })
      .then(setCards)
      .catch((requestError) => setError(requestError.message));
  }, []);

  return (
    <section className="flex flex-col gap-4 p-6">
      <div className="flex justify-end">
        <VisualCardViewToggle view={view} onChange={setView} />
      </div>

      {error ? <div className="text-destructive mb-4 text-sm">{error}</div> : null}

      <VisualCardWall cards={cards} view={view} onNavigate={onNavigate} footer={false} />
    </section>
  );
}
