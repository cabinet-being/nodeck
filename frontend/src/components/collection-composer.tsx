import * as React from 'react';
import { BookOpen, Check, Layers, Plus } from 'lucide-react';

import {
  createCollectionFromCards,
  getCard,
  listCards,
  resolveApiUrl,
  updateCollectionChildren,
  type Card,
} from '@/api/cards';
import { cardLabel } from '@/components/card-tile';
import { SortableCardList, type SortableCard } from '@/components/sortable-card-list';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Segmented } from '@/components/ui/segmented';
import { Sheet } from '@/components/ui/sheet';
import { randomUuid } from '@/lib/uuid';
import { cn } from '@/lib/utils';

export type ComposerMode = 'comic' | 'set' | 'add';

/**
 * Turns chosen media cards into a new comic or set, or adds them to the end of an existing one.
 * The list order is the page order; it starts as the order the cards were chosen in.
 */
export function CollectionComposer({
  open,
  onOpenChange,
  mode: initialMode,
  cards,
  onCardsChange,
  onDone,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  mode: ComposerMode;
  cards: SortableCard[];
  onCardsChange: (cards: SortableCard[]) => void;
  /** Called with the address of the comic or set that was created or extended. */
  onDone: (path: string) => void;
}) {
  const [mode, setMode] = React.useState<ComposerMode>(initialMode);
  const [title, setTitle] = React.useState('');
  const [target, setTarget] = React.useState<Card | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [submitting, setSubmitting] = React.useState(false);
  // One key per opened composer, so a retried create cannot make a second collection.
  const creationKey = React.useRef(randomUuid());

  React.useEffect(() => {
    if (!open) return;
    setMode(initialMode);
    setError(null);
    setTarget(null);
    creationKey.current = randomUuid();
  }, [open, initialMode]);

  const creating = mode !== 'add';
  const tooFew = creating && cards.length < 2;
  const canSubmit = !submitting && cards.length > 0 && !tooFew && (creating || target !== null);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    if (!canSubmit) return;
    setSubmitting(true);
    setError(null);
    try {
      if (mode === 'add') {
        const current = await getCard(target!.id);
        const existing = (current.containedCards ?? []).map((child) => child.id);
        const adding = cards.map((card) => card.id).filter((id) => !existing.includes(id));
        await updateCollectionChildren(target!.id, [...existing, ...adding]);
        onDone(`/cards/${target!.id}`);
      } else {
        const created = await createCollectionFromCards({
          type: mode,
          title: title.trim() || undefined,
          childCardIds: cards.map((card) => card.id),
          idempotencyKey: creationKey.current,
        });
        setTitle('');
        onDone(`/cards/${created.id}`);
      }
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Unable to save.');
    } finally {
      setSubmitting(false);
    }
  }

  const submitLabel = mode === 'add'
    ? `Add ${cards.length === 1 ? 'page' : `${cards.length} pages`}`
    : mode === 'comic' ? 'Create comic' : 'Create set';

  return (
    <Sheet open={open} onOpenChange={onOpenChange} side="right" className="content-start"
      title={mode === 'add' ? 'Add to a comic or set' : mode === 'comic' ? 'New comic' : 'New set'}>
      <form onSubmit={submit} className="grid content-start gap-4">
        <Segmented
          ariaLabel="What to make"
          value={mode}
          onValueChange={(next) => { setMode(next); setError(null); }}
          options={[
            { value: 'comic', label: 'Comic', icon: <BookOpen /> },
            { value: 'set', label: 'Set', icon: <Layers /> },
            { value: 'add', label: 'Add to…', icon: <Plus /> },
          ]}
          className="justify-self-start"
        />
        {creating ? (
          <label className="grid gap-2 text-sm font-medium">Title
            <Input value={title} onChange={(event) => setTitle(event.target.value)} placeholder="Optional"
              disabled={submitting} />
          </label>
        ) : (
          <CollectionPicker selected={target} onSelect={setTarget} disabled={submitting} />
        )}
        <div className="grid gap-2">
          <div className="flex items-baseline justify-between gap-2 text-sm">
            <span className="font-medium">
              {mode === 'add' ? 'Pages to add' : 'Pages'} ({cards.length})
            </span>
            <span className="text-muted-foreground text-xs">
              {mode === 'add' ? 'Added after the existing pages' : 'Drag to change the order'}
            </span>
          </div>
          <SortableCardList items={cards} onChange={onCardsChange} disabled={submitting} />
          {cards.length === 0 ? <p className="text-muted-foreground text-sm">No pages chosen.</p> : null}
          {tooFew && cards.length > 0 ? (
            <p className="text-muted-foreground text-sm">A new comic or set needs at least two pages.</p>
          ) : null}
        </div>
        {error ? <p className="text-destructive text-sm" role="alert">{error}</p> : null}
        <div className="flex flex-wrap justify-end gap-2">
          <Button type="button" variant="outline" disabled={submitting} onClick={() => onOpenChange(false)}>Cancel</Button>
          <Button type="submit" disabled={!canSubmit}>
            {submitting ? 'Saving…' : submitLabel}
          </Button>
        </div>
      </form>
    </Sheet>
  );
}

