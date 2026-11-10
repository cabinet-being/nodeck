import * as React from 'react';
import { Search, X } from 'lucide-react';

import { listSources, listTags, type Source, type Tag } from '@/api/catalog';
import { TagChip } from '@/components/tag-chip';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';

export type CatalogFilters = {
  search: string;
  tags: Tag[];
  source: Source | null;
};

export function CatalogFilterBar({
  filters,
  legacyActive = false,
  onChange,
  onClearAll,
}: {
  filters: CatalogFilters;
  legacyActive?: boolean;
  onChange: (filters: CatalogFilters) => void;
  onClearAll: () => void;
}) {
  const [searchDraft, setSearchDraft] = React.useState(filters.search);
  const [tagSearch, setTagSearch] = React.useState('');
  const [sourceSearch, setSourceSearch] = React.useState('');
  const [tagChoices, setTagChoices] = React.useState<Tag[]>([]);
  const [sourceChoices, setSourceChoices] = React.useState<Source[]>([]);
  const [lookupError, setLookupError] = React.useState<string | null>(null);

  React.useEffect(() => { setSearchDraft(filters.search); }, [filters.search]);

  React.useEffect(() => {
    if (!tagSearch.trim()) { setTagChoices([]); return; }
    let active = true;
    listTags(tagSearch)
      .then((results) => { if (active) { setTagChoices(results); setLookupError(null); } })
      .catch((cause) => { if (active) setLookupError(cause instanceof Error ? cause.message : 'Unable to search tags.'); });
    return () => { active = false; };
  }, [tagSearch]);

  React.useEffect(() => {
    if (!sourceSearch.trim()) { setSourceChoices([]); return; }
    let active = true;
    listSources(sourceSearch)
      .then((results) => { if (active) { setSourceChoices(results); setLookupError(null); } })
      .catch((cause) => { if (active) setLookupError(cause instanceof Error ? cause.message : 'Unable to search sources.'); });
    return () => { active = false; };
  }, [sourceSearch]);

  const hasActive = filters.search.trim() !== '' || filters.tags.length > 0 ||
    filters.source !== null || legacyActive;

  function clearAll() {
    setSearchDraft('');
    setTagSearch('');
    setSourceSearch('');
    setLookupError(null);
    onClearAll();
  }

  return (
    <div className="grid max-w-3xl gap-3" role="search" aria-label="Filter cards">
      <form className="flex gap-2" onSubmit={(event) => {
        event.preventDefault();
        onChange({ ...filters, search: searchDraft.trim() });
      }}>
        <div className="relative flex-1">
          <Input type="search" aria-label="Search cards and tags" placeholder="Search titles or tags"
            className="pr-9 [&::-webkit-search-cancel-button]:hidden"
            value={searchDraft} onChange={(event) => setSearchDraft(event.target.value)} />
          {searchDraft ? <Button type="button" size="icon-xs" variant="ghost"
            className="absolute top-1/2 right-1.5 -translate-y-1/2" aria-label="Clear search" title="Clear search"
            onClick={() => {
              setSearchDraft('');
              if (filters.search) onChange({ ...filters, search: '' });
            }}><X className="size-3" /></Button> : null}
        </div>
        <Button type="submit" size="icon" variant="outline" aria-label="Search" title="Search">
          <Search className="size-4" />
        </Button>
      </form>

      <div className="grid gap-3 sm:grid-cols-2">
        <div className="relative">
          <Input type="search" aria-label="Find tag filter" placeholder="Filter by tag name"
            value={tagSearch} onChange={(event) => setTagSearch(event.target.value)} />
          {tagSearch.trim() && tagChoices.length > 0 ? <div className="bg-background absolute top-full z-20 mt-1 grid max-h-64 w-full overflow-auto rounded-lg border p-1 shadow-lg">
            {tagChoices.filter((tag) => !filters.tags.some((selected) => selected.id === tag.id))
              .map((tag) => <Button key={tag.id} type="button" variant="ghost" className="justify-start"
                onClick={() => {
                  setTagSearch('');
                  onChange({ ...filters, search: searchDraft.trim(), tags: [...filters.tags, tag] });
                }}>
                {tag.name}{tag.nameConflict ? ' · legacy duplicate' : ''}
              </Button>)}
          </div> : null}
        </div>
        <div className="relative">
          <Input type="search" aria-label="Find source filter" placeholder="Filter by source name"
            value={sourceSearch} onChange={(event) => setSourceSearch(event.target.value)} />
          {sourceSearch.trim() && sourceChoices.length > 0 ? <div className="bg-background absolute top-full z-20 mt-1 grid max-h-64 w-full overflow-auto rounded-lg border p-1 shadow-lg">
            {sourceChoices.map((source) => <Button key={source.id} type="button" variant="ghost"
              className="justify-start" onClick={() => {
                setSourceSearch('');
                onChange({ ...filters, search: searchDraft.trim(), source });
              }}>
              {source.name}{source.nameConflict ? ' · legacy duplicate' : ''}
            </Button>)}
          </div> : null}
        </div>
      </div>

      {lookupError ? <p className="text-destructive text-sm" role="alert">{lookupError}</p> : null}

      {hasActive ? <div className="flex flex-wrap items-center gap-2" aria-label="Active filters">
        {filters.search.trim() ? <span className="bg-muted inline-flex items-center gap-1 rounded-full py-0.5 pr-0.5 pl-2.5 text-xs">
          Search: {filters.search}
          <Button type="button" size="icon-xs" variant="ghost" aria-label="Remove search filter"
            onClick={() => { setSearchDraft(''); onChange({ ...filters, search: '' }); }}><X className="size-3" /></Button>
        </span> : null}
        {filters.tags.map((tag) => <span key={tag.id} className="inline-flex items-center gap-1">
          <TagChip name={tag.name} category={tag.category} origin={tag.origin} />
          <Button type="button" size="icon-xs" variant="ghost" aria-label={`Remove ${tag.name} filter`}
            onClick={() => onChange({ ...filters, tags: filters.tags.filter((item) => item.id !== tag.id) })}>
            <X className="size-3" />
          </Button>
        </span>)}
        {filters.source ? <span className="bg-muted inline-flex items-center gap-1 rounded-full py-0.5 pr-0.5 pl-2.5 text-xs">
          Source: {filters.source.name}
          <Button type="button" size="icon-xs" variant="ghost" aria-label="Remove source filter"
            onClick={() => onChange({ ...filters, source: null })}><X className="size-3" /></Button>
        </span> : null}
        {legacyActive ? <span className="text-muted-foreground text-xs">
          Legacy filters are active. Choose a new filter to replace them.
        </span> : null}
        <Button type="button" size="sm" variant="outline" onClick={clearAll}>Clear all</Button>
      </div> : null}
    </div>
  );
}
