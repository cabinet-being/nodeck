import * as React from 'react';

import { listCards, type Card } from '@/api/cards';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import {
  useVisualCardView,
  VisualCardViewToggle,
  VisualCardWall,
} from '@/components/visual-card-wall';

export function GalleryPage({
  currentSearch,
  onNavigate,
  onNavigateTo,
}: {
  currentSearch: string;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onNavigateTo: (path: string) => void;
}) {
  const [cards, setCards] = React.useState<Card[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [view, setView] = useVisualCardView('nodeck.galleryView');
  const [tagIdsInput, setTagIdsInput] = React.useState('');
  const [sourceIdInput, setSourceIdInput] = React.useState('');

  React.useEffect(() => {
    const searchParams = new URLSearchParams(currentSearch);
    setTagIdsInput(searchParams.get('tags') ?? '');
    setSourceIdInput(searchParams.get('source') ?? '');
  }, [currentSearch]);

  React.useEffect(() => {
    const searchParams = new URLSearchParams(currentSearch);
    setError(null);

    listCards({
      types: ['media', 'comic', 'set'],
      tags: parseIds(searchParams.get('tags')),
      source: parseSingleId(searchParams.get('source')),
      excludeContainedMedia: true,
      sort: 'created_at',
      order: 'desc',
    })
      .then(setCards)
      .catch((requestError) => setError(requestError.message));
  }, [currentSearch]);

  return (
    <section className="flex flex-col gap-4 p-6">
      <div className="grid gap-3 xl:grid-cols-[1fr_auto]">
        <div className="grid gap-3 md:grid-cols-[1fr_12rem_auto_auto]">
          <Input
            value={tagIdsInput}
            onChange={(event) => setTagIdsInput(event.target.value)}
            placeholder="Tag IDs, comma separated"
          />
          <Input
            value={sourceIdInput}
            onChange={(event) => setSourceIdInput(event.target.value)}
            placeholder="Source ID"
          />
          <Button type="button" variant="outline" onClick={applyFilters}>
            Apply filters
          </Button>
          <Button type="button" variant="ghost" onClick={clearFilters}>
            Clear
          </Button>
        </div>

        <VisualCardViewToggle view={view} onChange={setView} />
      </div>

      {error ? <div className="text-destructive mb-4 text-sm">{error}</div> : null}

      <VisualCardWall cards={cards} view={view} onNavigate={onNavigate} />
    </section>
  );

  function applyFilters() {
    const searchParams = new URLSearchParams();
    const tags = parseIds(tagIdsInput);
    const source = parseSingleId(sourceIdInput);

    if (tags.length > 0) {
      searchParams.set('tags', tags.join(','));
    }

    if (source) {
      searchParams.set('source', String(source));
    }

    onNavigateTo(`/gallery${searchParams.toString() ? `?${searchParams.toString()}` : ''}`);
  }

  function clearFilters() {
    setTagIdsInput('');
    setSourceIdInput('');
    onNavigateTo('/gallery');
  }
}

function parseIds(value: string | null) {
  if (!value) {
    return [];
  }

  return value
    .split(',')
    .map((item) => Number(item.trim()))
    .filter((item) => Number.isInteger(item) && item > 0);
}

function parseSingleId(value: string | null) {
  const parsed = Number(value);

  return Number.isInteger(parsed) && parsed > 0 ? parsed : undefined;
}
