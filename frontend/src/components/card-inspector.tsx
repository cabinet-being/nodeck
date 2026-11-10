import * as React from 'react';
import { Layers, Link2 } from 'lucide-react';

import {
  createCardRelation, deleteCardRelation, listCards,
  type Card, type CardRelationEntry, type CardRelations,
} from '@/api/cards';
import { getCardDecks, removeCardFromDeck, type DeckSummary } from '@/api/decks';
import { ActionBar } from '@/components/action-bar';
import { CardCatalogPanel } from '@/components/card-catalog-panel';
import { type ConnectionGroup } from '@/components/connection-strip';
import { DeleteCardDialog } from '@/components/delete-card-dialog';
import { NodeChip } from '@/components/node-chip';
import { RelationDashboard } from '@/components/relation-dashboard';
import { Button } from '@/components/ui/button';
import { Dialog } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Sheet } from '@/components/ui/sheet';

/**
 * Right-hand column of the card page: title, actions and the card's connections
 * (tags, suggestions, sources, decks, related cards, file facts).
 */
export function CardInspector({
  card,
  relations,
  onRelationsChange,
  onNavigate,
  onNavigateTo,
  onFavoriteChange,
  originalUrl,
}: {
  card: Card;
  relations: CardRelations;
  onRelationsChange: (relations: CardRelations) => void;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onNavigateTo?: (path: string) => void;
  onFavoriteChange: (isFavorite: boolean) => void;
  originalUrl?: string | null;
}) {
  const [detailsOpen, setDetailsOpen] = React.useState(false);
  const [deleting, setDeleting] = React.useState(false);
  const [deckOpen, setDeckOpen] = React.useState(false);
  const [memberDecks, setMemberDecks] = React.useState<DeckSummary[]>([]);
  const [deckRevision, setDeckRevision] = React.useState(0);
  const [pickerOpen, setPickerOpen] = React.useState(false);
  const [pickerSearch, setPickerSearch] = React.useState('');
  const [pickerResults, setPickerResults] = React.useState<Card[]>([]);
  const [pickerError, setPickerError] = React.useState<string | null>(null);
  const [mutationError, setMutationError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);

  React.useEffect(() => {
    let active = true;
    getCardDecks(card.id)
      .then((decks) => { if (active) setMemberDecks(decks.filter((deck) => deck.systemKey === null)); })
      .catch(() => { if (active) setMemberDecks([]); });
    return () => { active = false; };
  }, [card.id, deckRevision]);

  const relatedEntries = dedupe([
    ...relations.outgoingRelations.filter((relation) => relation.relationType === 'related_to'),
    ...relations.incomingRelations.filter((relation) => relation.relationType === 'related_to'),
  ]);

  React.useEffect(() => {
    if (!pickerOpen) return;
    let active = true;
    setPickerError(null);
    listCards({ search: pickerSearch || undefined, sort: 'created_at', order: 'desc' })
      .then((results) => { if (active) setPickerResults(results.filter((result) => result.id !== card.id)); })
      .catch((cause) => { if (active) setPickerError(cause instanceof Error ? cause.message : 'Unable to search cards.'); });
    return () => { active = false; };
  }, [pickerOpen, pickerSearch, card.id]);

  async function addRelated(selected: Card) {
    setBusy(true);
    setPickerError(null);
    try {
      const created = await createCardRelation(card.id, { toCardId: selected.id, relationType: 'related_to', properties: null });
      onRelationsChange({ ...relations, outgoingRelations: [...relations.outgoingRelations, created] });
      setPickerOpen(false);
      setPickerSearch('');
      setPickerResults([]);
    } catch (cause) {
      setPickerError(cause instanceof Error ? cause.message : 'Unable to add related card.');
    } finally {
      setBusy(false);
    }
  }

  async function removeRelated(relationId: number) {
    if (busy) return;
    setBusy(true);
    setMutationError(null);
    try {
      await deleteCardRelation(relationId);
      onRelationsChange({
        outgoingRelations: relations.outgoingRelations.filter((relation) => relation.id !== relationId),
        incomingRelations: relations.incomingRelations.filter((relation) => relation.id !== relationId),
      });
    } catch (cause) {
      setMutationError(cause instanceof Error ? cause.message : 'Unable to remove related card.');
    } finally {
      setBusy(false);
    }
  }

  async function removeFromDeck(deckId: number) {
    if (busy) return;
    setBusy(true);
    setMutationError(null);
    try {
      await removeCardFromDeck(deckId, card.id);
      setDeckRevision((value) => value + 1);
    } catch (cause) {
      setMutationError(cause instanceof Error ? cause.message : 'Unable to update deck.');
    } finally {
      setBusy(false);
    }
  }

  const extraGroups: ConnectionGroup[] = [
    {
      key: 'decks', label: 'Decks', icon: Layers, empty: 'Not in any deck',
      onAdd: () => setDeckOpen(true), addLabel: 'Add to deck',
      nodes: memberDecks.map((deck) => (
        <NodeChip key={deck.id} kind="deck" label={deck.title} href={`/decks/${deck.id}`} onNavigate={onNavigate}
          onRemove={() => void removeFromDeck(deck.id)} removeLabel={`Remove from ${deck.title}`} />
      )),
    },
    {
      key: 'related', label: 'Related cards', icon: Link2, empty: 'No related cards',
      onAdd: () => { setPickerOpen(true); setPickerSearch(''); setPickerResults([]); }, addLabel: 'Add related card',
      nodes: relatedEntries.map((relation) => (
        <NodeChip key={relation.id} kind="card" label={relation.relatedCard.title || 'Untitled'}
          href={`/cards/${relation.relatedCard.id}`} onNavigate={onNavigate}
          onRemove={() => void removeRelated(relation.id)} removeLabel={`Remove related card ${relation.relatedCard.title || ''}`.trim()} />
      )),
    },
  ];

  const alreadyRelated = new Set(relatedEntries.map((relation) => relation.relatedCard.id));

  return (
    <aside className="grid content-start gap-5" aria-label="Card inspector">
      <div className="grid gap-2">
        <h1 className="line-clamp-3 text-xl font-semibold tracking-tight">{card.title || 'Untitled'}</h1>
        <ActionBar
          cardId={card.id}
          isFavorite={card.isFavorite}
          onFavoriteChange={onFavoriteChange}
          editHref={`/cards/${card.id}/edit`}
          onNavigate={onNavigate}
          onDelete={() => setDeleting(true)}
          originalUrl={originalUrl}
          onShowDetails={() => setDetailsOpen(true)}
          deckOpen={deckOpen}
          onDeckOpenChange={setDeckOpen}
          onDecksChange={() => setDeckRevision((value) => value + 1)}
        />
      </div>

      <CardCatalogPanel cardId={card.id} metadata={card.metadata} extraGroups={extraGroups} onNavigate={onNavigate} />
      {mutationError ? <p className="text-destructive text-xs" role="alert">{mutationError}</p> : null}

      <Dialog open={pickerOpen} title="Add related card" onOpenChange={setPickerOpen}>
        <div className="grid gap-3">
          <Input autoFocus type="search" aria-label="Search cards" placeholder="Search by title"
            value={pickerSearch} onChange={(event) => setPickerSearch(event.target.value)} />
          {pickerError ? <p className="text-destructive text-xs" role="alert">{pickerError}</p> : null}
          <div className="grid max-h-64 gap-1 overflow-auto">
            {pickerResults.filter((result) => !alreadyRelated.has(result.id)).map((result) => (
              <Button key={result.id} type="button" variant="ghost" className="justify-start" disabled={busy}
                onClick={() => void addRelated(result)}>
                {result.title || 'Untitled'}
              </Button>
            ))}
          </div>
        </div>
      </Dialog>

      <Sheet open={detailsOpen} onOpenChange={setDetailsOpen} side="right" title="Details" className="sm:max-w-3xl">
        <CardDetailsContent card={card} relations={relations} onRelationsChange={onRelationsChange} onNavigate={onNavigate} />
      </Sheet>

      <DeleteCardDialog
        card={deleting ? card : null}
        onClose={() => setDeleting(false)}
        onDeleted={() => {
          setDeleting(false);
          if (onNavigateTo) onNavigateTo('/gallery');
        }}
      />
    </aside>
  );
}

