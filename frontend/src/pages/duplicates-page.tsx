import * as React from 'react';
import { Check, CopyCheck, Heart, Layers, Merge, X } from 'lucide-react';

import {
  dismissDuplicates,
  listDuplicateGroups,
  mergeDuplicates,
  type DuplicateCard,
  type DuplicateGroup,
  type DuplicateGroupPage,
  type DuplicateKind,
} from '@/api/duplicates';
import { CardPreview } from '@/components/card-preview';
import { EmptyState } from '@/components/empty-state';
import { pageCountOf, Pagination, useClampPage, usePageScroll, useResetPage } from '@/components/pagination';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Dialog } from '@/components/ui/dialog';
import { Segmented } from '@/components/ui/segmented';
import { Skeleton } from '@/components/ui/skeleton';
import { formatTime } from '@/components/video-player';
import { parsePositiveInt, useUrlState } from '@/hooks/use-url-state';
import { cn } from '@/lib/utils';

const pageSize = 20;

type BatchState = { running: boolean; merged: number; groups: number; skipped: number; error: string | null };

export function DuplicatesPage({ onNavigate }: { onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void }) {
  const [kind, setKind] = useUrlState<DuplicateKind>('kind', 'all', {
    parse: (raw) => (raw === 'exact' || raw === 'similar' ? raw : undefined),
  });
  const [page, setPage] = useUrlState('page', 1, { parse: parsePositiveInt });
  const [data, setData] = React.useState<DuplicateGroupPage | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [isLoading, setIsLoading] = React.useState(true);
  const [reloadKey, setReloadKey] = React.useState(0);
  const [confirmBatch, setConfirmBatch] = React.useState(false);
  const [batch, setBatch] = React.useState<BatchState | null>(null);
  const stopRef = React.useRef(false);
  const pageCount = pageCountOf(data?.total ?? 0, pageSize);
  useResetPage(setPage, [kind]);
  useClampPage(page, pageCount, !isLoading && !error, setPage);
  usePageScroll(page);

  React.useEffect(() => {
    const controller = new AbortController();
    setIsLoading(true);
    setError(null);
    listDuplicateGroups(kind, pageSize, (page - 1) * pageSize, controller.signal)
      .then((result) => setData(result))
      .catch((cause) => { if (!controller.signal.aborted) setError(messageOf(cause)); })
      .finally(() => { if (!controller.signal.aborted) setIsLoading(false); });
    return () => controller.abort();
  }, [kind, page, reloadKey]);

  const reload = React.useCallback(() => setReloadKey((key) => key + 1), []);

  async function mergeAllExact() {
    setConfirmBatch(false);
    stopRef.current = false;
    const state: BatchState = { running: true, merged: 0, groups: 0, skipped: 0, error: null };
    setBatch({ ...state });
    try {
      // Groups that cannot be merged stay in the list, so the next request starts after them.
      let offset = 0;
      for (;;) {
        if (stopRef.current) break;
        const result = await listDuplicateGroups('exact', 100, offset);
        if (result.groups.length === 0) break;
        for (const group of result.groups) {
          if (stopRef.current) break;
          if (group.conflict) {
            state.skipped++;
            offset++;
          } else {
            try {
              const merged = await mergeDuplicates(group.suggestedSurvivorId,
                group.cards.map((card) => card.id).filter((id) => id !== group.suggestedSurvivorId));
              state.merged += merged.mergedCardIds.length;
              state.groups++;
            } catch {
              state.skipped++;
              offset++;
            }
          }
          setBatch({ ...state });
        }
      }
    } catch (cause) {
      state.error = messageOf(cause);
    }
    state.running = false;
    setBatch({ ...state });
    reload();
  }

  const exactGroups = data?.exactGroups ?? 0;
  return (
    <section className="mx-auto flex w-full max-w-6xl flex-col gap-4 p-4 sm:p-6">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="grid gap-1">
          <h1 className="text-2xl font-semibold">Duplicates</h1>
          <p className="text-muted-foreground max-w-prose text-sm">
            Merging keeps one card and moves tags, sources, decks, favorites, comic pages, votes and views of the
            others onto it. Their titles, properties and file details are kept in the card&apos;s metadata.
          </p>
        </div>
        <Button type="button" disabled={!exactGroups || batch?.running} onClick={() => setConfirmBatch(true)}>
          <CopyCheck /> Merge all exact{exactGroups ? ` (${exactGroups})` : ''}
        </Button>
      </div>

      {data ? (
        <p className="text-muted-foreground text-sm" aria-live="polite">
          {data.exactGroups} exact {plural(data.exactGroups, 'group')} ({data.exactRedundantCards} extra {plural(data.exactRedundantCards, 'copy', 'copies')}),{' '}
          {data.similarGroups} similar {plural(data.similarGroups, 'group')}.
          {data.pendingHashes > 0 ? ` ${data.pendingHashes} ${plural(data.pendingHashes, 'card is', 'cards are')} still being fingerprinted in the background.` : ''}
        </p>
      ) : null}

      {batch ? (
        <div role="status" className="bg-surface flex flex-wrap items-center gap-3 rounded-xl border p-3 text-sm">
          <span className="flex-1">
            {batch.running ? 'Merging exact duplicates… ' : 'Finished. '}
            {batch.merged} {plural(batch.merged, 'copy', 'copies')} merged in {batch.groups} {plural(batch.groups, 'group')}
            {batch.skipped ? `, ${batch.skipped} ${plural(batch.skipped, 'group')} skipped` : ''}.
            {batch.error ? ` ${batch.error}` : ''}
          </span>
          {batch.running ? (
            <Button type="button" variant="outline" size="sm" onClick={() => { stopRef.current = true; }}>Stop</Button>
          ) : (
            <Button type="button" variant="ghost" size="sm" onClick={() => setBatch(null)}>Dismiss</Button>
          )}
        </div>
      ) : null}

      <div className="-mx-4 overflow-x-auto px-4 sm:mx-0 sm:px-0">
        <Segmented
          ariaLabel="Duplicate kind"
          value={kind}
          onValueChange={setKind}
          options={[
            { value: 'all', label: 'All' },
            { value: 'exact', label: 'Same file' },
            { value: 'similar', label: 'Looks alike' },
          ]}
        />
      </div>

      {error ? <div role="alert" className="text-destructive text-sm">{error}</div> : null}

      {isLoading && !data ? (
        <div role="status" aria-label="Loading" className="grid gap-3">
          {Array.from({ length: 3 }, (_, index) => <Skeleton key={index} className="h-64 rounded-xl" />)}
        </div>
      ) : null}

      {data && data.groups.length === 0 && !isLoading ? (
        <EmptyState icon={CopyCheck} title={kind === 'similar'
          ? 'No cards look alike. Pictures are compared once their fingerprints are ready.'
          : 'No duplicates found.'} />
      ) : null}

      <div className={cn('grid gap-4', isLoading && 'opacity-60')}>
        {data?.groups.map((group) => (
          <GroupPanel key={group.key} group={group} onNavigate={onNavigate} onDone={reload} disabled={!!batch?.running} />
        ))}
      </div>

      <Pagination page={page} pageCount={pageCount} onPageChange={setPage} total={data?.total} disabled={isLoading} />

      <Dialog open={confirmBatch} onOpenChange={setConfirmBatch} title="Merge all exact duplicates?"
        description={`${exactGroups} ${plural(exactGroups, 'group')} of identical files. In each group the suggested card is kept and the other copies are merged into it; their files are deleted. Groups with pages of different comics or sets are skipped.`}>
        <div className="flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={() => setConfirmBatch(false)}>Cancel</Button>
          <Button type="button" onClick={() => void mergeAllExact()}><Merge /> Merge {data?.exactRedundantCards ?? 0} {plural(data?.exactRedundantCards ?? 0, 'copy', 'copies')}</Button>
        </div>
      </Dialog>
    </section>
  );
}

