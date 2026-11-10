import * as React from 'react';
import { LoaderCircle, Pencil, X } from 'lucide-react';

import { type Card } from '@/api/cards';
import { getDeck, removeCardFromDeck, type DeckDetails } from '@/api/decks';
import { CardBrowser } from '@/components/card-browser';
import { Button, buttonVariants } from '@/components/ui/button';
import { Card as UiCard, CardContent, CardHeader, CardTitle } from '@/components/ui/card';

export function DeckDetailsPage({
  deckId,
  currentSearch,
  onNavigate,
  onNavigateTo,
}: {
  deckId: number;
  currentSearch: string;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onNavigateTo: (path: string) => void;
}) {
  const [result, setResult] = React.useState<{
    deckId: number;
    deck: DeckDetails | null;
    error: string | null;
  } | null>(null);
  const [reloadKey, setReloadKey] = React.useState(0);
  const [removingId, setRemovingId] = React.useState<number | null>(null);
  const [removeError, setRemoveError] = React.useState<string | null>(null);
  const [headerRetry, setHeaderRetry] = React.useState(0);
  const pageCount = React.useRef(0);
  const currentDeckId = React.useRef(deckId);
  currentDeckId.current = deckId;
  const pageParam = Number(new URLSearchParams(currentSearch).get('page'));
  const page = Number.isInteger(pageParam) && pageParam > 0 ? pageParam : 1;

  React.useEffect(() => {
    let active = true;
    setRemovingId(null);
    setRemoveError(null);

    getDeck(deckId, { includeCards: false })
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

    return () => { active = false; };
  }, [deckId, headerRetry]);

  if (result?.deckId === deckId && result.error) {
    return (
      <div className="flex flex-wrap items-center gap-3 p-6" role="alert">
        <p className="text-destructive text-sm">{result.error}</p>
        <Button type="button" size="sm" variant="outline" onClick={() => {
          setResult(null);
          setHeaderRetry((key) => key + 1);
        }}>Retry</Button>
      </div>
    );
  }

  if (result?.deckId !== deckId || !result.deck) {
    return <div className="text-muted-foreground p-6 text-sm">Loading...</div>;
  }

  const deck = result.deck;
  const canRemove = deck.systemKey !== 'favorites';

  async function removeCard(cardId: number) {
    if (removingId !== null) return;
    setRemovingId(cardId);
    setRemoveError(null);

    try {
      await removeCardFromDeck(deckId, cardId);
      if (currentDeckId.current !== deckId) return;
      const updated = await getDeck(deckId, { includeCards: false }).catch(() => null);
      if (currentDeckId.current !== deckId) return;
      if (updated) setResult({ deckId, deck: updated, error: null });
      if (pageCount.current <= 1 && page > 1) {
        const params = new URLSearchParams(currentSearch);
        if (page - 1 <= 1) params.delete('page');
        else params.set('page', String(page - 1));
        onNavigateTo(`/decks/${deckId}${params.toString() ? `?${params.toString()}` : ''}`);
      } else {
        setReloadKey((key) => key + 1);
      }
    } catch (error) {
      if (currentDeckId.current === deckId) {
        setRemoveError(error instanceof Error ? error.message : 'Unable to remove card.');
      }
    } finally {
      if (currentDeckId.current === deckId) setRemovingId(null);
    }
  }

  return (
    <section className="grid gap-6 px-4 py-6 sm:px-6 sm:py-8">
      <div className="grid min-w-0 gap-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="min-w-0">
            <h1 className="text-2xl font-semibold tracking-tight">{deck.title}</h1>
            <p className="text-muted-foreground text-sm">{deck.cardCount} card(s)</p>
          </div>
          {deck.systemKey === 'favorites' ? null : (
            <a
              href={`/decks/${deck.id}/edit`}
              onClick={onNavigate}
              className={buttonVariants({ variant: 'outline', size: 'icon' })}
              aria-label={`Edit ${deck.title}`}
              title="Edit deck"
            >
              <Pencil className="size-4" />
            </a>
          )}
        </div>

        {removeError ? <p className="text-destructive text-sm" role="alert">{removeError}</p> : null}
        <CardBrowser
          scope={{ kind: 'deck', deckId }}
          basePath={`/decks/${deckId}`}
          currentSearch={currentSearch}
          onNavigate={onNavigate}
          onNavigateTo={onNavigateTo}
          viewStorageKey="nodeck.deckView"
          reloadKey={reloadKey}
          emptyMessage="This deck has no cards yet."
          onPageLoaded={({ count }) => { pageCount.current = count; }}
          renderCaption={(card) => (
            <DeckCardCaption
              card={card}
              onNavigate={onNavigate}
              onRemove={canRemove ? removeCard : undefined}
              isRemoving={removingId === card.id}
              isSaving={removingId !== null}
            />
          )}
        />
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
  card: Card;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onRemove?: (cardId: number) => void;
  isRemoving: boolean;
  isSaving: boolean;
}) {
  const label = card.title ?? card.type;
  const membership = card.membershipProperties;
  return (
    <div className="bg-background/95 flex min-w-0 items-center gap-2 p-2">
      <a href={`/cards/${card.id}`} onClick={onNavigate} className="block min-w-0 flex-1 hover:underline" aria-label={`Open ${label}`}>
        <div className="text-muted-foreground truncate text-xs">
          {card.deckPosition != null ? `#${card.deckPosition + 1} · ` : ''}{card.type} · ID {card.id}
        </div>
        <div className="truncate text-sm font-medium" title={label}>{label}</div>
      </a>
      {membership && Object.keys(membership).length > 0 ? (
        <details className="text-muted-foreground text-xs">
          <summary className="cursor-pointer" aria-label={`Membership properties for ${label}`}>Details</summary>
          <dl className="mt-2 space-y-1 break-words">
            {Object.entries(membership).map(([key, value]) => (
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
          variant="ghost"
          size="icon-xs"
          disabled={isSaving}
          aria-label={`Remove ${label} from deck`}
          title="Remove from deck"
          onClick={() => onRemove(card.id)}
        >
          {isRemoving ? <LoaderCircle className="size-4 animate-spin" /> : <X className="size-4" />}
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