/** Relation dashboard (with graph and legend) plus raw properties and metadata. */
export function CardDetailsContent({
  card,
  relations,
  onRelationsChange,
  onNavigate,
  withJson = true,
}: {
  card: Card;
  relations: CardRelations;
  onRelationsChange: (relations: CardRelations) => void;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  withJson?: boolean;
}) {
  return (
    <div className="grid gap-4">
      <RelationDashboard card={card} relations={relations} onRelationsChange={onRelationsChange} onNavigate={onNavigate} />
      {withJson ? (
        <>
          <JsonPanel title="Properties" value={card.properties} />
          <JsonPanel title="Metadata" value={card.metadata} />
        </>
      ) : null}
    </div>
  );
}

function JsonPanel({ title, value }: { title: string; value: Record<string, unknown> | null }) {
  return (
    <section className="grid gap-2 rounded-xl border p-4">
      <h2 className="text-base font-semibold">{title}</h2>
      <pre className="bg-muted/50 max-h-72 overflow-auto rounded-lg p-3 text-xs">{JSON.stringify(value ?? {}, null, 2)}</pre>
    </section>
  );
}

function dedupe(relations: CardRelationEntry[]) {
  const seen = new Set<number>();
  return relations.filter((relation) => {
    if (seen.has(relation.relatedCard.id)) return false;
    seen.add(relation.relatedCard.id);
    return true;
  });
}
