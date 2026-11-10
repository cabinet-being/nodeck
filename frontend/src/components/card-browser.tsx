import * as React from 'react';
import { ChevronLeft, ChevronRight, Images, Layers3, SearchX, Sparkles } from 'lucide-react';

import { listCards, type Card } from '@/api/cards';
import { getSource, getTag, type Source, type Tag } from '@/api/catalog';
import { EmptyState } from '@/components/empty-state';
import { FilterBar, type CatalogFilters } from '@/components/filter-bar';
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
  /** Change to refetch the current page. */
  reloadKey?: number;
  /** Called after each successful page load. */
  onPageLoaded?: (info: { count: number; page: number }) => void;
  emptyMessage?: string;
}) {
  const [cards, setCards] = React.useState<Card[]>([]);
  const [hasNextPage, setHasNextPage] = React.useState(false);
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
  const pagesMode = deckId === null && params.get('view') === 'pages';

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
    listCards({
      ...(deckId === null
        ? pagesMode
          ? { types: ['media'], sort: 'created_at' as const, order: 'desc' as const }
          : { types: ['media', 'comic', 'set'], excludeContainedMedia: true, sort: 'created_at' as const, order: 'desc' as const }
        : { deckId, sort: 'position' as const }),
      tags: parseIds(legacyTags || null),
      source: parseSingleId(legacySource),
      catalogTags: parseIds(catalogTagIds || null),
      catalogSource: parseSingleId(catalogSourceId),
      search: search || undefined,
      limit: pageSize + 1,
      offset: (page - 1) * pageSize,
    }).then((results) => {
      if (!active) return;
      const pageCards = results.slice(0, pageSize);
      setCards(pageCards);
      setHasNextPage(results.length > pageSize);
      onPageLoadedRef.current?.({ count: pageCards.length, page });
    })
      .catch((cause) => {
        if (!active) return;
        setCards([]);
        setHasNextPage(false);
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

  const filtersActive = search !== '' || catalogTagIds !== '' || catalogSourceId !== '' || legacyActive;

  return (
    <div className="flex flex-col gap-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        {title ?? <span />}
        <div className="flex items-center gap-2">
          {deckId === null ? (
            <Segmented
              ariaLabel="Show"
              value={pagesMode ? 'pages' : 'collections'}
              onValueChange={selectGalleryMode}
              options={[
                { value: 'collections', label: 'Collections', icon: <Layers3 /> },
                { value: 'pages', label: 'Pages', icon: <Images /> },
              ]}
            />
          ) : null}
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
        />
      ) : null}
      {!error && (page > 1 || hasNextPage) ? <nav className="flex items-center justify-center gap-3 py-2" aria-label="Pages">
        <Button type="button" variant="outline" disabled={loading || page <= 1}
          onClick={() => goToPage(page - 1)}><ChevronLeft className="size-4" />Previous</Button>
        <span className="text-muted-foreground text-sm tabular-nums">Page {page}</span>
        <Button type="button" variant="outline" disabled={loading || !hasNextPage}
          onClick={() => goToPage(page + 1)}>Next<ChevronRight className="size-4" /></Button>
      </nav> : null}
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
