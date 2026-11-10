import * as React from 'react';
import { Globe, Plus, Search, X } from 'lucide-react';

import { listSources, listTags, type Source, type Tag } from '@/api/catalog';
import { NodeChip } from '@/components/node-chip';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';

export type CatalogFilters = {
  search: string;
  tags: Tag[];
  source: Source | null;
};

const maxChoices = 8;

export function FilterBar({
  filters,
  legacyActive = false,
  onChange,
  onClearAll,
  onClearLegacy,
}: {
  filters: CatalogFilters;
  /** Filters from older links (`tags` / `source` params) are active. */
  legacyActive?: boolean;
  onChange: (filters: CatalogFilters) => void;
  onClearAll: () => void;
  onClearLegacy?: () => void;
}) {
  const [searchDraft, setSearchDraft] = React.useState(filters.search);

  React.useEffect(() => { setSearchDraft(filters.search); }, [filters.search]);

  const hasActive = filters.search.trim() !== '' || filters.tags.length > 0 ||
    filters.source !== null || legacyActive;

  const chips: React.ReactNode[] = [];
  for (const tag of filters.tags) {
    chips.push(
      <NodeChip key={`tag-${tag.id}`} kind="tag" label={tag.name} category={tag.category} origin={tag.origin}
        removeLabel={`Remove filter ${tag.name}`}
        onRemove={() => onChange({ ...filters, tags: filters.tags.filter((item) => item.id !== tag.id) })} />
    );
  }
  if (filters.source) {
    chips.push(
      <NodeChip key="source" kind="source" label={filters.source.name}
        removeLabel={`Remove filter ${filters.source.name}`}
        onRemove={() => onChange({ ...filters, source: null })} />
    );
  }
  if (legacyActive) {
    chips.push(
      <NodeChip key="older" kind="filter" label="Older filter" removeLabel="Remove older filter"
        onRemove={onClearLegacy ?? onClearAll} />
    );
  }

  return (
    <div className="grid gap-3" role="search" aria-label="Filter cards">
      <div className="flex flex-wrap items-center gap-2">
        <form
          className="relative min-w-0 flex-1 basis-56 sm:max-w-md"
          onSubmit={(event) => {
            event.preventDefault();
            onChange({ ...filters, search: searchDraft.trim() });
          }}
        >
          <Search className="text-muted-foreground pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2" aria-hidden="true" />
          <Input
            type="search"
            aria-label="Search the gallery"
            placeholder="Search"
            className="pr-9 pl-9 [&::-webkit-search-cancel-button]:hidden"
            value={searchDraft}
            onChange={(event) => setSearchDraft(event.target.value)}
          />
          {searchDraft ? (
            <Button type="button" size="icon-xs" variant="ghost"
              className="absolute top-1/2 right-1.5 -translate-y-1/2" aria-label="Clear search"
              onClick={() => {
                setSearchDraft('');
                if (filters.search) onChange({ ...filters, search: '' });
              }}>
              <X className="size-3" />
            </Button>
          ) : null}
        </form>
        <AddFilter filters={filters} onChange={(next) => onChange({ ...next, search: searchDraft.trim() })} />
      </div>

      {hasActive ? (
        <div className="flex flex-wrap items-center gap-y-2" aria-label="Active filters" role="group">
          {filters.search.trim() ? (
            <>
              <NodeChip kind="filter" label={`“${filters.search}”`} removeLabel="Remove search"
                onRemove={() => { setSearchDraft(''); onChange({ ...filters, search: '' }); }} />
              {chips.length > 0 ? <Connector /> : null}
            </>
          ) : null}
          {chips.map((chip, index) => (
            <React.Fragment key={index}>
              {chip}
              {index < chips.length - 1 ? <Connector /> : null}
            </React.Fragment>
          ))}
          <Button type="button" size="sm" variant="ghost" className="ml-3" onClick={() => { setSearchDraft(''); onClearAll(); }}>
            Clear
          </Button>
        </div>
      ) : null}
    </div>
  );
}

function Connector() {
  return <span aria-hidden="true" className="bg-border h-px w-4 shrink-0" />;
}

function AddFilter({
  filters,
  onChange,
}: {
  filters: CatalogFilters;
  onChange: (filters: CatalogFilters) => void;
}) {
  const [open, setOpen] = React.useState(false);
  const [query, setQuery] = React.useState('');
  const [tagChoices, setTagChoices] = React.useState<Tag[]>([]);
  const [sourceChoices, setSourceChoices] = React.useState<Source[]>([]);
  const [error, setError] = React.useState<string | null>(null);

  React.useEffect(() => {
    if (!open) return;
    let active = true;
    const timer = window.setTimeout(() => {
      Promise.all([listTags(query.trim()), listSources(query.trim())])
        .then(([tags, sources]) => {
          if (!active) return;
          setTagChoices(tags);
          setSourceChoices(sources);
          setError(null);
        })
        .catch((cause) => {
          if (active) setError(cause instanceof Error ? cause.message : 'Unable to search.');
        });
    }, 200);
    return () => { active = false; window.clearTimeout(timer); };
  }, [open, query]);

  const tags = tagChoices
    .filter((tag) => !filters.tags.some((selected) => selected.id === tag.id))
    .slice(0, maxChoices);
  const sources = sourceChoices.filter((source) => source.id !== filters.source?.id).slice(0, maxChoices);

  function close() {
    setOpen(false);
    setQuery('');
  }

  return (
    <Popover open={open} onOpenChange={(next) => { setOpen(next); if (!next) setQuery(''); }}>
      <PopoverTrigger render={<Button type="button" variant="outline" size="sm" />}>
        <Plus className="size-3.5" aria-hidden="true" />
        Filter
      </PopoverTrigger>
      <PopoverContent align="start" className="w-72 max-w-[calc(100vw-2rem)]">
        <Input
          type="search"
          autoFocus
          aria-label="Find a tag or source"
          placeholder="Find a tag or source"
          value={query}
          onChange={(event) => setQuery(event.target.value)}
        />
        {error ? <p className="text-destructive px-2 py-2 text-sm" role="alert">{error}</p> : null}
        <div className="mt-2 grid max-h-72 gap-0.5 overflow-auto">
          {tags.length > 0 ? <p className="text-muted-foreground px-2 pt-1 text-xs font-medium">Tags</p> : null}
          {tags.map((tag) => (
            <button key={`t${tag.id}`} type="button"
              className="hover:bg-muted focus-visible:bg-muted flex min-h-9 items-center rounded-lg px-2 text-left pointer-coarse:min-h-11"
              onClick={() => { onChange({ ...filters, tags: [...filters.tags, tag] }); close(); }}>
              <NodeChip kind="tag" label={tag.name} category={tag.category} origin={tag.origin} size="sm" />
            </button>
          ))}
          {sources.length > 0 ? <p className="text-muted-foreground px-2 pt-2 text-xs font-medium">Sources</p> : null}
          {sources.map((source) => (
            <button key={`s${source.id}`} type="button"
              className="hover:bg-muted focus-visible:bg-muted flex min-h-9 items-center gap-2 rounded-lg px-2 text-left text-sm pointer-coarse:min-h-11"
              onClick={() => { onChange({ ...filters, source }); close(); }}>
              <Globe className="text-muted-foreground size-3.5" aria-hidden="true" />
              {source.name}
            </button>
          ))}
          {!error && tags.length === 0 && sources.length === 0 ? (
            <p className="text-muted-foreground px-2 py-3 text-sm">No matches</p>
          ) : null}
        </div>
      </PopoverContent>
    </Popover>
  );
}
