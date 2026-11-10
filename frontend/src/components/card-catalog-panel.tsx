import * as React from 'react';
import { Check, ExternalLink, FileText, Globe, MoreHorizontal, Pencil, Save, Tag as TagIcon, Wand2, X } from 'lucide-react';

import {
  correctTagSuggestion, createSource, createTag, decideTagSuggestion, decideTagSuggestions, deleteCardSource, deleteCardTag,
  listCardSources, listCardTags, listSources, listTags, listTagSuggestions,
  setCardSource, setCardTag, type CardSource, type CardTag, type Source, type Tag,
  type TagSuggestion,
} from '@/api/catalog';
import { ConnectionStrip, type ConnectionGroup } from '@/components/connection-strip';
import { NodeChip } from '@/components/node-chip';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Dialog } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Menu, MenuContent, MenuItem, MenuTrigger } from '@/components/ui/menu';
import { Tooltip } from '@/components/ui/tooltip';
import { cn } from '@/lib/utils';

export function CardCatalogPanel({ cardId, metadata, extraGroups = [], onNavigate }: {
  cardId: number;
  metadata: Record<string, unknown>;
  /** Groups appended after Sources (for example Decks and Related cards). */
  extraGroups?: ConnectionGroup[];
  onNavigate?: (event: React.MouseEvent<HTMLAnchorElement>) => void;
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
  const [selectMode, setSelectMode] = React.useState(false);
  const [showAllTags, setShowAllTags] = React.useState(false);
  const [editingSourceId, setEditingSourceId] = React.useState<number | null>(null);
  const [showAllSuggestions, setShowAllSuggestions] = React.useState(false);
  const [selectedSuggestionIds, setSelectedSuggestionIds] = React.useState<number[]>([]);
  const [correcting, setCorrecting] = React.useState<TagSuggestion | null>(null);
  const [openSuggestionId, setOpenSuggestionId] = React.useState<number | null>(null);

  React.useEffect(() => {
    setSelectMode(false);
    setShowAllTags(false);
    setEditingSourceId(null);
    setShowAllSuggestions(false);
    setSelectedSuggestionIds([]);
    setOpenSuggestionId(null);
  }, [cardId]);

  React.useEffect(() => {
    let active = true;
    Promise.all([listCardTags(cardId), listCardSources(cardId), listTagSuggestions(cardId)])
      .then(([nextTags, nextSources, nextSuggestions]) => {
        if (!active) return;
        setTags(nextTags);
        setSources(nextSources);
        setSuggestions(nextSuggestions);
        setSelectedSuggestionIds((current) => current.filter((id) => nextSuggestions.some((item) => item.tagId === id)));
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

  async function run(action: () => Promise<unknown>, close = false): Promise<boolean> {
    setBusy(true);
    setError(null);
    try {
      await action();
      setRevision((value) => value + 1);
      if (close) { setMode(null); setCorrecting(null); setSearch(''); setSourceUrl(''); }
      return true;
    } catch (cause) {
      setError(message(cause));
      return false;
    } finally {
      setBusy(false);
    }
  }

  async function reviewMany(decision: 'accepted' | 'rejected', tagIds?: number[]) {
    if (await run(() => decideTagSuggestions(cardId, decision, tagIds)))
      setSelectedSuggestionIds([]);
  }

  const suggestionById = new Map(suggestions.map((suggestion) => [suggestion.tagId, suggestion]));
  const assignedIds = new Set(tags.map((tag) => tag.id));
  const systemTags = tags.filter((tag) => tag.assignmentSource === 'system' || tag.origin === 'system');
  const userTags = tags.filter((tag) => !(tag.assignmentSource === 'system' || tag.origin === 'system'));
  const settledTags = userTags.filter((tag) => !suggestionById.has(tag.id));
  const pendingTags = userTags.filter((tag) => suggestionById.has(tag.id));
  const orderedTags = [...settledTags, ...pendingTags];
  const visibleTags = showAllTags ? orderedTags : orderedTags.slice(0, 8);
  const unassignedSuggestions = suggestions.filter((suggestion) => !assignedIds.has(suggestion.tagId));
  const visibleSuggestions = showAllSuggestions ? unassignedSuggestions : unassignedSuggestions.slice(0, 8);
  const file = fileSummary(metadata);

  function startPicker(next: 'tag' | 'source') {
    setMode(next); setSearch(''); setChoices([]); setSourceUrl(''); setError(null);
  }

  function chipHref(tagId: number) {
    return onNavigate ? `/gallery?catalog_tags=${tagId}` : undefined;
  }

  const fileNode = file ? (
    <NodeChip kind="file" size="sm" label={file} className={cn('border-transparent bg-transparent px-0 font-normal text-muted-foreground')} />
  ) : null;
  const systemNodes = systemTags.map((tag) => (
    <NodeChip key={`s${tag.id}`} kind="tag" size="sm" label={tag.name} category="meta" origin="system"
      href={chipHref(tag.id)} onNavigate={onNavigate} />
  ));

  const tagNodes = visibleTags.map((tag) => {
    const pending = suggestionById.get(tag.id);
    if (pending) return renderSuggestion(pending);
    return <NodeChip key={`t${tag.id}`} kind="tag" label={tag.name} category={tag.category} origin={tag.origin}
      href={chipHref(tag.id)} onNavigate={onNavigate} onRemove={() => { if (!busy) void run(() => deleteCardTag(cardId, tag.id)); }} />;
  });

  function renderSuggestion(suggestion: TagSuggestion) {
    const selected = selectedSuggestionIds.includes(suggestion.tagId);
    const open = openSuggestionId === suggestion.tagId;
    return (
      <span key={suggestion.tagId}
        onKeyDown={(event) => { if (event.key === 'Escape' && open) setOpenSuggestionId(null); }}
        className="group/s relative inline-flex max-w-full min-w-0 items-center gap-0.5">
        {selectMode ? (
          <label className="inline-flex size-6 shrink-0 items-center justify-center pointer-coarse:size-11">
            <Checkbox aria-label={`Select ${suggestion.name}`} checked={selected} disabled={busy}
              onCheckedChange={(checked) => setSelectedSuggestionIds((current) => checked
                ? [...current, suggestion.tagId] : current.filter((id) => id !== suggestion.tagId))} />
          </label>
        ) : null}
        <Tooltip label={confidenceLabel(suggestion.confidence)} side="top">
          <button type="button" aria-expanded={open} aria-label={`Review suggested tag ${suggestion.name}`}
            className="inline-flex min-w-0 max-w-full rounded-full pointer-coarse:min-h-10"
            onClick={() => setOpenSuggestionId(open ? null : suggestion.tagId)}>
            <NodeChip kind="tag" suggested label={suggestion.name} category={suggestion.category} origin={suggestion.origin} />
          </button>
        </Tooltip>
        <span data-open={open}
          className="bg-surface shadow-2 pointer-events-none invisible absolute top-full left-0 z-20 inline-flex items-center rounded-full border p-0.5 opacity-0 transition-opacity group-focus-within/s:pointer-events-auto group-focus-within/s:visible group-focus-within/s:opacity-100 group-hover/s:pointer-events-auto group-hover/s:visible group-hover/s:opacity-100 pointer-coarse:group-focus-within/s:invisible pointer-coarse:group-focus-within/s:opacity-0 pointer-coarse:group-hover/s:invisible pointer-coarse:group-hover/s:opacity-0 pointer-coarse:data-[open=true]:pointer-events-auto pointer-coarse:data-[open=true]:visible pointer-coarse:data-[open=true]:opacity-100">
          <Button type="button" size="icon-xs" variant="ghost" className="pointer-coarse:size-10"
            aria-label={`Accept ${suggestion.name}`} title="Accept" disabled={busy}
            onClick={() => void run(() => decideTagSuggestion(cardId, suggestion.tagId, 'accepted'))}>
            <Check />
          </Button>
          <Button type="button" size="icon-xs" variant="ghost" className="pointer-coarse:size-10"
            aria-label={`Reject ${suggestion.name}`} title="Reject" disabled={busy}
            onClick={() => void run(() => decideTagSuggestion(cardId, suggestion.tagId, 'rejected'))}>
            <X />
          </Button>
          <Button type="button" size="icon-xs" variant="ghost" className="pointer-coarse:size-10"
            aria-label={`Correct ${suggestion.name}`} title="Replace with another tag" disabled={busy}
            onClick={() => { setCorrecting(suggestion); setMode('tag'); setSearch(''); setChoices([]); setError(null); }}>
            <Pencil />
          </Button>
        </span>
      </span>
    );
  }

  const suggestionNodes = visibleSuggestions.map(renderSuggestion);

  const suggestionActions = suggestions.length > 0 ? (
    <>
      <span className="bg-primary/10 text-primary rounded-full px-2 py-0.5 text-xs font-medium tabular-nums">{suggestions.length} to review</span>
      {selectMode ? (
        <>
          <Button type="button" size="sm" variant="secondary" className="pointer-coarse:h-11"
            disabled={busy || selectedSuggestionIds.length === 0}
            onClick={() => void reviewMany('accepted', selectedSuggestionIds)}>Accept ({selectedSuggestionIds.length})</Button>
          <Button type="button" size="sm" variant="secondary" className="pointer-coarse:h-11"
            disabled={busy || selectedSuggestionIds.length === 0}
            onClick={() => void reviewMany('rejected', selectedSuggestionIds)}>Reject ({selectedSuggestionIds.length})</Button>
          <Button type="button" size="sm" variant="ghost" className="pointer-coarse:h-11"
            onClick={() => { setSelectMode(false); setSelectedSuggestionIds([]); }}>Done</Button>
        </>
      ) : (
        <>
          <Button type="button" size="sm" variant="ghost" className="pointer-coarse:h-11"
            onClick={() => setSelectMode(true)}>Select</Button>
          <Menu>
            <MenuTrigger render={<Button type="button" size="icon-sm" variant="ghost" className="pointer-coarse:size-11"
              aria-label="Review all suggestions" disabled={busy} />}>
              <MoreHorizontal className="size-4" />
            </MenuTrigger>
            <MenuContent>
              <MenuItem onClick={() => void reviewMany('accepted')}><Check /> Accept all</MenuItem>
              <MenuItem onClick={() => void reviewMany('rejected')}><X /> Reject all</MenuItem>
            </MenuContent>
          </Menu>
        </>
      )}
    </>
  ) : null;

  const sourceNodes = sources.map((source) => (
    <React.Fragment key={source.id}>
      <span className="inline-flex max-w-full min-w-0 items-center gap-0.5">
        <NodeChip kind="source" label={source.name}
          onRemove={() => { if (!busy) void run(() => deleteCardSource(cardId, source.id)); }} />
        {source.originalUrl ? (
          <a href={source.originalUrl} target="_blank" rel="noreferrer noopener" aria-label={`Open original for ${source.name}`}
            title="Open original"
            className="text-muted-foreground hover:bg-muted hover:text-foreground inline-flex size-6 items-center justify-center rounded-md pointer-coarse:size-11">
            <ExternalLink className="size-3.5" />
          </a>
        ) : null}
        <Button type="button" size="icon-xs" variant="ghost" className="pointer-coarse:size-11"
          aria-label={`Edit URL for ${source.name}`} title="Edit original URL" aria-expanded={editingSourceId === source.id}
          onClick={() => setEditingSourceId((current) => current === source.id ? null : source.id)}>
          <Pencil />
        </Button>
      </span>
      {editingSourceId === source.id ? (
        <div className="flex w-full gap-2">
          <Input aria-label={`Original URL for ${source.name}`} value={sourceDrafts[source.id] ?? ''}
            onChange={(event) => setSourceDrafts((current) => ({ ...current, [source.id]: event.target.value }))}
            placeholder="Original URL" />
          <Button type="button" size="icon" variant="outline" aria-label={`Save URL for ${source.name}`}
            disabled={busy} onClick={() => void run(() => setCardSource(cardId, source.id, sourceDrafts[source.id] ?? '')).then((ok) => { if (ok) setEditingSourceId(null); })}>
            <Save className="size-4" />
          </Button>
        </div>
      ) : null}
    </React.Fragment>
  ));

  const groups: ConnectionGroup[] = [
    {
      key: 'tags', label: 'Tags', icon: TagIcon, nodes: tagNodes, count: userTags.length, empty: 'No tags',
      onAdd: () => startPicker('tag'), addLabel: 'Add tag', actions: suggestionActions,
      footer: !showAllTags && orderedTags.length > 8 ? (
        <Button type="button" variant="ghost" size="xs" className="justify-self-start pointer-coarse:h-11"
          onClick={() => setShowAllTags(true)}>+{orderedTags.length - 8} more</Button>
      ) : null,
    },
    {
      key: 'suggested', label: 'Suggested', icon: Wand2, nodes: suggestionNodes, count: unassignedSuggestions.length,
      hideWhenEmpty: true,
      footer: !showAllSuggestions && unassignedSuggestions.length > 8 ? (
        <Button type="button" variant="ghost" size="xs" className="justify-self-start pointer-coarse:h-11"
          onClick={() => setShowAllSuggestions(true)}>+{unassignedSuggestions.length - 8} more</Button>
      ) : null,
    },
    {
      key: 'sources', label: 'Sources', icon: Globe, nodes: sourceNodes, empty: 'No sources',
      onAdd: () => startPicker('source'), addLabel: 'Add source',
    },
    ...(systemNodes.length > 0 ? [{
      key: 'file', label: 'File', icon: FileText, nodes: systemNodes, footer: fileNode,
    }] : []),
    ...extraGroups,
  ];

  return (
    <>
      <ConnectionStrip groups={groups} footer={systemNodes.length === 0 ? fileNode : null} />
      {error && !mode ? <p className="text-destructive text-xs">{error}</p> : null}

      <Dialog open={mode !== null} title={correcting ? `Correct ${correcting.name}` : mode === 'tag' ? 'Add tag' : 'Add source'}
        onOpenChange={(open) => { if (!open) { setMode(null); setCorrecting(null); setError(null); } }}>
        <div className="grid gap-3">
          <Input autoFocus type="search" aria-label="Search catalog" placeholder="Search by name"
            value={search} onChange={(event) => setSearch(event.target.value)} />
          {mode === 'source' ? <Input aria-label="Original URL" placeholder="Original URL"
            value={sourceUrl} onChange={(event) => setSourceUrl(event.target.value)} /> : null}
          {error ? <p className="text-destructive text-xs">{error}</p> : null}
          <div className="grid max-h-64 gap-1 overflow-auto">
            {choices.filter((choice) => !('origin' in choice && choice.origin === 'system')).filter((choice) => mode === 'tag'
              ? correcting ? choice.id !== correcting.tagId : !tags.some((tag) => tag.id === choice.id)
              : !sources.some((source) => source.id === choice.id))
              .map((choice) => <Button key={choice.id} type="button" variant="ghost"
                className="justify-start" disabled={busy}
                onClick={() => run(() => correcting ? correctTagSuggestion(cardId, correcting.tagId, choice.id)
                  : mode === 'tag' ? setCardTag(cardId, choice.id)
                  : setCardSource(cardId, choice.id, sourceUrl), true)}>
                {choice.name}
              </Button>)}
          </div>
          {correcting ? <Button type="button" variant="outline"
            onClick={() => { setMode(null); setCorrecting(null); setError(null); }}>Cancel</Button> : null}
          {!correcting && search.trim() && !choices.some((choice) => choice.name.toLocaleLowerCase() === search.trim().toLocaleLowerCase())
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
    </>
  );
}


function fileSummary(metadata: Record<string, unknown>): string | null {
  const parts: string[] = [];
  if (typeof metadata.width === 'number' && typeof metadata.height === 'number') parts.push(`${metadata.width}×${metadata.height}`);
  if (typeof metadata.media_type === 'string') parts.push(metadata.media_type);
  if (typeof metadata.file_size === 'number') parts.push(formatBytes(metadata.file_size));
  if (typeof metadata.duration === 'number' && metadata.duration > 0) parts.push(formatDuration(metadata.duration));
  const codecs = [metadata.video_codec, metadata.audio_codec].filter((value): value is string => typeof value === 'string' && value !== '');
  if (codecs.length > 0) parts.push(codecs.join('/'));
  else if (metadata.has_audio === true) parts.push('audio');
  return parts.length > 0 ? parts.join(' · ') : null;
}

function formatBytes(bytes: number) {
  if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(1)} GB`;
  if (bytes >= 1024 ** 2) return `${(bytes / 1024 ** 2).toFixed(1)} MB`;
  return `${Math.max(1, Math.round(bytes / 1024))} KB`;
}

function formatDuration(seconds: number) {
  const total = Math.round(seconds);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = String(total % 60).padStart(2, '0');
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${s}` : `${m}:${s}`;
}

function confidenceLabel(confidence: number) {
  if (confidence >= 0.8) return 'Suggested · high confidence';
  if (confidence >= 0.5) return 'Suggested · medium confidence';
  return 'Suggested · low confidence';
}

function message(cause: unknown) {
  return cause instanceof Error ? cause.message : 'Unable to save changes.';
}
