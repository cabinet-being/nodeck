import * as React from 'react';

import { cardTypes, listCards, type Card } from '@/api/cards';
import { Badge } from '@/components/ui/badge';
import { buttonVariants } from '@/components/ui/button';
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

  React.useEffect(() => {
    setIsLoading(true);
    setError(null);

    listCards({
      type: type || undefined,
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
          Create Card
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

      <div className="overflow-hidden rounded-lg border">
        <table className="w-full border-collapse text-sm">
          <thead className="bg-muted/50 text-muted-foreground">
            <tr>
              <th className="px-3 py-2 text-left font-medium">ID</th>
              <th className="px-3 py-2 text-left font-medium">Type</th>
              <th className="px-3 py-2 text-left font-medium">Title</th>
              <th className="px-3 py-2 text-left font-medium">Preview</th>
              <th className="px-3 py-2 text-left font-medium">Created</th>
            </tr>
          </thead>
          <tbody>
            {cards.map((card) => (
              <tr key={card.id} className="border-t">
                <td className="px-3 py-2">
                  <a href={`/cards/${card.id}`} onClick={onNavigate} className="hover:underline">
                    #{card.id}
                  </a>
                </td>
                <td className="px-3 py-2">
                  <Badge variant="outline">{card.type}</Badge>
                </td>
                <td className="px-3 py-2">{card.title}</td>
                <td className="px-3 py-2">{card.previewUrl ? 'yes' : 'no'}</td>
                <td className="px-3 py-2">{String(card.metadata.created_at ?? '')}</td>
              </tr>
            ))}
            {!isLoading && cards.length === 0 ? (
              <tr>
                <td className="text-muted-foreground px-3 py-6 text-center" colSpan={5}>
                  No cards
                </td>
              </tr>
            ) : null}
          </tbody>
        </table>
      </div>
    </section>
  );
}
