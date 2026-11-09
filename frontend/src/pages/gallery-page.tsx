import * as React from 'react';
import { RotateCcw, Search, SlidersHorizontal, X } from 'lucide-react';

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

const pageSize = 48;

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
  const [hasNextPage, setHasNextPage] = React.useState(false);
  const [loading, setLoading] = React.useState(true);
  const [error, setError] = React.useState<string | null>(null);
  const [view, setView] = useVisualCardView('nodeck.galleryView');
  const [selectedTags, setSelectedTags] = React.useState<Tag[]>([]);
  const [selectedSource, setSelectedSource] = React.useState<Source | null>(null);
  const [searchDraft, setSearchDraft] = React.useState('');
  const [tagSearch, setTagSearch] = React.useState('');
  const [sourceSearch, setSourceSearch] = React.useState('');
  const [tagChoices, setTagChoices] = React.useState<Tag[]>([]);
  const [sourceChoices, setSourceChoices] = React.useState<Source[]>([]);
  const page = parsePage(new URLSearchParams(currentSearch).get('page'));

  React.useEffect(() => {
    let active = true;
    const params = new URLSearchParams(currentSearch);
    const tagIds = parseIds(params.get('catalog_tags'));
    const sourceId = parseSingleId(params.get('catalog_source'));
    setSearchDraft(params.get('search') ?? '');
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
    setLoading(true);
    setCards([]);
    setHasNextPage(false);
    listCards({
      types: ['media', 'comic', 'set'],
      tags: parseIds(params.get('tags')),
      source: parseSingleId(params.get('source')),
      catalogTags: parseIds(params.get('catalog_tags')),
      catalogSource: parseSingleId(params.get('catalog_source')),
      search: params.get('search')?.trim() || undefined,
      excludeContainedMedia: true,
      sort: 'created_at',
      order: 'desc',
      limit: pageSize + 1,
      offset: (page - 1) * pageSize,
    }).then((results) => {
      if (!active) return;
      setCards(results.slice(0, pageSize));
      setHasNextPage(results.length > pageSize);
    })
      .catch((cause) => { if (active) setError(cause instanceof Error ? cause.message : 'Unable to load gallery.'); })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [currentSearch, page]);

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

  function applyFilters(tags = selectedTags, source = selectedSource, search = searchDraft) {
    const params = new URLSearchParams();
    if (search.trim()) params.set('search', search.trim());
    if (tags.length > 0) params.set('catalog_tags', tags.map((tag) => tag.id).join(','));
    if (source) params.set('catalog_source', String(source.id));
    onNavigateTo(`/gallery${params.toString() ? `?${params.toString()}` : ''}`);
  }

  function goToPage(nextPage: number) {
    const params = new URLSearchParams(currentSearch);
    if (nextPage <= 1) params.delete('page');
    else params.set('page', String(nextPage));
    onNavigateTo(`/gallery${params.toString() ? `?${params.toString()}` : ''}`);
  }

  function clearFilters() {
    setSelectedTags([]);
    setSelectedSource(null);
    setSearchDraft('');
    setTagSearch('');
    setSourceSearch('');
    onNavigateTo('/gallery');
  }

  const hasLegacyFilters = new URLSearchParams(currentSearch).has('tags') ||
    new URLSearchParams(currentSearch).has('source');

  return (
    <section className="flex flex-col gap-5 px-4 py-6 sm:px-6 sm:py-8">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-2xl font-semibold tracking-tight">Gallery</h1>
        <VisualCardViewToggle view={view} onChange={setView} />
      </div>
      <form className="flex max-w-2xl gap-2" onSubmit={(event) => { event.preventDefault(); applyFilters(); }}>
        <Input type="search" aria-label="Search cards and tags" placeholder="Search titles or tags"
          value={searchDraft} onChange={(event) => setSearchDraft(event.target.value)} />
        <Button type="submit" size="icon" variant="outline" aria-label="Search gallery" title="Search"><Search className="size-4" /></Button>
      </form>
      <details className="max-w-2xl rounded-xl border px-4 py-3">
        <summary className="flex cursor-pointer list-none items-center gap-2 text-sm font-medium">
          <SlidersHorizontal className="size-4" /> Filters
          {selectedTags.length + (selectedSource ? 1 : 0) > 0 ? <span className="bg-muted rounded-full px-2 py-0.5 text-xs">{selectedTags.length + (selectedSource ? 1 : 0)}</span> : null}
        </summary>
      <div className="grid gap-3 pt-4">
        <div className="grid gap-3 md:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto_auto]">
          <div className="relative grid content-start gap-2">
            <div className="flex flex-wrap gap-1">
              {selectedTags.map((tag) => <span key={tag.id} className="inline-flex items-center gap-1">
                <TagChip name={tag.name} category={tag.category} origin={tag.origin} />
                <Button type="button" size="icon-xs" variant="ghost" aria-label={`Remove ${tag.name} filter`}
                  onClick={() => {
                    const next = selectedTags.filter((item) => item.id !== tag.id);
                    setSelectedTags(next);
                    applyFilters(next);
                  }}>
                  <X className="size-3" />
                </Button>
              </span>)}
            </div>
            <Input type="search" aria-label="Find tag filter" placeholder="Find tags"
              value={tagSearch} onChange={(event) => setTagSearch(event.target.value)} />
            {tagSearch && tagChoices.length > 0 ? <div className="bg-background absolute top-full z-20 mt-1 grid max-h-64 w-full overflow-auto rounded-lg border p-1 shadow-lg">
              {tagChoices.filter((tag) => !selectedTags.some((selected) => selected.id === tag.id))
                .map((tag) => <Button key={tag.id} type="button" variant="ghost" className="justify-start"
                  onClick={() => {
                    const next = [...selectedTags, tag];
                    setSelectedTags(next);
                    setTagSearch('');
                    applyFilters(next);
                  }}>
                  {tag.name}{tag.nameConflict ? ' · legacy duplicate' : ''}
                </Button>)}
            </div> : null}
          </div>

          <div className="relative grid content-start gap-2">
            {selectedSource ? <div className="flex items-center gap-1 text-sm">
              <span className="truncate">{selectedSource.name}</span>
              <Button type="button" size="icon-xs" variant="ghost" aria-label="Remove source filter"
                onClick={() => { setSelectedSource(null); applyFilters(selectedTags, null); }}><X className="size-3" /></Button>
            </div> : null}
            <Input type="search" aria-label="Find source filter" placeholder="Find source"
              value={sourceSearch} onChange={(event) => setSourceSearch(event.target.value)} />
            {sourceSearch && sourceChoices.length > 0 ? <div className="bg-background absolute top-full z-20 mt-1 grid max-h-64 w-full overflow-auto rounded-lg border p-1 shadow-lg">
              {sourceChoices.map((source) => <Button key={source.id} type="button" variant="ghost"
                className="justify-start" onClick={() => {
                  setSelectedSource(source);
                  setSourceSearch('');
                  applyFilters(selectedTags, source);
                }}>
                {source.name}{source.nameConflict ? ' · legacy duplicate' : ''}
              </Button>)}
            </div> : null}
          </div>
          <Button type="button" variant="outline" onClick={() => applyFilters()}>Apply</Button>
          <Button type="button" variant="ghost" onClick={clearFilters} aria-label="Clear filters" title="Clear filters"><RotateCcw className="size-4" /><span className="sr-only">Clear filters</span></Button>
        </div>
      </div>
      </details>
      {hasLegacyFilters ? <p className="text-muted-foreground text-xs">Legacy filters are active. Apply a new selection to replace them.</p> : null}
      {error ? <p className="text-destructive text-sm">{error}</p> : null}
      {loading ? <p className="text-muted-foreground text-sm">Loading gallery…</p> : null}
      {!loading && !error && cards.length === 0 ?
        <p className="text-muted-foreground rounded-lg border p-6 text-center text-sm">No cards match this view.</p> : null}
      {!loading && cards.length > 0 ? <VisualCardWall cards={cards} view={view} onNavigate={onNavigate} /> : null}
      {!error && (page > 1 || hasNextPage) ? <nav className="flex items-center justify-center gap-3 py-2" aria-label="Gallery pages">
        <Button type="button" variant="outline" disabled={loading || page <= 1}
          onClick={() => goToPage(page - 1)}>Previous</Button>
        <span className="text-muted-foreground text-sm">Page {page}</span>
        <Button type="button" variant="outline" disabled={loading || !hasNextPage}
          onClick={() => goToPage(page + 1)}>Next</Button>
      </nav> : null}
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

function parsePage(value: string | null) {
  const page = Number(value);
  return Number.isInteger(page) && page > 0 && page <= 100_000 ? page : 1;
}
