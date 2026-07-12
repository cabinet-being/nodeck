import * as React from 'react';

import { listCards, type Card } from '@/api/cards';
import { createDeck } from '@/api/decks';
import {
  DeckForm,
  toDeckCardInputs,
  toRecord,
  type DeckFormValue,
} from '@/components/deck-form';

const initialValue: DeckFormValue = {
  title: '',
  properties: [],
  cards: [],
};

export function CreateDeckPage({
  onCreated,
  onCancel,
}: {
  onCreated: (path: string) => void;
  onCancel: () => void;
}) {
  const [value, setValue] = React.useState<DeckFormValue>(initialValue);
  const [availableCards, setAvailableCards] = React.useState<Card[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [isSubmitting, setIsSubmitting] = React.useState(false);

  React.useEffect(() => {
    listCards({ sort: 'created_at', order: 'desc' })
      .then(setAvailableCards)
      .catch((requestError) => setError(requestError.message));
  }, []);

  async function handleSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError(null);
    setIsSubmitting(true);

    try {
      if (!value.title.trim()) {
        throw new Error('Deck title is required.');
      }

      const created = await createDeck({
        title: value.title.trim(),
        properties: toRecord(value.properties),
        cards: toDeckCardInputs(value.cards),
      });

      onCreated(`/decks/${created.id}`);
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : 'Unable to create deck.');
    } finally {
      setIsSubmitting(false);
    }
  }

  return (
    <section className="mx-auto flex w-full max-w-5xl flex-col gap-4 p-6">
      <h1 className="text-2xl font-semibold">Create Deck</h1>
      <DeckForm
        value={value}
        availableCards={availableCards}
        isSubmitting={isSubmitting}
        submitLabel="Create deck"
        submittingLabel="Creating..."
        error={error}
        onValueChange={setValue}
        onSubmit={handleSubmit}
        onCancel={onCancel}
      />
    </section>
  );
}
