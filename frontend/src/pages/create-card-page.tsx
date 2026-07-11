import * as React from 'react';

import { createCard, listCards, type Card } from '@/api/cards';
import {
  CardForm,
  toRecord,
  toRelationInputs,
  type CardFormValue,
} from '@/components/card-form';

const initialValue: CardFormValue = {
  type: 'media',
  title: '',
  properties: [],
  relations: [],
};

export function CreateCardPage({
  onCreated,
}: {
  onCreated: (path: string) => void;
}) {
  const [value, setValue] = React.useState<CardFormValue>(initialValue);
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
      const created = await createCard({
        type: value.type,
        title: value.title.trim() || undefined,
        image: value.image,
        properties: toRecord(value.properties),
        relations: toRelationInputs(value.relations),
      });

      onCreated(`/cards/${created.id}`);
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : 'Unable to create card.');
    } finally {
      setIsSubmitting(false);
    }
  }

  return (
    <section className="mx-auto flex w-full max-w-4xl flex-col gap-4 p-6">
      <div>
        <h1 className="text-2xl font-semibold">Create Card</h1>
      </div>

      <CardForm
        value={value}
        availableCards={availableCards}
        isSubmitting={isSubmitting}
        submitLabel="Create card"
        submittingLabel="Creating..."
        error={error}
        onValueChange={setValue}
        onSubmit={handleSubmit}
      />
    </section>
  );
}
