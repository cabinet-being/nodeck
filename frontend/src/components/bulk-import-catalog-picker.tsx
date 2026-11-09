import * as React from 'react';
import { ChevronDown, Tags, X } from 'lucide-react';

import { listSources, listTags, type Source, type Tag } from '@/api/catalog';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';

export type BulkImportCatalogSelection = {
  tags: Tag[];
  source: Source | null;
  originalUrl: string;
};

export function BulkImportCatalogPicker({ selection, onChange, locked }: {
  selection: BulkImportCatalogSelection;
  onChange: (selection: BulkImportCatalogSelection) => void;
  locked: boolean;
}) {
  const [tagSearch, setTagSearch] = React.useState('');
  const [sourceSearch, setSourceSearch] = React.useState('');
  const [tagChoices, setTagChoices] = React.useState<Tag[]>([]);
  const [sourceChoices, setSourceChoices] = React.useState<Source[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [open, setOpen] = React.useState(false);

  React.useEffect(() => {
    if (!open || locked || !tagSearch.trim()) { setTagChoices([]); return; }
    let active = true;
    listTags(tagSearch.trim()).then((choices) => { if (active) { setTagChoices(choices); setError(null); } })
      .catch((cause) => { if (active) setError(message(cause)); });
    return () => { active = false; };
  }, [open, locked, tagSearch]);

  React.useEffect(() => {
    if (!open || locked || !sourceSearch.trim()) { setSourceChoices([]); return; }
    let active = true;
    listSources(sourceSearch.trim()).then((choices) => { if (active) { setSourceChoices(choices); setError(null); } })
      .catch((cause) => { if (active) setError(message(cause)); });
    return () => { active = false; };
  }, [open, locked, sourceSearch]);

  return <section className="rounded-xl border p-4">
    <Button type="button" variant="ghost" size="sm" aria-expanded={open} aria-controls="import-catalog-options"
      onClick={() => setOpen((current) => !current)}>
      <Tags className="size-4" /> Tags & source <ChevronDown className={`size-4 transition-transform ${open ? 'rotate-180' : ''}`} />
    </Button>
    <p className="text-muted-foreground mt-1 text-xs">
      {selection.tags.length} tags{selection.source ? ` · ${selection.source.name}` : ''}{locked && ' · locked for this import'}
    </p>
    {open && <div id="import-catalog-options" className="mt-3 grid gap-3 sm:grid-cols-2">
      <p className="text-muted-foreground text-xs sm:col-span-2">Applied to every imported card and group.</p>
      <div className="grid content-start gap-2">
        <Input type="search" aria-label="Find import tag" placeholder="Search existing tags by name"
          value={tagSearch} disabled={locked} onChange={(event) => { setTagSearch(event.target.value); setTagChoices([]); }} />
        <div className="flex flex-wrap gap-1">
          {selection.tags.map((tag) => <Button key={tag.id} type="button" variant="outline" size="sm"
            disabled={locked} aria-label={`Remove import tag ${tag.name}`}
            onClick={() => onChange({ ...selection, tags: selection.tags.filter((candidate) => candidate.id !== tag.id) })}>
            {tag.name} <X className="size-3" />
          </Button>)}
        </div>
        {tagSearch.trim() && <div className="grid max-h-36 gap-1 overflow-auto">
          {tagChoices.filter((tag) => !selection.tags.some((selected) => selected.id === tag.id))
            .map((tag) => <Button key={tag.id} type="button" variant="ghost" size="sm"
              className="justify-start" disabled={locked}
              onClick={() => { onChange({ ...selection, tags: [...selection.tags, tag] }); setTagSearch(''); }}>
              {tag.name}{tag.nameConflict ? ' · legacy duplicate' : ''}
            </Button>)}
        </div>}
      </div>
      <div className="grid content-start gap-2">
        <Input type="search" aria-label="Find import source" placeholder="Search existing sources by name"
          value={sourceSearch} disabled={locked} onChange={(event) => { setSourceSearch(event.target.value); setSourceChoices([]); }} />
        {selection.source && <Button type="button" variant="outline" size="sm" disabled={locked}
          className="justify-start" aria-label={`Remove import source ${selection.source.name}`}
          onClick={() => onChange({ ...selection, source: null, originalUrl: '' })}>
          {selection.source.name} <X className="size-3" />
        </Button>}
        {sourceSearch.trim() && <div className="grid max-h-36 gap-1 overflow-auto">
          {sourceChoices.map((source) => <Button key={source.id} type="button" variant="ghost" size="sm"
            className="justify-start" disabled={locked}
            onClick={() => { onChange({ ...selection, source }); setSourceSearch(''); }}>
            {source.name}{source.nameConflict ? ' · legacy duplicate' : ''}
          </Button>)}
        </div>}
        {selection.source && <Input aria-label="Original URL for import source" placeholder="Original URL (optional)"
          value={selection.originalUrl} disabled={locked}
          onChange={(event) => onChange({ ...selection, originalUrl: event.target.value })} />}
      </div>
      {error && <p role="alert" className="text-destructive text-sm sm:col-span-2">Catalog search failed: {error}</p>}
    </div>}
  </section>;
}

function message(cause: unknown) {
  return cause instanceof Error ? cause.message : 'Request failed.';
}
