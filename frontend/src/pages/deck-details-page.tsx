import * as React from 'react';

import { getDeck, removeCardFromDeck, type DeckCard, type DeckDetails } from '@/api/decks';
import { Badge } from '@/components/ui/badge';
import { Button, buttonVariants } from '@/components/ui/button';
import { Card as UiCard, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import {
  useVisualCardView,
  VisualCardViewToggle,
  VisualCardWall,
} from '@/components/visual-card-wall';

export function DeckDetailsPage({
  deckId,
  onNavigate,
}: {
  deckId: number;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const [result, setResult] = React.useState<{
    deckId: number;
    deck: DeckDetails | null;
    error: string | null;
  } | null>(null);
  const [view, setView] = useVisualCardView('nodeck.deckView');
  const [removingId, setRemovingId] = React.useState<number | null>(null);
  const [removeError, setRemoveError] = React.useState<string | null>(null);
  const currentDeckId = React.useRef(deckId);
  const requestGeneration = React.useRef(0);
  currentDeckId.current = deckId;

  React.useEffect(() => {
    let active = true;
    requestGeneration.current += 1;
    setResult(null);
    setRemovingId(null);
    setRemoveError(null);

    getDeck(deckId)
      .then((deck) => {
        if (active) setResult({ deckId, deck, error: null });
      })
      .catch((requestError) => {
        if (active) setResult({
          deckId,
          deck: null,
          error: requestError instanceof Error ? requestError.message : 'Unable to load deck.',
        });
      });

    return () => {
      active = false;
      requestGeneration.current += 1;
    };
  }, [deckId]);

  if (result?.deckId === deckId && result.error) {
    return <div className="text-destructive p-6 text-sm">{result.error}</div>;
  }

  if (result?.deckId !== deckId || !result.deck) {
    return <div className="text-muted-foreground p-6 text-sm">Loading...</div>;
  }

  const deck = result.deck;
  const cards = [...deck.cards].sort((left, right) => left.position - right.position);

  async function removeCard(cardId: number) {
    if (removingId !== null) return;
    const generation = requestGeneration.current;
    setRemovingId(cardId);
    setRemoveError(null);

    try {
      const updated = await removeCardFromDeck(deckId, cardId);
      if (currentDeckId.current === deckId && requestGeneration.current === generation) {
        setResult({ deckId, deck: updated, error: null });
      }
    } catch (error) {
      if (currentDeckId.current === deckId && requestGeneration.current === generation) {
        setRemoveError(error instanceof Error ? error.message : 'Unable to remove card.');
      }
    } finally {
      if (currentDeckId.current === deckId && requestGeneration.current === generation) setRemovingId(null);
    }
  }

  return (
    <section className="grid gap-4 p-6 2xl:grid-cols-[minmax(0,1fr)_24rem]">
      <div className="grid min-w-0 gap-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h1 className="text-2xl font-semibold">{deck.title}</h1>
            <div className="text-muted-foreground text-sm">Deck #{deck.id}</div>
          </div>
          {deck.systemKey === 'favorites' ? null : (
            <a
              href={`/decks/${deck.id}/edit`}
              onClick={onNavigate}
              className={buttonVariants({ variant: 'outline' })}
            >
              Edit deck
            </a>
          )}
        </div>

        <div className="flex justify-end">
          <VisualCardViewToggle view={view} onChange={setView} />
        </div>
        {removeError ? <p className="text-destructive text-sm" role="alert">{removeError}</p> : null}
        {cards.length > 0 ? (
          <VisualCardWall
            cards={cards}
            view={view}
            onNavigate={onNavigate}
            renderCaption={(index) => (
              <DeckCardCaption
                card={cards[index]}
                onNavigate={onNavigate}
                onRemove={deck.systemKey === 'favorites' ? undefined : removeCard}
                isRemoving={removingId === cards[index].id}
                isSaving={removingId !== null}
              />
            )}
          />
        ) : (
          <div className="text-muted-foreground rounded-lg border p-6 text-center text-sm">
            No cards
          </div>
        )}
      </div>

      <aside className="grid content-start gap-4">
        <details className="rounded-lg border">
          <summary className="cursor-pointer px-4 py-3 text-sm">Technical details</summary>
          <div className="grid gap-3 border-t p-3">
            <JsonPanel title="Properties" value={deck.properties} />
            <JsonPanel title="Metadata" value={deck.metadata} />
          </div>
        </details>
      </aside>
    </section>
  );
}

function DeckCardCaption({
  card,
  onNavigate,
  onRemove,
  isRemoving,
  isSaving,
}: {
  card: DeckCard;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onRemove?: (cardId: number) => void;
  isRemoving: boolean;
  isSaving: boolean;
}) {
  return (
    <div className="min-w-0 space-y-2 bg-background/95 p-3">
      <a href={`/cards/${card.id}`} onClick={onNavigate} className="block min-w-0 hover:underline">
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-muted-foreground text-xs">{card.position + 1}.</span>
          <Badge variant="outline">{card.type}</Badge>
          <span className="text-muted-foreground text-xs">#{card.id}</span>
        </div>
        <div className="mt-1 truncate text-sm font-medium" title={card.title ?? card.type}>
          {card.title ?? card.type}
        </div>
      </a>
      {card.membershipProperties && Object.keys(card.membershipProperties).length > 0 ? (
        <details className="text-muted-foreground text-xs">
          <summary className="cursor-pointer">Membership properties</summary>
          <dl className="mt-2 space-y-1 break-words">
            {Object.entries(card.membershipProperties).map(([key, value]) => (
              <div key={key}>
                <dt className="inline font-medium">{key}: </dt>
                <dd className="inline">{typeof value === 'string' ? value : JSON.stringify(value)}</dd>
              </div>
            ))}
          </dl>
        </details>
      ) : null}
      {onRemove ? (
        <Button
          type="button"
          variant="outline"
          size="xs"
          disabled={isSaving}
          aria-label={`Remove ${card.title ?? card.type} from deck`}
          onClick={() => onRemove(card.id)}
        >
          {isRemoving ? 'Removing...' : 'Remove from deck'}
        </Button>
      ) : null}
    </div>
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
