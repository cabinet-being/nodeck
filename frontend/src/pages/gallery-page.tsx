import * as React from 'react';
import { X } from 'lucide-react';

import { listCards, type Card } from '@/api/cards';
import { getSource, getTag, listSources, listTags, type Source, type Tag } from '@/api/catalog';
import { TagChip } from '@/components/tag-chip';
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
  const [selectedTags, setSelectedTags] = React.useState<Tag[]>([]);
  const [selectedSource, setSelectedSource] = React.useState<Source | null>(null);
  const [tagSearch, setTagSearch] = React.useState('');
  const [sourceSearch, setSourceSearch] = React.useState('');
  const [tagChoices, setTagChoices] = React.useState<Tag[]>([]);
  const [sourceChoices, setSourceChoices] = React.useState<Source[]>([]);

  React.useEffect(() => {
    let active = true;
    const params = new URLSearchParams(currentSearch);
    const tagIds = parseIds(params.get('catalog_tags'));
    const sourceId = parseSingleId(params.get('catalog_source'));
    Promise.all([
      Promise.all(tagIds.map((id) => getTag(id).catch(() => null))),
      sourceId ? getSource(sourceId).catch(() => null) : Promise.resolve(null),
    ]).then(([tags, source]) => {
      if (!active) return;
      setSelectedTags(tags.filter((tag): tag is Tag => tag !== null));
      setSelectedSource(source);
    });
    return () => { active = false; };
  }, [currentSearch]);

  React.useEffect(() => {
    const params = new URLSearchParams(currentSearch);
    let active = true;
    setError(null);
    listCards({
      types: ['media', 'comic', 'set'],
      tags: parseIds(params.get('tags')),
      source: parseSingleId(params.get('source')),
      catalogTags: parseIds(params.get('catalog_tags')),
      catalogSource: parseSingleId(params.get('catalog_source')),
      excludeContainedMedia: true,
      sort: 'created_at',
      order: 'desc',
    }).then((results) => { if (active) setCards(results); })
      .catch((cause) => { if (active) setError(cause instanceof Error ? cause.message : 'Unable to load gallery.'); });
    return () => { active = false; };
  }, [currentSearch]);

  React.useEffect(() => {
    if (!tagSearch.trim()) { setTagChoices([]); return; }
    let active = true;
    listTags(tagSearch).then((results) => { if (active) setTagChoices(results); })
      .catch((cause) => { if (active) setError(cause instanceof Error ? cause.message : 'Unable to search tags.'); });
    return () => { active = false; };
  }, [tagSearch]);

  React.useEffect(() => {
    if (!sourceSearch.trim()) { setSourceChoices([]); return; }
    let active = true;
    listSources(sourceSearch).then((results) => { if (active) setSourceChoices(results); })
      .catch((cause) => { if (active) setError(cause instanceof Error ? cause.message : 'Unable to search sources.'); });
    return () => { active = false; };
  }, [sourceSearch]);

  function applyFilters() {
    const params = new URLSearchParams();
    if (selectedTags.length > 0) params.set('catalog_tags', selectedTags.map((tag) => tag.id).join(','));
    if (selectedSource) params.set('catalog_source', String(selectedSource.id));
    onNavigateTo(`/gallery${params.toString() ? `?${params.toString()}` : ''}`);
  }

  function clearFilters() {
    setSelectedTags([]);
    setSelectedSource(null);
    setTagSearch('');
    setSourceSearch('');
    onNavigateTo('/gallery');
  }

  const hasLegacyFilters = new URLSearchParams(currentSearch).has('tags') ||
    new URLSearchParams(currentSearch).has('source');

  return (
    <section className="flex flex-col gap-4 p-6">
      <div className="grid gap-3 xl:grid-cols-[1fr_auto]">
        <div className="grid gap-3 md:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto_auto]">
          <div className="relative grid content-start gap-2">
            <div className="flex flex-wrap gap-1">
              {selectedTags.map((tag) => <span key={tag.id} className="inline-flex items-center gap-1">
                <TagChip name={tag.name} category={tag.category} origin={tag.origin} />
                <Button type="button" size="icon-xs" variant="ghost" aria-label={`Remove ${tag.name} filter`}
                  onClick={() => setSelectedTags((current) => current.filter((item) => item.id !== tag.id))}>
                  <X className="size-3" />
                </Button>
              </span>)}
            </div>
            <Input type="search" aria-label="Find tag filter" placeholder="Find tags"
              value={tagSearch} onChange={(event) => setTagSearch(event.target.value)} />
            {tagSearch && tagChoices.length > 0 ? <div className="bg-background absolute top-full z-20 mt-1 grid max-h-64 w-full overflow-auto rounded-lg border p-1 shadow-lg">
              {tagChoices.filter((tag) => !selectedTags.some((selected) => selected.id === tag.id))
                .map((tag) => <Button key={tag.id} type="button" variant="ghost" className="justify-start"
                  onClick={() => { setSelectedTags((current) => [...current, tag]); setTagSearch(''); }}>
                  {tag.name}{tag.nameConflict ? ' · legacy duplicate' : ''}
                </Button>)}
            </div> : null}
          </div>

          <div className="relative grid content-start gap-2">
            {selectedSource ? <div className="flex items-center gap-1 text-sm">
              <span className="truncate">{selectedSource.name}</span>
              <Button type="button" size="icon-xs" variant="ghost" aria-label="Remove source filter"
                onClick={() => setSelectedSource(null)}><X className="size-3" /></Button>
            </div> : null}
            <Input type="search" aria-label="Find source filter" placeholder="Find source"
              value={sourceSearch} onChange={(event) => setSourceSearch(event.target.value)} />
            {sourceSearch && sourceChoices.length > 0 ? <div className="bg-background absolute top-full z-20 mt-1 grid max-h-64 w-full overflow-auto rounded-lg border p-1 shadow-lg">
              {sourceChoices.map((source) => <Button key={source.id} type="button" variant="ghost"
                className="justify-start" onClick={() => { setSelectedSource(source); setSourceSearch(''); }}>
                {source.name}{source.nameConflict ? ' · legacy duplicate' : ''}
              </Button>)}
            </div> : null}
          </div>
          <Button type="button" variant="outline" onClick={applyFilters}>Apply</Button>
          <Button type="button" variant="ghost" onClick={clearFilters}>Clear</Button>
        </div>
        <VisualCardViewToggle view={view} onChange={setView} />
      </div>
      {hasLegacyFilters ? <p className="text-muted-foreground text-xs">Legacy filters are active. Apply a new selection to replace them.</p> : null}
      {error ? <p className="text-destructive text-sm">{error}</p> : null}
      <VisualCardWall cards={cards} view={view} onNavigate={onNavigate} />
    </section>
  );
}

function parseIds(value: string | null) {
  if (!value) return [];
  return value.split(',').map((part) => Number(part.trim()))
    .filter((id) => Number.isInteger(id) && id > 0);
}

function parseSingleId(value: string | null) {
  const id = Number(value);
  return Number.isInteger(id) && id > 0 ? id : undefined;
}
