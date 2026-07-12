import * as React from 'react';

import { resolveApiUrl } from '@/api/cards';
import { getDeck, type DeckCard, type DeckDetails } from '@/api/decks';
import { Badge } from '@/components/ui/badge';
import { buttonVariants } from '@/components/ui/button';
import { Card as UiCard, CardContent, CardHeader, CardTitle } from '@/components/ui/card';

export function DeckDetailsPage({
  deckId,
  onNavigate,
}: {
  deckId: number;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const [deck, setDeck] = React.useState<DeckDetails | null>(null);
  const [error, setError] = React.useState<string | null>(null);

  React.useEffect(() => {
    setDeck(null);
    setError(null);

    getDeck(deckId)
      .then(setDeck)
      .catch((requestError) => setError(requestError.message));
  }, [deckId]);

  if (error) {
    return <div className="text-destructive p-6 text-sm">{error}</div>;
  }

  if (!deck) {
    return <div className="text-muted-foreground p-6 text-sm">Loading...</div>;
  }

  return (
    <section className="grid gap-4 p-6 2xl:grid-cols-[minmax(0,1fr)_24rem]">
      <div className="grid min-w-0 gap-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h1 className="text-2xl font-semibold">{deck.title}</h1>
            <div className="text-muted-foreground text-sm">Deck #{deck.id}</div>
          </div>
          <a
            href={`/decks/${deck.id}/edit`}
            onClick={onNavigate}
            className={buttonVariants({ variant: 'outline' })}
          >
            Edit deck
          </a>
        </div>

        <div className="grid gap-3">
          {deck.cards.map((card) => (
            <DeckCardItem key={card.id} card={card} onNavigate={onNavigate} />
          ))}
          {deck.cards.length === 0 ? (
            <div className="text-muted-foreground rounded-lg border p-6 text-center text-sm">
              No cards
            </div>
          ) : null}
        </div>
      </div>

      <aside className="grid content-start gap-4">
        <JsonPanel title="Properties" value={deck.properties} />
        <JsonPanel title="Metadata" value={deck.metadata} />
      </aside>
    </section>
  );
}

function DeckCardItem({
  card,
  onNavigate,
}: {
  card: DeckCard;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  return (
    <a
      href={`/cards/${card.id}`}
      onClick={onNavigate}
      className="grid gap-3 rounded-lg border p-3 hover:bg-muted/50 md:grid-cols-[auto_5rem_1fr]"
    >
      <div className="text-muted-foreground w-7 text-sm">{card.position + 1}</div>
      {card.previewUrl ? (
        <img
          src={resolveApiUrl(card.previewUrl)}
          alt=""
          className="bg-muted size-20 rounded border object-cover"
          loading="lazy"
        />
      ) : (
        <div className="bg-muted text-muted-foreground grid size-20 place-items-center rounded border text-xs uppercase">
          {card.type}
        </div>
      )}
      <div className="min-w-0">
        <div className="mb-1 flex flex-wrap items-center gap-2">
          <span className="text-muted-foreground text-sm">#{card.id}</span>
          <Badge variant="outline">{card.type}</Badge>
        </div>
        <div className="truncate font-medium">{card.title ?? card.type}</div>
        {card.membershipProperties ? (
          <pre className="bg-muted/50 mt-2 max-h-28 overflow-auto rounded p-2 text-xs">
            {JSON.stringify(card.membershipProperties, null, 2)}
          </pre>
        ) : null}
      </div>
    </a>
  );
}

function JsonPanel({
  title,
  value,
}: {
  title: string;
  value: Record<string, unknown> | null;
}) {
  return (
    <UiCard>
      <CardHeader>
        <CardTitle>{title}</CardTitle>
      </CardHeader>
      <CardContent>
        <pre className="bg-muted/50 max-h-72 overflow-auto rounded-lg p-3 text-xs">
          {JSON.stringify(value ?? {}, null, 2)}
        </pre>
      </CardContent>
    </UiCard>
  );
}