function GroupPanel({
  group, onNavigate, onDone, disabled,
}: {
  group: DuplicateGroup;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onDone: () => void;
  disabled: boolean;
}) {
  const [survivorId, setSurvivorId] = React.useState(group.suggestedSurvivorId);
  const [excluded, setExcluded] = React.useState<Set<number>>(() => new Set());
  const [busy, setBusy] = React.useState<'merge' | 'dismiss' | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const mergeIds = group.cards.map((card) => card.id).filter((id) => id !== survivorId && !excluded.has(id));
  const titleId = `duplicate-group-${group.key}`;

  async function run(action: 'merge' | 'dismiss') {
    setBusy(action);
    setError(null);
    try {
      if (action === 'merge') await mergeDuplicates(survivorId, mergeIds);
      else await dismissDuplicates(group.cards.map((card) => card.id));
      onDone();
    } catch (cause) {
      setError(messageOf(cause));
      setBusy(null);
    }
  }

  return (
    <section aria-labelledby={titleId} className="bg-surface grid gap-3 rounded-xl border p-3 sm:p-4">
      <div className="flex flex-wrap items-center gap-2">
        <h2 id={titleId} className="text-base font-semibold">
          {group.cards.length} {group.kind === 'exact' ? 'identical files' : 'cards that look alike'}
        </h2>
        <Badge variant={group.kind === 'exact' ? 'secondary' : 'outline'}>
          {group.kind === 'exact' ? 'Same file' : `Looks alike · ${group.maxDistance ?? 0} of 64 bits differ`}
        </Badge>
      </div>

      <div role="radiogroup" aria-label="Card to keep" className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
        {group.cards.map((card) => (
          <DuplicateTile key={card.id} card={card} group={group} keep={card.id === survivorId}
            included={!excluded.has(card.id)} onNavigate={onNavigate}
            onKeep={() => {
              setSurvivorId(card.id);
              setExcluded((current) => { const next = new Set(current); next.delete(card.id); return next; });
            }}
            onIncludedChange={(included) => setExcluded((current) => {
              const next = new Set(current);
              if (included) next.delete(card.id); else next.add(card.id);
              return next;
            })} />
        ))}
      </div>

      {group.conflict ? <p className="text-warning text-sm">{group.conflict}</p> : null}
      {error ? <p role="alert" className="text-destructive text-sm">{error}</p> : null}

      <div className="flex flex-wrap items-center justify-end gap-2">
        <Button type="button" variant="ghost" disabled={disabled || busy !== null} onClick={() => void run('dismiss')}>
          <X /> {busy === 'dismiss' ? 'Saving…' : 'Not duplicates'}
        </Button>
        <Button type="button" disabled={disabled || busy !== null || mergeIds.length === 0 || !!group.conflict}
          onClick={() => void run('merge')}>
          <Merge /> {busy === 'merge' ? 'Merging…' : `Merge ${mergeIds.length} into #${survivorId}`}
        </Button>
      </div>
    </section>
  );
}

