import * as React from 'react';

import { listCards, type Card } from '@/api/cards';
import { getDeck, updateDeck, type DeckDetails } from '@/api/decks';
import {
  DeckForm,
  keyValueRowsFromRecord,
  toDeckCardInputs,
  toRecord,
  type DeckFormValue,
} from '@/components/deck-form';

export function EditDeckPage({
  deckId,
  onSaved,
  onCancel,
}: {
  deckId: number;
  onSaved: (path: string) => void;
  onCancel: () => void;
}) {
  const [value, setValue] = React.useState<DeckFormValue | null>(null);
  const [availableCards, setAvailableCards] = React.useState<Card[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [isLoading, setIsLoading] = React.useState(true);
  const [isSubmitting, setIsSubmitting] = React.useState(false);

  React.useEffect(() => {
    let isCurrent = true;
    setIsLoading(true);
    setError(null);

    Promise.all([
      getDeck(deckId),
      listCards({ sort: 'created_at', order: 'desc' }),
    ])
      .then(([deck, cards]) => {
        if (!isCurrent) {
          return;
        }

        setAvailableCards(cards);
        setValue(createFormValue(deck, cards));
      })
      .catch((requestError) => {
        if (isCurrent) {
          setError(requestError.message);
        }
      })
      .finally(() => {
        if (isCurrent) {
          setIsLoading(false);
        }
      });

    return () => {
      isCurrent = false;
    };
  }, [deckId]);

  async function handleSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();

    if (!value) {
      return;
    }

    setError(null);
    setIsSubmitting(true);

    try {
      if (!value.title.trim()) {
        throw new Error('Deck title is required.');
      }

      const updated = await updateDeck(deckId, {
        title: value.title.trim(),
        properties: toRecord(value.properties),
        cards: toDeckCardInputs(value.cards),
      });

      onSaved(`/decks/${updated.id}`);
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : 'Unable to update deck.');
    } finally {
      setIsSubmitting(false);
    }
  }

  if (isLoading) {
    return <section className="p-6 text-sm text-muted-foreground">Loading...</section>;
  }

  if (!value) {
    return <section className="p-6 text-sm text-destructive">{error ?? 'Deck not found.'}</section>;
  }

  return (
    <section className="mx-auto flex w-full max-w-5xl flex-col gap-4 p-6">
      <h1 className="text-2xl font-semibold">Edit Deck #{deckId}</h1>
      <DeckForm
        value={value}
        availableCards={availableCards}
        isSubmitting={isSubmitting}
        submitLabel="Save changes"
        submittingLabel="Saving..."
        error={error}
        onValueChange={setValue}
        onSubmit={handleSubmit}
        onCancel={onCancel}
      />
    </section>
  );
}

function createFormValue(deck: DeckDetails, cards: Card[]): DeckFormValue {
  const cardById = new Map(cards.map((card) => [card.id, card]));

  return {
    title: deck.title,
    properties: keyValueRowsFromRecord(deck.properties),
    cards: deck.cards.map((deckCard) => ({
      card: cardById.get(deckCard.id) ?? {
        id: deckCard.id,
        type: deckCard.type,
        title: deckCard.title,
        previewUrl: deckCard.previewUrl,
        contentUrl: deckCard.contentUrl,
        properties: deckCard.properties,
        metadata: deckCard.metadata,
      },
      properties: keyValueRowsFromRecord(deckCard.membershipProperties),
    })),
  };
}
