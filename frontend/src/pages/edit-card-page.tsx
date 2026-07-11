import * as React from 'react';

import { getCard, listCards, updateCard, type Card } from '@/api/cards';
import {
  CardForm,
  keyValueRowsFromRecord,
  relationRowsFromCard,
  toRecord,
  toRelationInputs,
  type CardFormValue,
} from '@/components/card-form';
import { Badge } from '@/components/ui/badge';
import { Card as UiCard, CardContent, CardHeader, CardTitle } from '@/components/ui/card';

export function EditCardPage({
  cardId,
  onSaved,
}: {
  cardId: number;
  onSaved: (path: string) => void;
}) {
  const [value, setValue] = React.useState<CardFormValue | null>(null);
  const [card, setCard] = React.useState<Card | null>(null);
  const [availableCards, setAvailableCards] = React.useState<Card[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [isLoading, setIsLoading] = React.useState(true);
  const [isSubmitting, setIsSubmitting] = React.useState(false);

  React.useEffect(() => {
    let isCurrent = true;
    setIsLoading(true);
    setError(null);

    Promise.all([
      getCard(cardId),
      listCards({ sort: 'created_at', order: 'desc' }),
    ])
      .then(([loadedCard, cards]) => {
        if (!isCurrent) {
          return;
        }

        setCard(loadedCard);
        setAvailableCards(cards.filter((item) => item.id !== loadedCard.id));
        setValue({
          type: loadedCard.type,
          title: loadedCard.title ?? '',
          properties: keyValueRowsFromRecord(loadedCard.properties),
          relations: relationRowsFromCard(loadedCard),
        });
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
  }, [cardId]);

  async function handleSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();

    if (!value) {
      return;
    }

    setError(null);
    setIsSubmitting(true);

    try {
      const updated = await updateCard(cardId, {
        title: value.title.trim() || undefined,
        image: value.image,
        properties: toRecord(value.properties),
        relations: toRelationInputs(value.relations),
      });

      onSaved(`/cards/${updated.id}`);
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : 'Unable to update card.');
    } finally {
      setIsSubmitting(false);
    }
  }

  if (isLoading) {
    return <section className="p-6 text-sm text-muted-foreground">Loading...</section>;
  }

  if (!value || !card) {
    return <section className="p-6 text-sm text-destructive">{error ?? 'Card not found.'}</section>;
  }

  return (
    <section className="mx-auto flex w-full max-w-4xl flex-col gap-4 p-6">
      <div>
        <h1 className="text-2xl font-semibold">Edit Card #{card.id}</h1>
      </div>

      <CardForm
        value={value}
        availableCards={availableCards}
        isSubmitting={isSubmitting}
        submitLabel="Save changes"
        submittingLabel="Saving..."
        error={error}
        typeLocked
        onValueChange={setValue}
        onSubmit={handleSubmit}
        incomingRelations={<IncomingRelations card={card} />}
      />
    </section>
  );
}

function IncomingRelations({ card }: { card: Card }) {
  const incomingRelations = card.incomingRelations ?? [];

  return (
    <UiCard>
      <CardHeader>
        <CardTitle>Incoming relations</CardTitle>
      </CardHeader>
      <CardContent>
        {incomingRelations.length > 0 ? (
          <div className="grid gap-2 text-sm">
            {incomingRelations.map((relation) => (
              <div
                key={relation.id}
                className="flex flex-wrap items-center gap-2 rounded-lg border px-3 py-2"
              >
                <span className="font-medium">#{relation.fromCardId}</span>
                <Badge variant="outline">{relation.relationType}</Badge>
                <span className="text-muted-foreground">to #{relation.toCardId}</span>
              </div>
            ))}
          </div>
        ) : (
          <div className="text-muted-foreground text-sm">No incoming relations</div>
        )}
      </CardContent>
    </UiCard>
  );
}
