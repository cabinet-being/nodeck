import * as React from 'react';

import { deleteDeck, type DeckSummary } from '@/api/decks';
import { Button } from '@/components/ui/button';
import { Dialog } from '@/components/ui/dialog';

/** Confirms deletion of a deck; its cards are kept. */
export function DeleteDeckDialog({
  deck,
  onClose,
  onDeleted,
}: {
  /** The deck to delete; the dialog is open while this is set. */
  deck: Pick<DeckSummary, 'id' | 'title' | 'cardCount'> | null;
  onClose: () => void;
  onDeleted: (deckId: number) => void;
}) {
  const [isDeleting, setIsDeleting] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);

  React.useEffect(() => { setError(null); }, [deck?.id]);

  async function handleDelete() {
    if (!deck) return;
    setIsDeleting(true);
    setError(null);
    try {
      await deleteDeck(deck.id);
      onDeleted(deck.id);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Unable to delete deck.');
    } finally {
      setIsDeleting(false);
    }
  }

  return (
    <Dialog
      open={deck !== null}
      title="Delete deck"
      description={deck ? `Delete “${deck.title}”? Its cards will not be deleted.` : undefined}
      onOpenChange={(open) => { if (!open && !isDeleting) onClose(); }}
    >
      {error ? <p className="text-destructive text-sm" role="alert">{error}</p> : null}
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" disabled={isDeleting} onClick={onClose}>Cancel</Button>
        <Button type="button" variant="destructive" disabled={isDeleting || deck === null} onClick={() => void handleDelete()}>
          {isDeleting ? 'Deleting...' : 'Delete deck'}
        </Button>
      </div>
    </Dialog>
  );
}
