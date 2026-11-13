import * as React from 'react';
import { Heart, Images } from 'lucide-react';

import { listCardsPage, removeFavorite, type Card } from '@/api/cards';
import { getFavoritesDeck } from '@/api/decks';
import { cardLabel } from '@/components/card-tile';
import { deckCardCountLabel } from '@/components/deck-stack';
import { EmptyState } from '@/components/empty-state';
import { Button, buttonVariants } from '@/components/ui/button';
import { pageCountOf, Pagination, useClampPage, usePageScroll } from '@/components/pagination';
import { useVisualCardView, VisualCardViewToggle, VisualCardWall, VisualCardWallSkeleton } from '@/components/visual-card-wall';
import { parsePositiveInt, useUrlState } from '@/hooks/use-url-state';

const pageSize = 48;

export function FavoritesPage({
  onNavigate,
}: {
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const [cards, setCards] = React.useState<Card[]>([]);
  const [total, setTotal] = React.useState(0);
  const [page, setPage] = useUrlState('page', 1, { parse: parsePositiveInt });
  const [error, setError] = React.useState<string | null>(null);
  const [isLoading, setIsLoading] = React.useState(true);
  const [retryKey, setRetryKey] = React.useState(0);
  const [removingCardId, setRemovingCardId] = React.useState<number | null>(null);
  const [view, setView] = useVisualCardView();
  const pageCount = pageCountOf(total, pageSize);
  useClampPage(page, pageCount, !isLoading && !error, setPage);
  usePageScroll(page);

  React.useEffect(() => {
    let active = true;
    setError(null);
    setIsLoading(true);

    // The favorites deck is read without its cards; its cards are paged in deck order.
    getFavoritesDeck()
      .then((deck) => listCardsPage({ deckId: deck.id, sort: 'position' }, page, pageSize))
      .then((result) => { if (active) { setCards(result.items); setTotal(result.total); } })
      .catch((requestError) => { if (active) setError(requestError.message); })
      .finally(() => { if (active) setIsLoading(false); });

    return () => { active = false; };
  }, [retryKey, page]);

  return (
    <section className="flex flex-col gap-5 px-4 py-6 sm:px-6 sm:py-8">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="flex items-center gap-2 text-2xl font-semibold tracking-tight">
          <Heart className="text-primary size-5 fill-current" aria-hidden="true" />
          Favorites
          {!isLoading && !error ? (
            <span className="text-muted-foreground text-base font-normal tabular-nums">· {deckCardCountLabel(total)}</span>
          ) : null}
        </h1>
        {cards.length > 0 ? <VisualCardViewToggle view={view} onChange={setView} /> : null}
      </div>

      {error ? (
        <div className="flex flex-wrap items-center gap-3" role="alert">
          <p className="text-destructive text-sm">{error}</p>
          <Button type="button" size="sm" variant="outline" onClick={() => setRetryKey((key) => key + 1)}>Try again</Button>
        </div>
      ) : null}

      {isLoading && cards.length === 0 ? <VisualCardWallSkeleton count={12} /> : null}
      {!isLoading && !error && cards.length === 0 ? (
        <EmptyState icon={Heart} title="No favorites yet"
          action={<a href="/gallery" onClick={onNavigate} className={buttonVariants({ variant: 'outline' })}><Images className="size-4" /> Browse gallery</a>} />
      ) : null}

      {cards.length > 0 ? (
        <VisualCardWall
          cards={cards}
          view={view}
          onNavigate={onNavigate}
          renderActions={(index) => {
            const card = cards[index];
            return (
              <Button
                type="button"
                variant="secondary"
                size="icon"
                aria-label="Remove from favorites"
                title={`Remove ${cardLabel(card)} from favorites`}
                disabled={removingCardId === card.id}
                className="bg-surface/95 text-primary shadow-1 size-8 rounded-full pointer-coarse:size-11"
                onClick={(event) => { event.preventDefault(); event.stopPropagation(); void handleRemove(card.id); }}
              >
                <Heart className="fill-current" />
              </Button>
            );
          }}
        />
      ) : null}
      <Pagination page={page} pageCount={pageCount} disabled={isLoading} onPageChange={setPage} />
    </section>
  );

  async function handleRemove(cardId: number) {
    setRemovingCardId(cardId);
    setError(null);

    try {
      await removeFavorite(cardId);
      setCards((current) => current.filter((card) => card.id !== cardId));
      setTotal((current) => Math.max(0, current - 1));
      // Refill the page from the next one.
      setRetryKey((key) => key + 1);
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : 'Unable to remove favorite.');
    } finally {
      setRemovingCardId(null);
    }
  }
}
