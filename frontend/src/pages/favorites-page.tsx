import * as React from 'react';
import { X } from 'lucide-react';

import { resolveApiUrl, removeFavorite } from '@/api/cards';
import { getFavorites, type DeckCard } from '@/api/decks';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';

export function FavoritesPage({
  onNavigate,
}: {
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const [cards, setCards] = React.useState<DeckCard[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [removingCardId, setRemovingCardId] = React.useState<number | null>(null);

  React.useEffect(() => {
    setError(null);

    getFavorites()
      .then((deck) => setCards(deck.cards))
      .catch((requestError) => setError(requestError.message));
  }, []);

  return (
    <section className="flex flex-col gap-4 p-6">
      <div>
        <h1 className="text-2xl font-semibold">Favorites</h1>
        <div className="text-muted-foreground text-sm">{cards.length} card(s)</div>
      </div>

      {error ? <div className="text-destructive text-sm">{error}</div> : null}

      <div className="grid gap-3">
        {cards.map((card) => (
          <FavoriteCard
            key={card.id}
            card={card}
            isRemoving={removingCardId === card.id}
            onNavigate={onNavigate}
            onRemove={handleRemove}
          />
        ))}
        {cards.length === 0 ? (
          <div className="text-muted-foreground rounded-lg border p-6 text-center text-sm">
            No favorite cards
          </div>
        ) : null}
      </div>
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

function FavoriteCard({
  card,
  isRemoving,
  onNavigate,
  onRemove,
}: {
  card: DeckCard;
  isRemoving: boolean;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onRemove: (cardId: number) => void;
}) {
  return (
    <div className="grid gap-3 rounded-lg border p-3 hover:bg-muted/50 md:grid-cols-[auto_1fr_auto]">
      {card.previewUrl ? (
        <a href={`/cards/${card.id}`} onClick={onNavigate}>
          <img
            src={resolveApiUrl(card.previewUrl)}
            alt={card.title ?? ''}
            className="bg-muted size-20 rounded border object-cover"
            loading="lazy"
          />
        </a>
      ) : (
        <div className="flex items-start pt-1">
          <Badge variant="outline">{card.type}</Badge>
        </div>
      )}
      <a href={`/cards/${card.id}`} onClick={onNavigate} className="min-w-0">
        <div className="mb-1 flex flex-wrap items-center gap-2">
          <span className="text-muted-foreground text-sm">#{card.id}</span>
          {card.previewUrl ? <Badge variant="outline">{card.type}</Badge> : null}
        </div>
        <div className="truncate font-medium">{card.title ?? card.type}</div>
      </a>
      <Button
        type="button"
        variant="ghost"
        size="icon"
        aria-label={`Remove card ${card.id} from Favorites`}
        disabled={isRemoving}
        onClick={() => onRemove(card.id)}
      >
        <X className="size-4" />
      </Button>
    </div>
  );
}
