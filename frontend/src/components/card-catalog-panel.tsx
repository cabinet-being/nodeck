import * as React from 'react';
import { Check, ChevronDown, Plus, Save, X } from 'lucide-react';

import {
  createSource, createTag, decideTagSuggestion, deleteCardSource, deleteCardTag,
  listCardSources, listCardTags, listSources, listTags, listTagSuggestions,
  setCardSource, setCardTag, type CardSource, type CardTag, type Source, type Tag,
  type TagSuggestion,
} from '@/api/catalog';
import { TagChip } from '@/components/tag-chip';
import { Button } from '@/components/ui/button';
import { Dialog } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';

export function CardCatalogPanel({ cardId, metadata }: {
  cardId: number;
  metadata: Record<string, unknown>;
}) {
  const [tags, setTags] = React.useState<CardTag[]>([]);
  const [suggestions, setSuggestions] = React.useState<TagSuggestion[]>([]);
  const [sources, setSources] = React.useState<CardSource[]>([]);
  const [revision, setRevision] = React.useState(0);
  const [mode, setMode] = React.useState<'tag' | 'source' | null>(null);
  const [search, setSearch] = React.useState('');
  const [choices, setChoices] = React.useState<(Tag | Source)[]>([]);
  const [sourceUrl, setSourceUrl] = React.useState('');
  const [sourceDrafts, setSourceDrafts] = React.useState<Record<number, string>>({});
  const [error, setError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [suggestionsOpen, setSuggestionsOpen] = React.useState(false);
  const [showAllSuggestions, setShowAllSuggestions] = React.useState(false);
  const suggestionsId = React.useId();

  React.useEffect(() => {
    setSuggestionsOpen(false);
    setShowAllSuggestions(false);
  }, [cardId]);

  React.useEffect(() => {
    let active = true;
    Promise.all([listCardTags(cardId), listCardSources(cardId), listTagSuggestions(cardId)])
      .then(([nextTags, nextSources, nextSuggestions]) => {
        if (!active) return;
        setTags(nextTags);
        setSources(nextSources);
        setSuggestions(nextSuggestions);
        setSourceDrafts(Object.fromEntries(nextSources.map((source) => [source.id, source.originalUrl ?? ''])));
      })
      .catch((cause) => { if (active) setError(message(cause)); });
    return () => { active = false; };
  }, [cardId, revision]);

  React.useEffect(() => {
    if (!mode) return;
    let active = true;
    const load = mode === 'tag' ? listTags(search) : listSources(search);
    load.then((results) => { if (active) setChoices(results); })
      .catch((cause) => { if (active) setError(message(cause)); });
    return () => { active = false; };
  }, [mode, search]);

  async function run(action: () => Promise<unknown>, close = false) {
    setBusy(true);
    setError(null);
    try {
      await action();
      setRevision((value) => value + 1);
      if (close) { setMode(null); setSearch(''); setSourceUrl(''); }
    } catch (cause) {
      setError(message(cause));
    } finally {
      setBusy(false);
    }
  }

  const technical = [
    metadata.media_type,
    metadata.has_audio === true ? 'audio' : null,
    typeof metadata.width === 'number' && typeof metadata.height === 'number'
      ? `${metadata.width} × ${metadata.height}` : null,
  ].filter((value): value is string => typeof value === 'string');

  return (
    <section className="grid gap-4 rounded-lg border p-4">
      {technical.length > 0 ? <div className="flex flex-wrap gap-2" aria-label="File properties">
        {technical.map((value) => <span key={value}
          className="rounded-full border border-orange-500/40 bg-orange-500/10 px-2.5 py-1 text-xs font-medium text-orange-700 dark:text-orange-300">
          {value}
        </span>)}
      </div> : null}

      <div className="grid gap-2">
        <div className="flex items-center justify-between gap-2">
          <h2 className="text-sm font-semibold">Tags</h2>
          <Button type="button" size="icon-sm" variant="ghost" aria-label="Add tag"
            onClick={() => { setMode('tag'); setSearch(''); setChoices([]); setError(null); }}><Plus className="size-4" /></Button>
        </div>
        <div className="flex flex-wrap gap-2">
          {tags.map((tag) => <span key={tag.id} className="inline-flex items-center gap-1">
            <TagChip name={tag.name} category={tag.category} origin={tag.origin} />
            <Button type="button" size="icon-xs" variant="ghost" aria-label={`Remove ${tag.name}`}
              disabled={busy} onClick={() => run(() => deleteCardTag(cardId, tag.id))}><X className="size-3" /></Button>
          </span>)}
          {tags.length === 0 ? <span className="text-muted-foreground text-xs">No tags</span> : null}
        </div>
      </div>

      {suggestions.length > 0 ? <div className="grid gap-2" aria-label="Suggested tags">
        <h3>
          <Button type="button" variant="ghost" size="sm" className="w-full justify-between px-2"
            aria-expanded={suggestionsOpen} aria-controls={suggestionsId}
            onClick={() => setSuggestionsOpen((open) => !open)}>
            <span>Suggested tags <span className="text-muted-foreground">({suggestions.length} to review)</span></span>
            <ChevronDown className={`size-4 transition-transform ${suggestionsOpen ? 'rotate-180' : ''}`} />
          </Button>
        </h3>
        <div id={suggestionsId} className={suggestionsOpen ? 'grid gap-2' : ''}>
          {suggestionsOpen ? <>
            <div className="flex max-h-64 flex-wrap gap-2 overflow-y-auto">
            {(showAllSuggestions ? suggestions : suggestions.slice(0, 8)).map((suggestion) => <span key={suggestion.tagId}
            className="inline-flex items-center gap-1 rounded-full border px-1.5 py-1"
            title={suggestion.modelKey ? `Suggested by ${suggestion.modelKey}` : undefined}>
            <TagChip name={suggestion.name} category={suggestion.category} origin={suggestion.origin} />
            <span className="text-muted-foreground text-xs">{Math.round(suggestion.confidence * 100)}%</span>
            <Button type="button" size="icon-xs" variant="ghost"
              aria-label={`Accept ${suggestion.name}`} disabled={busy}
              onClick={() => run(() => decideTagSuggestion(cardId, suggestion.tagId, 'accepted'))}>
              <Check className="size-3" />
            </Button>
            <Button type="button" size="icon-xs" variant="ghost"
              aria-label={`Reject ${suggestion.name}`} disabled={busy}
              onClick={() => run(() => decideTagSuggestion(cardId, suggestion.tagId, 'rejected'))}>
              <X className="size-3" />
            </Button>
            </span>)}
            </div>
            {!showAllSuggestions && suggestions.length > 8 ? <Button type="button" variant="ghost" size="sm"
              className="justify-self-start" onClick={() => setShowAllSuggestions(true)}>
              Show all {suggestions.length} suggestions
            </Button> : null}
          </> : null}
        </div>
      </div> : null}

      <div className="grid gap-2">
        <div className="flex items-center justify-between gap-2">
          <h2 className="text-sm font-semibold">Sources</h2>
          <Button type="button" size="icon-sm" variant="ghost" aria-label="Add source"
            onClick={() => { setMode('source'); setSearch(''); setChoices([]); setSourceUrl(''); setError(null); }}><Plus className="size-4" /></Button>
        </div>
        {sources.map((source) => <div key={source.id} className="grid gap-2 rounded-lg border p-2">
          <div className="flex items-center justify-between gap-2 text-sm">
            <span className="font-medium">{source.name}</span>
            <Button type="button" size="icon-xs" variant="ghost" aria-label={`Remove ${source.name}`}
              disabled={busy} onClick={() => run(() => deleteCardSource(cardId, source.id))}><X className="size-3" /></Button>
          </div>
          <div className="flex gap-2">
            <Input aria-label={`Original URL for ${source.name}`} value={sourceDrafts[source.id] ?? ''}
              onChange={(event) => setSourceDrafts((current) => ({ ...current, [source.id]: event.target.value }))}
              placeholder="Original URL" />
            <Button type="button" size="icon" variant="outline" aria-label={`Save URL for ${source.name}`}
              disabled={busy} onClick={() => run(() => setCardSource(cardId, source.id, sourceDrafts[source.id] ?? ''))}>
              <Save className="size-4" />
            </Button>
          </div>
        </div>)}
        {sources.length === 0 ? <span className="text-muted-foreground text-xs">No sources</span> : null}
      </div>
      {error && !mode ? <p className="text-destructive text-xs">{error}</p> : null}

      <Dialog open={mode !== null} title={mode === 'tag' ? 'Add tag' : 'Add source'}
        onOpenChange={(open) => { if (!open) { setMode(null); setError(null); } }}>
        <div className="grid gap-3">
          <Input autoFocus type="search" aria-label="Search catalog" placeholder="Search by name"
            value={search} onChange={(event) => setSearch(event.target.value)} />
          {mode === 'source' ? <Input aria-label="Original URL" placeholder="Original URL"
            value={sourceUrl} onChange={(event) => setSourceUrl(event.target.value)} /> : null}
          {error ? <p className="text-destructive text-xs">{error}</p> : null}
          <div className="grid max-h-64 gap-1 overflow-auto">
            {choices.filter((choice) => mode === 'tag' ? !tags.some((tag) => tag.id === choice.id)
              : !sources.some((source) => source.id === choice.id))
              .map((choice) => <Button key={choice.id} type="button" variant="ghost"
                className="justify-start" disabled={busy}
                onClick={() => run(() => mode === 'tag' ? setCardTag(cardId, choice.id)
                  : setCardSource(cardId, choice.id, sourceUrl), true)}>
                {choice.name}
              </Button>)}
          </div>
          {search.trim() && !choices.some((choice) => choice.name.toLocaleLowerCase() === search.trim().toLocaleLowerCase())
            ? <Button type="button" variant="outline" disabled={busy}
              onClick={() => run(async () => {
                if (mode === 'tag') {
                  const created = await createTag(search.trim());
                  await setCardTag(cardId, created.id);
                } else {
                  const created = await createSource(search.trim());
                  await setCardSource(cardId, created.id, sourceUrl);
                }
              }, true)}>Create “{search.trim()}”</Button> : null}
        </div>
      </Dialog>
    </section>
  );
}

function message(cause: unknown) {
  return cause instanceof Error ? cause.message : 'Unable to save changes.';
}
