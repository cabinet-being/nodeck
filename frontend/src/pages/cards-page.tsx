import * as React from 'react';
import { MoreHorizontal, Pencil, Plus, Trash2 } from 'lucide-react';

import { cardTypes, listCards, type Card } from '@/api/cards';
import { FavoriteToggle } from '@/components/favorite-toggle';
import { CardPreview } from '@/components/card-preview';
import { Badge } from '@/components/ui/badge';
import { Button, buttonVariants } from '@/components/ui/button';
import { DeleteCardDialog } from '@/components/delete-card-dialog';
import { EmptyState } from '@/components/empty-state';
import { Input } from '@/components/ui/input';
import { Menu, MenuContent, MenuItem, MenuLinkItem, MenuTrigger } from '@/components/ui/menu';
import { Segmented } from '@/components/ui/segmented';
import { Skeleton } from '@/components/ui/skeleton';
import { useUrlState } from '@/hooks/use-url-state';

export function CardsPage({
  onNavigate,
}: {
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const [cards, setCards] = React.useState<Card[]>([]);
  const [search, setSearch] = useUrlState('q', '', { debounce: 300 });
  const [type, setType] = useUrlState('type', '', {
    parse: (raw) => ((cardTypes as readonly string[]).includes(raw) ? raw : undefined),
  });
  const [order, setOrder] = useUrlState<'asc' | 'desc'>('order', 'desc', {
    parse: (raw) => (raw === 'asc' ? 'asc' : undefined),
  });
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
    <section className="mx-auto flex w-full max-w-5xl flex-col gap-4 p-4 sm:p-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-2xl font-semibold">Manage cards</h1>
        <a href="/cards/new" onClick={onNavigate} className={buttonVariants()}>
          <Plus className="size-4" /> Create card
        </a>
      </div>

      <div className="flex flex-wrap items-center gap-3">
        <Input
          type="search"
          aria-label="Search cards"
          className="min-w-48 flex-1"
          value={search}
          placeholder="Search by title"
          onChange={(event) => setSearch(event.target.value)}
        />
        <Segmented
          ariaLabel="Sort cards"
          value={order}
          onValueChange={setOrder}
          options={[
            { value: 'desc', label: 'Newest' },
            { value: 'asc', label: 'Oldest' },
          ]}
        />
      </div>
      <div className="-mx-4 overflow-x-auto px-4 sm:mx-0 sm:px-0">
        <Segmented
          ariaLabel="Card type"
          value={type}
          onValueChange={setType}
          options={[
            { value: '', label: 'All types' },
            ...cardTypes.map((cardType) => ({ value: cardType, label: cardType })),
          ]}
        />
      </div>

      {error ? <div role="alert" className="text-destructive text-sm">{error}</div> : null}

      {isLoading && cards.length === 0 ? (
        <div role="status" aria-label="Loading" className="grid gap-2">
          {Array.from({ length: 6 }, (_, index) => <Skeleton key={index} className="h-20 rounded-xl" />)}
        </div>
      ) : null}

      <div className="bg-surface divide-y overflow-hidden rounded-xl border">
        {cards.map((card) => (
          <div key={card.id} className="hover:bg-muted/40 flex min-w-0 items-center gap-3 p-2.5">
            <a href={`/cards/${card.id}`} onClick={onNavigate} aria-label={card.title || `Card ${card.id}`}
              className="bg-muted size-16 shrink-0 overflow-hidden rounded-lg">
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
              <Menu>
                <MenuTrigger render={<Button type="button" variant="ghost" size="icon"
                  aria-label={`Actions for ${card.title || `card ${card.id}`}`} />}>
                  <MoreHorizontal />
                </MenuTrigger>
                <MenuContent>
                  <MenuLinkItem href={`/cards/${card.id}/edit`} onClick={onNavigate}><Pencil /> Edit</MenuLinkItem>
                  <MenuItem onClick={() => setCardToDelete(card)} className="text-destructive [&_svg]:text-destructive">
                    <Trash2 /> Delete
                  </MenuItem>
                </MenuContent>
              </Menu>
            </div>
          </div>
        ))}
        {!isLoading && cards.length === 0 && !error ? <EmptyState title="No cards found" /> : null}
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
