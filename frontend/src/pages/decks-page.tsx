import * as React from 'react';
import { Layers3, MoreHorizontal, Pencil, Plus, SearchX, Trash2 } from 'lucide-react';

import { listDecksPage, type DeckSummary } from '@/api/decks';
import { DeckStack } from '@/components/deck-stack';
import { DeleteDeckDialog } from '@/components/delete-deck-dialog';
import { EmptyState } from '@/components/empty-state';
import { Button, buttonVariants } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Menu, MenuContent, MenuItem, MenuLinkItem, MenuTrigger } from '@/components/ui/menu';
import { Segmented } from '@/components/ui/segmented';
import { Skeleton } from '@/components/ui/skeleton';
import { pageCountOf, Pagination, useClampPage, usePageScroll, useResetPage } from '@/components/pagination';
import { parsePositiveInt, useUrlState } from '@/hooks/use-url-state';

const pageSize = 30;

export function DecksPage({
  onNavigate,
}: {
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const [decks, setDecks] = React.useState<DeckSummary[]>([]);
  const [search, setSearch] = useUrlState('q', '', { debounce: 300 });
  const [order, setOrder] = useUrlState<'asc' | 'desc'>('order', 'desc', {
    parse: (raw) => (raw === 'asc' ? 'asc' : undefined),
  });
  const [error, setError] = React.useState<string | null>(null);
  const [isLoading, setIsLoading] = React.useState(true);
  const [retryKey, setRetryKey] = React.useState(0);
  const [deckToDelete, setDeckToDelete] = React.useState<DeckSummary | null>(null);
  const [page, setPage] = useUrlState('page', 1, { parse: parsePositiveInt });
  const [total, setTotal] = React.useState(0);
  const pageCount = pageCountOf(total, pageSize);
  useResetPage(setPage, [order, search]);
  useClampPage(page, pageCount, !isLoading && !error, setPage);
  usePageScroll(page);

  React.useEffect(() => {
    let active = true;
    setIsLoading(true);
    setError(null);

    listDecksPage({ search: search || undefined, sort: 'created_at', order }, page, pageSize)
      .then((result) => { if (active) { setDecks(result.items); setTotal(result.total); } })
      .catch((requestError) => { if (active) setError(requestError.message); })
      .finally(() => { if (active) setIsLoading(false); });

    return () => { active = false; };
  }, [order, search, retryKey, page]);

  const newDeck = (
    <a href="/decks/new" onClick={onNavigate} className={buttonVariants()}>
      <Plus className="size-4" /> New deck
    </a>
  );

  return (
    <section className="flex flex-col gap-5 px-4 py-6 sm:px-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-2xl font-semibold tracking-tight">Decks</h1>
        {newDeck}
      </div>

      <div className="flex flex-wrap items-center gap-3">
        <Input
          type="search"
          value={search}
          aria-label="Search decks"
          placeholder="Search decks"
          className="min-w-0 flex-1 sm:max-w-sm"
          onChange={(event) => setSearch(event.target.value)}
        />
        <Segmented
          ariaLabel="Sort decks"
          value={order}
          onValueChange={setOrder}
          options={[
            { value: 'desc', label: 'Newest' },
            { value: 'asc', label: 'Oldest' },
          ]}
        />
      </div>

      {error ? (
        <div className="flex flex-wrap items-center gap-3" role="alert">
          <p className="text-destructive text-sm">{error}</p>
          <Button type="button" size="sm" variant="outline" onClick={() => setRetryKey((key) => key + 1)}>Try again</Button>
        </div>
      ) : null}

      {isLoading ? (
        <div role="status" aria-label="Loading" className="grid grid-cols-2 gap-x-4 gap-y-8 sm:grid-cols-3 md:grid-cols-4 xl:grid-cols-5">
          {Array.from({ length: 8 }, (_, index) => <Skeleton key={index} className="aspect-square rounded-2xl" />)}
        </div>
      ) : null}

      {!isLoading && !error && decks.length === 0 ? (
        search ? (
          <EmptyState icon={SearchX} title="No matching decks"
            action={<Button type="button" variant="outline" size="sm" onClick={() => setSearch('')}>Clear search</Button>} />
        ) : (
          <EmptyState icon={Layers3} title="No decks yet" action={newDeck} />
        )
      ) : null}

      {!isLoading && decks.length > 0 ? (
        <div className="grid grid-cols-2 gap-x-4 gap-y-8 sm:grid-cols-3 md:grid-cols-4 xl:grid-cols-5">
          {decks.map((deck) => (
            <DeckStack
              key={deck.id}
              deck={deck}
              href={`/decks/${deck.id}`}
              onNavigate={onNavigate}
              actions={deck.systemKey === 'favorites' ? null : (
                <Menu>
                  <MenuTrigger render={<Button type="button" variant="ghost" size="icon-sm"
                    aria-label={`Actions for ${deck.title}`} className="bg-surface/90 shadow-1 rounded-full" />}>
                    <MoreHorizontal />
                  </MenuTrigger>
                  <MenuContent>
                    <MenuLinkItem href={`/decks/${deck.id}/edit`} onClick={onNavigate}><Pencil /> Edit</MenuLinkItem>
                    <MenuItem onClick={() => setDeckToDelete(deck)} className="text-destructive [&_svg]:text-destructive">
                      <Trash2 /> Delete
                    </MenuItem>
                  </MenuContent>
                </Menu>
              )}
            />
          ))}
        </div>
      ) : null}

      <Pagination page={page} pageCount={pageCount} total={total} disabled={isLoading} onPageChange={setPage} />

      <DeleteDeckDialog
        deck={deckToDelete}
        onClose={() => setDeckToDelete(null)}
        onDeleted={(id) => {
          setDecks((current) => current.filter((deck) => deck.id !== id));
          setDeckToDelete(null);
          setRetryKey((key) => key + 1);
        }}
      />
    </section>
  );
}
