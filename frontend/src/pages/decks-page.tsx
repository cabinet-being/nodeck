import * as React from 'react';
import { Heart, Layers3, Pencil, Plus, Trash2 } from 'lucide-react';

import { deleteDeck, listDecks, type DeckSummary } from '@/api/decks';
import { Button, buttonVariants } from '@/components/ui/button';
import { Dialog } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';

export function DecksPage({
  onNavigate,
}: {
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const [decks, setDecks] = React.useState<DeckSummary[]>([]);
  const [search, setSearch] = React.useState('');
  const [order, setOrder] = React.useState<'asc' | 'desc'>('desc');
  const [error, setError] = React.useState<string | null>(null);
  const [isLoading, setIsLoading] = React.useState(true);
  const [deckToDelete, setDeckToDelete] = React.useState<DeckSummary | null>(null);
  const [isDeleting, setIsDeleting] = React.useState(false);

  React.useEffect(() => {
    setIsLoading(true);
    setError(null);

    listDecks({
      search: search || undefined,
      sort: 'created_at',
      order,
    })
      .then(setDecks)
      .catch((requestError) => setError(requestError.message))
      .finally(() => setIsLoading(false));
  }, [order, search]);

  return (
    <section className="flex flex-col gap-4 p-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-2xl font-semibold">Decks</h1>
        <a href="/decks/new" onClick={onNavigate} className={buttonVariants()}>
          <Plus className="size-4" /> Create deck
        </a>
      </div>

      <div className="grid gap-3 md:grid-cols-[1fr_12rem]">
        <Input
          value={search}
          placeholder="Search by ID or title"
          onChange={(event) => setSearch(event.target.value)}
        />
        <select
          value={order}
          onChange={(event) => setOrder(event.target.value as 'asc' | 'desc')}
          className="border-input bg-background h-9 rounded-lg border px-3 text-sm"
        >
          <option value="desc">Newest first</option>
          <option value="asc">Oldest first</option>
        </select>
      </div>

      {error ? <div className="text-destructive text-sm">{error}</div> : null}

      <div className="divide-y overflow-hidden rounded-xl border">
        {decks.map((deck) => (
          <div key={deck.id} className="flex min-w-0 items-center gap-3 p-3 hover:bg-muted/30">
            <a href={`/decks/${deck.id}`} onClick={onNavigate}
              className="bg-muted text-muted-foreground flex size-12 shrink-0 items-center justify-center rounded-lg"
              aria-label={`Open ${deck.title}`}>
              {deck.systemKey === 'favorites' ? <Heart className="size-5" /> : <Layers3 className="size-5" />}
            </a>
            <div className="min-w-0 flex-1">
              <a href={`/decks/${deck.id}`} onClick={onNavigate}
                className="block truncate text-sm font-medium hover:underline">{deck.title}</a>
              <span className="text-muted-foreground text-xs">{deck.cardCount} cards</span>
            </div>
            <div className="flex shrink-0 justify-end gap-1">
              {deck.systemKey === 'favorites' ? null : (
                <>
                  <a href={`/decks/${deck.id}/edit`} onClick={onNavigate}
                    className={buttonVariants({ variant: 'ghost', size: 'icon' })}
                    aria-label={`Edit deck ${deck.id}`}>
                    <Pencil className="size-4" />
                  </a>
                  <Button type="button" variant="ghost" size="icon"
                    aria-label={`Delete deck ${deck.id}`} onClick={() => setDeckToDelete(deck)}>
                    <Trash2 className="size-4" />
                  </Button>
                </>
              )}
            </div>
          </div>
        ))}
        {!isLoading && decks.length === 0 ? (
          <p className="text-muted-foreground px-3 py-8 text-center text-sm">No decks</p>
        ) : null}
      </div>

      <Dialog
        open={deckToDelete !== null}
        title="Delete deck"
        description={
          deckToDelete
            ? `Delete deck #${deckToDelete.id} ${deckToDelete.title}? This removes ${deckToDelete.cardCount} membership(s). Deleting this deck will not delete its cards.`
            : undefined
        }
        onOpenChange={(open) => {
          if (!open && !isDeleting) {
            setDeckToDelete(null);
          }
        }}
      >
        <div className="flex justify-end gap-2">
          <Button
            type="button"
            variant="outline"
            disabled={isDeleting}
            onClick={() => setDeckToDelete(null)}
          >
            Cancel
          </Button>
          <Button
            type="button"
            variant="destructive"
            disabled={isDeleting || deckToDelete === null}
            onClick={handleDelete}
          >
            {isDeleting ? 'Deleting...' : 'Delete deck'}
          </Button>
        </div>
      </Dialog>
    </section>
  );

  async function handleDelete() {
    if (!deckToDelete) {
      return;
    }

    setIsDeleting(true);
    setError(null);

    try {
      await deleteDeck(deckToDelete.id);
      setDecks((current) => current.filter((deck) => deck.id !== deckToDelete.id));
      setDeckToDelete(null);
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : 'Unable to delete deck.');
    } finally {
      setIsDeleting(false);
    }
  }
}