function CollectionPicker({
  selected,
  onSelect,
  disabled,
}: {
  selected: Card | null;
  onSelect: (card: Card) => void;
  disabled: boolean;
}) {
  const [search, setSearch] = React.useState('');
  const [results, setResults] = React.useState<Card[] | null>(null);
  const [error, setError] = React.useState<string | null>(null);

  React.useEffect(() => {
    let active = true;
    const timer = window.setTimeout(() => {
      listCards({ types: ['comic', 'set'], search: search.trim() || undefined, limit: 30, sort: 'created_at', order: 'desc' })
        .then((cards) => { if (active) { setResults(cards); setError(null); } })
        .catch((cause) => { if (active) setError(cause instanceof Error ? cause.message : 'Unable to search.'); });
    }, 200);
    return () => { active = false; window.clearTimeout(timer); };
  }, [search]);

  return (
    <div className="grid gap-2">
      <label htmlFor="composer-target-search" className="text-sm font-medium">Comic or set</label>
      <Input id="composer-target-search" type="search" value={search} placeholder="Search comics and sets"
        onChange={(event) => setSearch(event.target.value)} disabled={disabled} />
      {error ? <p className="text-destructive text-sm" role="alert">{error}</p> : null}
      <div role="listbox" aria-label="Comics and sets" className="grid max-h-60 gap-1 overflow-auto rounded-xl border p-1">
        {results === null ? <p className="text-muted-foreground p-2 text-sm">Loading…</p> : null}
        {results?.length === 0 ? <p className="text-muted-foreground p-2 text-sm">No comics or sets found.</p> : null}
        {results?.map((card) => {
          const active = selected?.id === card.id;
          const count = Number(card.metadata?.contained_count);
          return (
            <button
              key={card.id}
              type="button"
              role="option"
              aria-selected={active}
              disabled={disabled}
              onClick={() => onSelect(card)}
              className={cn(
                'flex min-h-11 items-center gap-3 rounded-lg p-1.5 text-left text-sm',
                active ? 'bg-accent text-accent-foreground' : 'hover:bg-muted'
              )}
            >
              {card.previewUrl
                ? <img src={resolveApiUrl(card.previewUrl)} alt="" className="size-10 shrink-0 rounded-md object-cover" />
                : <div className="bg-muted size-10 shrink-0 rounded-md" />}
              <span className="min-w-0 flex-1">
                <span className="block truncate">{cardLabel(card)}</span>
                <span className="text-muted-foreground block text-xs capitalize">
                  {card.type}{Number.isFinite(count) && count > 0 ? ` · ${count} pages` : ''}
                </span>
              </span>
              {active ? <Check className="size-4 shrink-0" /> : null}
            </button>
          );
        })}
      </div>
    </div>
  );
}
