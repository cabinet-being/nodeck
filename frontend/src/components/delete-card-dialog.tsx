import * as React from 'react';

import { deleteCard, getCard, getCardDeletionImpact, type Card, type CardDeletionImpact } from '@/api/cards';
import { Button } from '@/components/ui/button';
import { Dialog } from '@/components/ui/dialog';
import { Skeleton } from '@/components/ui/skeleton';

/**
 * Confirms deletion of a card, showing how many cards and deck memberships are affected.
 * `onDeleted` receives the ids of every card that was removed.
 */
export function DeleteCardDialog({
  card,
  onClose,
  onDeleted,
}: {
  /** The card to delete; the dialog is open while this is set. */
  card: Pick<Card, 'id' | 'type' | 'title'> | null;
  onClose: () => void;
  onDeleted: (removedIds: number[]) => void;
}) {
  const [details, setDetails] = React.useState<Card | null>(null);
  const [impact, setImpact] = React.useState<CardDeletionImpact | null>(null);
  const [loadError, setLoadError] = React.useState<string | null>(null);
  const [deleteError, setDeleteError] = React.useState<string | null>(null);
  const [isDeleting, setIsDeleting] = React.useState(false);

  const cardId = card?.id ?? null;
  const cardType = card?.type ?? null;
  const isCollection = cardType === 'comic' || cardType === 'set';

  React.useEffect(() => {
    setDetails(null);
    setImpact(null);
    setLoadError(null);
    setDeleteError(null);
    if (cardId === null) return;
    let active = true;
    Promise.all([getCardDeletionImpact(cardId), isCollection ? getCard(cardId) : Promise.resolve(null)])
      .then(([nextImpact, nextDetails]) => {
        if (!active) return;
        setImpact(nextImpact);
        setDetails(nextDetails);
      })
      .catch((cause) => {
        if (active) setLoadError(cause instanceof Error ? cause.message : 'Unable to load deletion impact.');
      });
    return () => { active = false; };
  }, [cardId, isCollection]);

  async function handleDelete(deleteContents: boolean) {
    if (!card) return;
    setIsDeleting(true);
    setDeleteError(null);
    try {
      await deleteCard(card.id, deleteContents);
      const removed = [card.id, ...(deleteContents ? (details?.containedCards ?? []).map((item) => item.id) : [])];
      onDeleted(removed);
    } catch (cause) {
      setDeleteError(cause instanceof Error ? cause.message : 'Unable to delete card.');
    } finally {
      setIsDeleting(false);
    }
  }

  return (
    <Dialog
      open={card !== null}
      title="Delete card"
      description={card ? `Delete ${card.title ? `“${card.title}”` : 'this card'}?` : undefined}
      onOpenChange={(open) => { if (!open && !isDeleting) onClose(); }}
    >
      {loadError ? <p className="text-destructive text-sm" role="alert">{loadError}</p> : null}
      {card && !impact && !loadError ? <Skeleton className="h-10 w-full" /> : null}
      {impact ? (
        <p className="text-muted-foreground text-sm">
          Delete card only: {impact.parentOnly.cardCount} card(s), {impact.parentOnly.deckMembershipCount} deck membership(s).
          {isCollection
            ? ` Delete with contents: ${impact.withContents.cardCount} card(s), ${impact.withContents.deckMembershipCount} deck membership(s).`
            : ''}
        </p>
      ) : null}
      {deleteError ? <p className="text-destructive text-sm" role="alert">{deleteError}</p> : null}
      <div className="flex flex-wrap justify-end gap-2">
        <Button type="button" variant="outline" disabled={isDeleting} onClick={onClose}>Cancel</Button>
        <Button type="button" variant="destructive" disabled={isDeleting || !card || !impact}
          onClick={() => void handleDelete(false)}>
          {isDeleting ? 'Deleting...' : isCollection ? 'Delete collection only' : 'Delete'}
        </Button>
        {card && isCollection ? (
          <Button type="button" variant="destructive" disabled={isDeleting || !impact || !details}
            onClick={() => void handleDelete(true)}>
            Delete with {impact ? impact.withContents.cardCount - 1 : 0} contents
          </Button>
        ) : null}
      </div>
    </Dialog>
  );
}
