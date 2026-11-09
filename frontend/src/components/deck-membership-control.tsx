import * as React from 'react';

import {
  addCardToDeck,
  getCardDecks,
  listDecks,
  removeCardFromDeck,
  type DeckSummary,
} from '@/api/decks';
import { Button } from '@/components/ui/button';

type MembershipState = {
  cardId: number;
  decks: DeckSummary[];
  memberIds: Set<number>;
};

export function DeckMembershipControl({ cardId }: { cardId: number }) {
  const [state, setState] = React.useState<MembershipState | null>(null);
  const [loadError, setLoadError] = React.useState<string | null>(null);
  const [saveError, setSaveError] = React.useState<string | null>(null);
  const [savingId, setSavingId] = React.useState<number | null>(null);
  const [reload, setReload] = React.useState(0);
  const [opened, setOpened] = React.useState(false);
  const currentCardId = React.useRef(cardId);
  currentCardId.current = cardId;

  React.useEffect(() => {
    if (!opened) return;
    let active = true;
    setState(null);
    setLoadError(null);
    setSaveError(null);
    setSavingId(null);

    Promise.all([listDecks(), getCardDecks(cardId)])
      .then(([decks, memberships]) => {
        if (!active || currentCardId.current !== cardId) return;
        setState({
          cardId,
          decks: decks.filter((deck) => deck.systemKey === null),
          memberIds: new Set(memberships.map((deck) => deck.id)),
        });
      })
      .catch((error) => {
        if (active && currentCardId.current === cardId) {
          setLoadError(error instanceof Error ? error.message : 'Unable to load decks.');
        }
      });

    return () => { active = false; };
  }, [cardId, reload, opened]);

  const visibleState = state?.cardId === cardId ? state : null;

  async function toggle(deckId: number, isMember: boolean) {
    if (savingId !== null || !visibleState) return;
    setSavingId(deckId);
    setSaveError(null);

    try {
      const updated = isMember
        ? await removeCardFromDeck(deckId, cardId)
        : await addCardToDeck(deckId, cardId);
      if (currentCardId.current !== cardId) return;

      setState((current) => {
        if (!current || current.cardId !== cardId) return current;
        const memberIds = new Set(current.memberIds);
        if (isMember) memberIds.delete(deckId);
        else memberIds.add(deckId);
        return {
          ...current,
          memberIds,
          decks: current.decks.map((deck) => deck.id === deckId ? {
            ...deck,
            cardCount: updated.cards.length,
          } : deck),
        };
      });
    } catch (error) {
      if (currentCardId.current === cardId) {
        setSaveError(error instanceof Error ? error.message : 'Unable to update deck.');
      }
    } finally {
      if (currentCardId.current === cardId) setSavingId(null);
    }
  }

  return (
    <details className="rounded-lg border" aria-label="Deck membership"
      onToggle={(event) => setOpened(event.currentTarget.open)}>
      <summary className="cursor-pointer px-4 py-3 text-sm font-medium">Decks</summary>
      <div className="border-t p-4">
        {!visibleState && !loadError ? (
          <p className="text-muted-foreground text-sm" role="status">Loading decks...</p>
        ) : null}
        {!visibleState && loadError ? (
          <div className="space-y-2">
            <p className="text-destructive text-sm" role="alert">{loadError}</p>
            <Button type="button" variant="outline" size="sm" onClick={() => setReload((value) => value + 1)}>
              Retry
            </Button>
          </div>
        ) : null}
        {visibleState?.decks.length === 0 ? (
          <p className="text-muted-foreground text-sm">No decks yet.</p>
        ) : null}
        {visibleState && visibleState.decks.length > 0 ? (
          <ul className="max-h-64 space-y-2 overflow-y-auto">
            {visibleState.decks.map((deck) => {
              const isMember = visibleState.memberIds.has(deck.id);
              return (
                <li key={deck.id} className="flex items-center justify-between gap-2">
                  <span className="min-w-0 truncate text-sm" title={deck.title}>{deck.title}</span>
                  <Button
                    type="button"
                    variant={isMember ? 'outline' : 'secondary'}
                    size="sm"
                    disabled={savingId !== null}
                    aria-label={savingId === deck.id
                      ? `Saving ${deck.title}`
                      : `${isMember ? 'Remove from' : 'Add to'} ${deck.title}`}
                    aria-pressed={isMember}
                    onClick={() => void toggle(deck.id, isMember)}
                  >
                    {savingId === deck.id ? 'Saving...' : isMember ? 'Remove' : 'Add'}
                  </Button>
                </li>
              );
            })}
          </ul>
        ) : null}
        {saveError ? <p className="text-destructive mt-2 text-sm" role="alert">{saveError}</p> : null}
      </div>
    </details>
  );
}