function DuplicateTile({
  card, group, keep, included, onKeep, onIncludedChange, onNavigate,
}: {
  card: DuplicateCard;
  group: DuplicateGroup;
  keep: boolean;
  included: boolean;
  onKeep: () => void;
  onIncludedChange: (included: boolean) => void;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const facts = [
    card.width && card.height ? `${card.width}×${card.height}` : null,
    card.fileSize !== null ? formatBytes(card.fileSize) : null,
    card.mimeType ?? card.mediaType,
    card.duration ? formatTime(card.duration) : null,
  ].filter(Boolean).join(' · ');
  return (
    <div className={cn('grid content-start gap-2 rounded-lg border p-2 transition-colors duration-[var(--dur-fast)]',
      keep ? 'border-primary ring-primary/30 ring-2' : !included ? 'opacity-60' : '')}>
      <a href={`/cards/${card.id}`} onClick={onNavigate} aria-label={`Open card #${card.id}`}
        className="bg-muted block aspect-[4/3] overflow-hidden rounded-md">
        <CardPreview url={`/api/cards/${card.id}/preview`} title={card.title} type={card.mediaType ?? 'media'}
          imageClassName="size-full object-contain" fallbackClassName="size-full" />
      </a>
      <div className="grid min-w-0 gap-0.5 text-sm">
        <div className="flex min-w-0 items-baseline gap-1.5">
          <span className="text-muted-foreground shrink-0 tabular-nums">#{card.id}</span>
          <span className="truncate font-medium">{card.title || card.originalFileName || 'Untitled'}</span>
          {card.isFavorite ? <Heart aria-label="Favorite" className="text-primary size-3.5 shrink-0 fill-current" /> : null}
        </div>
        {facts ? <div className="text-muted-foreground tabular-nums">{facts}</div> : null}
        <div className="text-muted-foreground">
          {card.tagCount} {plural(card.tagCount, 'tag')} · {card.sourceCount} {plural(card.sourceCount, 'source')} · {card.viewCount} {plural(card.viewCount, 'view')}
          {card.createdAt ? ` · added ${card.createdAt.slice(0, 10)}` : ''}
        </div>
        {card.decks.length > 0 ? (
          <div className="text-muted-foreground truncate" title={card.decks.join(', ')}>
            <Layers className="mr-1 inline size-3.5 align-[-2px]" />{card.decks.join(', ')}
          </div>
        ) : null}
        {card.parentId ? (
          <div className="text-muted-foreground truncate">
            Page of {card.parentType ?? 'collection'}{' '}
            <a href={`/cards/${card.parentId}`} onClick={onNavigate} className="text-foreground underline-offset-2 hover:underline">
              {card.parentTitle || `#${card.parentId}`}
            </a>
          </div>
        ) : null}
        {group.kind === 'similar' && card.originalFileName && card.title ? (
          <div className="text-muted-foreground truncate" title={card.originalFileName}>{card.originalFileName}</div>
        ) : null}
      </div>
      <div className="flex flex-wrap items-center gap-x-4 gap-y-1">
        <button type="button" role="radio" aria-checked={keep} onClick={onKeep}
          className={cn('inline-flex min-h-11 items-center gap-2 text-sm font-medium', keep ? 'text-primary' : 'text-muted-foreground hover:text-foreground')}>
          <span className={cn('grid size-5 place-items-center rounded-full border-2', keep ? 'border-primary bg-primary text-primary-foreground' : 'border-border')}>
            {keep ? <Check className="size-3" /> : null}
          </span>
          {keep ? 'Keep this card' : 'Keep this one'}
        </button>
        {!keep && group.kind === 'similar' ? (
          <label className="text-muted-foreground flex min-h-11 items-center gap-2 text-sm">
            <Checkbox checked={included} onCheckedChange={(checked) => onIncludedChange(checked === true)} />
            Merge
          </label>
        ) : null}
      </div>
    </div>
  );
}

function formatBytes(bytes: number) {
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return `${value < 10 ? value.toFixed(1) : Math.round(value)} ${units[unit]}`;
}

function plural(count: number, one: string, many = `${one}s`) {
  return count === 1 ? one : many;
}

function messageOf(cause: unknown) {
  return cause instanceof Error ? cause.message : 'Request failed.';
}
