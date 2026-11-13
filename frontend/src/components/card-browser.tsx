import * as React from 'react';
import { BookOpen, CheckSquare, Images, Layers, Layers3, Plus, SearchX, Sparkles, X } from 'lucide-react';

import { listCardsPage, type Card } from '@/api/cards';
import { getSource, getTag, type Source, type Tag } from '@/api/catalog';
import { CollectionComposer, type ComposerMode } from '@/components/collection-composer';
import { EmptyState } from '@/components/empty-state';
import { type SortableCard } from '@/components/sortable-card-list';
import { FilterBar, type CatalogFilters } from '@/components/filter-bar';
import { pageCountOf, Pagination, usePageScroll } from '@/components/pagination';
import { Button } from '@/components/ui/button';
import { Segmented } from '@/components/ui/segmented';
import {
  useVisualCardView,
  VisualCardViewToggle,
  VisualCardWall,
  VisualCardWallSkeleton,
} from '@/components/visual-card-wall';

const pageSize = 48;

export type CardBrowserScope = { kind: 'all' } | { kind: 'deck'; deckId: number };

export function CardBrowser({
  scope,
  basePath,
  currentSearch,
  onNavigate,
  onNavigateTo,
  title,
  renderCaption,
  renderActions,
  onFavoriteChange,
  reloadKey = 0,
  onPageLoaded,
  emptyMessage = 'Nothing here yet.',
}: {
  scope: CardBrowserScope;
  basePath: string;
  currentSearch: string;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onNavigateTo: (path: string) => void;
  title?: React.ReactNode;
  /** Optional caption below each card; makes the wall show captions. */
  renderCaption?: (card: Card, index: number) => React.ReactNode;
  /** Optional round port buttons on each tile. */
  renderActions?: (card: Card, index: number) => React.ReactNode;
  /** Called after a tile's favorite button added or removed the card. */
  onFavoriteChange?: (card: Card, isFavorite: boolean) => void;
  /** Change to refetch the current page. */
  reloadKey?: number;
  /** Called after each successful page load. */
  onPageLoaded?: (info: { count: number; page: number }) => void;
  emptyMessage?: string;
}) {
  const [cards, setCards] = React.useState<Card[]>([]);
  const [total, setTotal] = React.useState(0);
  const [loading, setLoading] = React.useState(true);
  const [error, setError] = React.useState<string | null>(null);
  const [retryKey, setRetryKey] = React.useState(0);
  const [view, setView] = useVisualCardView();
  const [tags, setTags] = React.useState<Tag[]>([]);
  const [source, setSource] = React.useState<Source | null>(null);
  const onPageLoadedRef = React.useRef(onPageLoaded);
  onPageLoadedRef.current = onPageLoaded;

  const params = new URLSearchParams(currentSearch);
  const page = parsePage(params.get('page'));
  const search = params.get('search')?.trim() ?? '';
  const catalogTagIds = params.get('catalog_tags') ?? '';
  const catalogSourceId = params.get('catalog_source') ?? '';
  const legacyTags = params.get('tags') ?? '';
  const legacySource = params.get('source') ?? '';
  const legacyActive = legacyTags !== '' || legacySource !== '';
  const deckId = scope.kind === 'deck' ? scope.deckId : null;
  // Gallery: "pages" lists media pages instead of collections. Deck: "pages" also lists the
  // pages of comics and sets that are in the deck themselves, which are hidden by default.
  const pagesMode = params.get('view') === 'pages';
  const [selecting, setSelecting] = React.useState(false);
  const [selected, setSelected] = React.useState<SortableCard[]>([]);
  const [composerMode, setComposerMode] = React.useState<ComposerMode | null>(null);
  const [lastComposerMode, setLastComposerMode] = React.useState<ComposerMode>('comic');

  React.useEffect(() => {
    setSelecting(false);
    setSelected([]);
    setComposerMode(null);
  }, [deckId]);

  React.useEffect(() => {
    if (!selecting || composerMode !== null) return;
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') stopSelecting();
    }
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [selecting, composerMode]);

  React.useEffect(() => {
    let active = true;
    const sourceId = parseSingleId(catalogSourceId);
    Promise.all([
      Promise.all(parseIds(catalogTagIds).map((id) => getTag(id).catch(() => null))),
      sourceId ? getSource(sourceId).catch(() => null) : Promise.resolve(null),
    ]).then(([resolvedTags, resolvedSource]) => {
      if (!active) return;
      setTags(resolvedTags.filter((tag): tag is Tag => tag !== null));
      setSource(resolvedSource);
    });
    return () => { active = false; };
  }, [catalogTagIds, catalogSourceId]);

  React.useEffect(() => {
    let active = true;
    setError(null);
    setLoading(true);
    listCardsPage({
      ...(deckId === null
        ? pagesMode
          ? { types: ['media'], sort: 'created_at' as const, order: 'desc' as const }
          : { types: ['media', 'comic', 'set'], excludeContainedMedia: true, sort: 'created_at' as const, order: 'desc' as const }
        : { deckId, sort: 'position' as const, collapsePages: !pagesMode }),
      tags: parseIds(legacyTags || null),
      source: parseSingleId(legacySource),
      catalogTags: parseIds(catalogTagIds || null),
      catalogSource: parseSingleId(catalogSourceId),
      search: search || undefined,
    }, page, pageSize).then((result) => {
      if (!active) return;
      setCards(result.items);
      setTotal(result.total);
      onPageLoadedRef.current?.({ count: result.items.length, page });
    })
      .catch((cause) => {
        if (!active) return;
        setCards([]);
        setTotal(0);
        setError(cause instanceof Error ? cause.message : 'Unable to load cards.');
      })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [deckId, pagesMode, search, catalogTagIds, catalogSourceId, legacyTags, legacySource, page, reloadKey, retryKey]);

  function navigateWith(next: URLSearchParams) {
    const query = next.toString();
    onNavigateTo(`${basePath}${query ? `?${query}` : ''}`);
  }

  function applyFilters(filters: CatalogFilters) {
    const next = new URLSearchParams();
    if (filters.search.trim()) next.set('search', filters.search.trim());
    if (filters.tags.length > 0) next.set('catalog_tags', filters.tags.map((tag) => tag.id).join(','));
    if (filters.source) next.set('catalog_source', String(filters.source.id));
    if (pagesMode) next.set('view', 'pages');
    navigateWith(next);
  }

  function selectGalleryMode(mode: 'collections' | 'pages') {
    const next = new URLSearchParams(currentSearch);
    next.delete('page');
    if (mode === 'pages') next.set('view', 'pages');
    else next.delete('view');
    navigateWith(next);
  }

  function stopSelecting() {
    setSelecting(false);
    setSelected([]);
  }

  function toggleSelected(card: Card) {
    setSelected((current) => current.some((item) => item.id === card.id)
      ? current.filter((item) => item.id !== card.id)
      : [...current, card]);
  }

  function openComposer(mode: ComposerMode) {
    setLastComposerMode(mode);
    setComposerMode(mode);
  }

  function clearFilters() {
    const next = new URLSearchParams();
    if (pagesMode) next.set('view', 'pages');
    navigateWith(next);
  }

  function clearLegacy() {
    const next = new URLSearchParams(currentSearch);
    next.delete('tags');
    next.delete('source');
    next.delete('page');
    navigateWith(next);
  }

  function goToPage(nextPage: number) {
    const next = new URLSearchParams(currentSearch);
    if (nextPage <= 1) next.delete('page');
    else next.set('page', String(nextPage));
    navigateWith(next);
  }

  usePageScroll(page);
  const pageCount = pageCountOf(total, pageSize);

  const selectionOrder = new Map(selected.map((card, index) => [card.id, index + 1]));
  const filtersActive = search !== '' || catalogTagIds !== '' || catalogSourceId !== '' || legacyActive;

  return (
    <div className="flex flex-col gap-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        {title ?? <span />}
        <div className="flex flex-wrap items-center gap-2">
          <Segmented
            ariaLabel="Show"
            value={pagesMode ? 'pages' : 'collections'}
            onValueChange={selectGalleryMode}
            options={[
              { value: 'collections', label: 'Collections', icon: <Layers3 /> },
              { value: 'pages', label: deckId === null ? 'Pages' : 'With pages', icon: <Images /> },
            ]}
          />
          <Button type="button" variant={selecting ? 'default' : 'outline'} size="sm" aria-pressed={selecting}
            onClick={() => (selecting ? stopSelecting() : setSelecting(true))}>
            <CheckSquare className="size-3.5" aria-hidden="true" />
            {selecting ? 'Done' : 'Select'}
          </Button>
          <VisualCardViewToggle view={view} onChange={setView} />
        </div>
      </div>
      <FilterBar
        filters={{ search, tags, source }}
        legacyActive={legacyActive}
        onChange={applyFilters}
        onClearAll={clearFilters}
        onClearLegacy={clearLegacy}
      />
      {error ? <div className="flex flex-wrap items-center gap-3" role="alert">
        <p className="text-destructive text-sm">{error}</p>
        <Button type="button" size="sm" variant="outline" onClick={() => setRetryKey((key) => key + 1)}>Try again</Button>
      </div> : null}
      {loading ? <VisualCardWallSkeleton count={pageSize} /> : null}
      {!loading && !error && cards.length === 0 ? (
        filtersActive ? (
          <EmptyState icon={SearchX} title="No matches"
            action={<Button type="button" variant="outline" size="sm" onClick={clearFilters}>Clear filters</Button>} />
        ) : page > 1 ? (
          <EmptyState icon={Sparkles} title="Nothing on this page"
            action={<Button type="button" variant="outline" size="sm" onClick={() => goToPage(1)}>Back to the start</Button>} />
        ) : <EmptyState icon={Sparkles} title={emptyMessage} />
      ) : null}
      {!loading && cards.length > 0 ? (
        <VisualCardWall
          cards={cards}
          view={view}
          onNavigate={onNavigate}
          renderCaption={renderCaption ? (index) => renderCaption(cards[index], index) : undefined}
          renderActions={renderActions ? (index) => renderActions(cards[index], index) : undefined}
          onFavoriteChange={onFavoriteChange ? (index, isFavorite) => onFavoriteChange(cards[index], isFavorite) : undefined}
          selection={selecting ? (index) => {
            const card = cards[index];
            return {
              order: selectionOrder.get(card.id) ?? null,
              disabledReason: pageDisabledReason(card),
              onToggle: () => toggleSelected(card),
            };
          } : undefined}
        />
      ) : null}
      {!error ? (
        <Pagination page={page} pageCount={Math.max(pageCount, page)} total={total} disabled={loading}
          onPageChange={goToPage} />
      ) : null}
      {selecting ? (
        <>
          <div aria-hidden="true" className="h-20" />
          <SelectionBar
            count={selected.length}
            onCompose={openComposer}
            onClear={() => setSelected([])}
            onClose={stopSelecting}
          />
        </>
      ) : null}
      <CollectionComposer
        open={composerMode !== null}
        onOpenChange={(open) => { if (!open) setComposerMode(null); }}
        mode={composerMode ?? lastComposerMode}
        cards={selected}
        onCardsChange={setSelected}
        onDone={(path) => {
          setComposerMode(null);
          stopSelecting();
          onNavigateTo(path);
        }}
      />
    </div>
  );
}

/** Only media can become a page; a page of another comic or set is moved out of it. */
function pageDisabledReason(card: Card) {
  if (card.type !== 'media') return 'Comics and sets cannot become pages';
  return null;
}

function SelectionBar({
  count,
  onCompose,
  onClear,
  onClose,
}: {
  count: number;
  onCompose: (mode: ComposerMode) => void;
  onClear: () => void;
  onClose: () => void;
}) {
  return (
    <div
      role="toolbar"
      aria-label="Selection"
      className="bg-popover text-popover-foreground shadow-3 fixed inset-x-3 bottom-[calc(4rem+env(safe-area-inset-bottom))] z-40 mx-auto flex max-w-2xl flex-wrap items-center gap-2 rounded-[var(--radius-panel)] border p-2 md:bottom-6"
    >
      <span className="min-w-0 flex-1 px-2 text-sm font-medium tabular-nums" aria-live="polite">
        {count === 0 ? 'Tap media to choose pages' : `${count} selected`}
      </span>
      {count > 0 ? (
        <Button type="button" variant="ghost" size="sm" onClick={onClear}>Clear</Button>
      ) : null}
      <Button type="button" size="icon" variant="ghost" aria-label="Stop selecting" onClick={onClose} className="sm:order-last">
        <X className="size-4" />
      </Button>
      <div className="grid w-full grid-cols-3 gap-1.5 sm:flex sm:w-auto">
        <Button type="button" size="sm" disabled={count < 1} onClick={() => onCompose('comic')}>
          <BookOpen className="size-3.5" aria-hidden="true" /><span className="sm:hidden">Comic</span><span className="max-sm:hidden">Make comic</span>
        </Button>
        <Button type="button" size="sm" variant="outline" disabled={count < 1} onClick={() => onCompose('set')}>
          <Layers className="size-3.5" aria-hidden="true" /><span className="sm:hidden">Set</span><span className="max-sm:hidden">Make set</span>
        </Button>
        <Button type="button" size="sm" variant="outline" disabled={count < 1} onClick={() => onCompose('add')}>
          <Plus className="size-3.5" aria-hidden="true" /> Add to…
        </Button>
      </div>
    </div>
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
