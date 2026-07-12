import * as React from 'react';

import { listCards, type Card } from '@/api/cards';
import { Badge } from '@/components/ui/badge';
import { Input } from '@/components/ui/input';

type TagsFilter = 'all' | 'tag' | 'source';

export function TagsPage({
  onNavigate,
}: {
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const [cards, setCards] = React.useState<Card[]>([]);
  const [search, setSearch] = React.useState('');
  const [filter, setFilter] = React.useState<TagsFilter>('all');
  const [error, setError] = React.useState<string | null>(null);

  React.useEffect(() => {
    const types = filter === 'all' ? ['tag', 'source'] : [filter];

    listCards({
      types,
      search: search || undefined,
      sort: 'created_at',
      order: 'desc',
    })
      .then(setCards)
      .catch((requestError) => setError(requestError.message));
  }, [filter, search]);

  return (
    <section className="flex flex-col gap-4 p-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-2xl font-semibold">Tags</h1>
        <div className="flex rounded-lg bg-muted p-1">
          {(['all', 'tag', 'source'] as const).map((item) => (
            <button
              key={item}
              type="button"
              className={[
                'h-8 rounded-md px-3 text-sm',
                filter === item ? 'bg-background shadow-sm' : 'text-muted-foreground',
              ].join(' ')}
              onClick={() => setFilter(item)}
            >
              {item === 'all' ? 'All' : item}
            </button>
          ))}
        </div>
      </div>

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
            No tags or sources
          </div>
        ) : null}
      </div>
    </section>
  );
}
