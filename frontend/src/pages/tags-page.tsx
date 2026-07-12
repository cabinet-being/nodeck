import * as React from 'react';

import { listCards, type Card } from '@/api/cards';
import { Badge } from '@/components/ui/badge';
import { Input } from '@/components/ui/input';

export function TagsPage({
  onNavigate,
}: {
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  return (
    <TextCardIndexPage
      title="Tags"
      cardType="tag"
      emptyMessage="No tags"
      onNavigate={onNavigate}
    />
  );
}

export function TextCardIndexPage({
  title,
  cardType,
  emptyMessage,
  onNavigate,
}: {
  title: string;
  cardType: 'tag' | 'source';
  emptyMessage: string;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const [cards, setCards] = React.useState<Card[]>([]);
  const [search, setSearch] = React.useState('');
  const [error, setError] = React.useState<string | null>(null);

  React.useEffect(() => {
    listCards({
      type: cardType,
      search: search || undefined,
      sort: 'created_at',
      order: 'desc',
    })
      .then(setCards)
      .catch((requestError) => setError(requestError.message));
  }, [cardType, search]);

  return (
    <section className="flex flex-col gap-4 p-6">
      <h1 className="text-2xl font-semibold">{title}</h1>

      <Input
        value={search}
        placeholder="Search by ID or title"
        onChange={(event) => setSearch(event.target.value)}
      />

      {error ? <div className="text-destructive text-sm">{error}</div> : null}

      <div className="grid gap-2">
        {cards.map((card) => (
          <a
            key={card.id}
            href={`/cards/${card.id}`}
            onClick={onNavigate}
            className="grid gap-2 rounded-lg border p-3 hover:bg-muted/50 md:grid-cols-[auto_1fr_auto]"
          >
            <span className="text-muted-foreground text-sm">#{card.id}</span>
            <span className="font-medium">{card.title}</span>
            <Badge variant="outline">{card.type}</Badge>
          </a>
        ))}
        {cards.length === 0 ? (
          <div className="text-muted-foreground rounded-lg border p-6 text-center text-sm">
            {emptyMessage}
          </div>
        ) : null}
      </div>
    </section>
  );
}
