import * as React from 'react';
import { Pencil, Plus, Trash2 } from 'lucide-react';

import { cardTypes, listCards, type Card } from '@/api/cards';
import { FavoriteToggle } from '@/components/favorite-toggle';
import { CardPreview } from '@/components/card-preview';
import { Badge } from '@/components/ui/badge';
import { Button, buttonVariants } from '@/components/ui/button';
import { DeleteCardDialog } from '@/components/delete-card-dialog';
import { Input } from '@/components/ui/input';

export function CardsPage({
  onNavigate,
}: {
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const [cards, setCards] = React.useState<Card[]>([]);
  const [search, setSearch] = React.useState('');
  const [type, setType] = React.useState('');
  const [order, setOrder] = React.useState<'asc' | 'desc'>('desc');
  const [error, setError] = React.useState<string | null>(null);
  const [isLoading, setIsLoading] = React.useState(true);
  const [cardToDelete, setCardToDelete] = React.useState<Card | null>(null);

  React.useEffect(() => {
    setIsLoading(true);
    setError(null);

    listCards({
      types: type ? [type] : [...cardTypes],
      search: search || undefined,
      sort: 'created_at',
      order,
    })
      .then(setCards)
      .catch((requestError) => setError(requestError.message))
      .finally(() => setIsLoading(false));
  }, [order, search, type]);

  return (
    <section className="flex flex-col gap-4 p-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-2xl font-semibold">Cards</h1>
        <a href="/cards/new" onClick={onNavigate} className={buttonVariants()}>
          <Plus className="size-4" /> Create card
        </a>
      </div>

      <div className="grid gap-3 md:grid-cols-[1fr_12rem_12rem]">
        <Input
          value={search}
          placeholder="Search by ID or title"
          onChange={(event) => setSearch(event.target.value)}
        />
        <select
          value={type}
          onChange={(event) => setType(event.target.value)}
          className="border-input bg-background h-9 rounded-lg border px-3 text-sm"
        >
          <option value="">All types</option>
          {cardTypes.map((cardType) => (
            <option key={cardType} value={cardType}>
              {cardType}
            </option>
          ))}
        </select>
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
        {cards.map((card) => (
          <div key={card.id} className="flex min-w-0 items-center gap-3 p-2.5 hover:bg-muted/30">
            <a href={`/cards/${card.id}`} onClick={onNavigate} className="size-16 shrink-0 overflow-hidden rounded-lg bg-muted">
              <CardPreview url={card.previewUrl} title={card.title} type={card.type}
                imageClassName="size-full object-cover" fallbackClassName="size-full" />
            </a>
            <div className="min-w-0 flex-1">
              <a href={`/cards/${card.id}`} onClick={onNavigate}
                className="block truncate text-sm font-medium hover:underline" title={card.title || `Card ${card.id}`}>
                {card.title || `Card ${card.id}`}
              </a>
              <Badge variant="outline" className="mt-1 text-[10px]">{card.type}</Badge>
            </div>
            <div className="flex shrink-0 items-center gap-1">
              <FavoriteToggle
                cardId={card.id}
                isFavorite={card.isFavorite}
                onChange={(isFavorite) => updateFavorite(card.id, isFavorite)}
              />
              <a href={`/cards/${card.id}/edit`} onClick={onNavigate}
                className={buttonVariants({ variant: 'ghost', size: 'icon' })}
                aria-label={`Edit card ${card.id}`}>
                <Pencil className="size-4" />
              </a>
              <Button type="button" variant="ghost" size="icon"
                aria-label={`Delete card ${card.id}`} onClick={() => setCardToDelete(card)}>
                <Trash2 className="size-4" />
              </Button>
            </div>
          </div>
        ))}
        {!isLoading && cards.length === 0 ? (
          <p className="text-muted-foreground px-3 py-8 text-center text-sm">No cards</p>
        ) : null}
      </div>

      <DeleteCardDialog
        card={cardToDelete}
        onClose={() => setCardToDelete(null)}
        onDeleted={(removedIds) => {
          const removed = new Set(removedIds);
          setCards((current) => current.filter((card) => !removed.has(card.id)));
          setCardToDelete(null);
        }}
      />
    </section>
  );

  function updateFavorite(cardId: number, isFavorite: boolean) {
    setCards((current) =>
      current.map((card) => (card.id === cardId ? { ...card, isFavorite } : card))
    );
  }
}
