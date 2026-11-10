import * as React from 'react';
import { Heart, Images } from 'lucide-react';

import { removeFavorite } from '@/api/cards';
import { getFavorites, type DeckCard } from '@/api/decks';
import { cardLabel } from '@/components/card-tile';
import { deckCardCountLabel } from '@/components/deck-stack';
import { EmptyState } from '@/components/empty-state';
import { Button, buttonVariants } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { useVisualCardView, VisualCardViewToggle, VisualCardWall } from '@/components/visual-card-wall';

export function FavoritesPage({
  onNavigate,
}: {
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const [cards, setCards] = React.useState<DeckCard[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [isLoading, setIsLoading] = React.useState(true);
  const [retryKey, setRetryKey] = React.useState(0);
  const [removingCardId, setRemovingCardId] = React.useState<number | null>(null);
  const [view, setView] = useVisualCardView('nodeck.favoritesView');

  React.useEffect(() => {
    let active = true;
    setError(null);
    setIsLoading(true);

    getFavorites()
      .then((deck) => { if (active) setCards(deck.cards); })
      .catch((requestError) => { if (active) setError(requestError.message); })
      .finally(() => { if (active) setIsLoading(false); });

    return () => { active = false; };
  }, [retryKey]);

  return (
    <section className="flex flex-col gap-5 px-4 py-6 sm:px-6 sm:py-8">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="flex items-center gap-2 text-2xl font-semibold tracking-tight">
          <Heart className="text-primary size-5 fill-current" aria-hidden="true" />
          Favorites
          {!isLoading && !error ? (
            <span className="text-muted-foreground text-base font-normal tabular-nums">· {deckCardCountLabel(cards.length)}</span>
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

      {isLoading ? (
        <div role="status" aria-label="Loading" className="grid grid-cols-2 gap-3 sm:grid-cols-3 sm:gap-4 md:grid-cols-4 xl:grid-cols-6">
          {Array.from({ length: 12 }, (_, index) => <Skeleton key={index} className="aspect-square rounded-2xl" />)}
        </div>
      ) : null}

      {!isLoading && !error && cards.length === 0 ? (
        <EmptyState icon={Heart} title="No favorites yet"
          action={<a href="/gallery" onClick={onNavigate} className={buttonVariants({ variant: 'outline' })}><Images className="size-4" /> Browse gallery</a>} />
      ) : null}

      {!isLoading && cards.length > 0 ? (
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
    </section>
  );

  async function handleRemove(cardId: number) {
    setRemovingCardId(cardId);
    setError(null);

    try {
      await removeFavorite(cardId);
      setCards((current) => current.filter((card) => card.id !== cardId));
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : 'Unable to remove favorite.');
    } finally {
      setRemovingCardId(null);
    }
  }
}
