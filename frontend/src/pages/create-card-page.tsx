import * as React from 'react';
import { Plus, Trash2 } from 'lucide-react';

import {
  cardTypes,
  createCard,
  listCards,
  relationTypes,
  type Card,
  type CreateRelationInput,
} from '@/api/cards';
import { Button } from '@/components/ui/button';
import { Card as UiCard, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';

type KeyValueRow = {
  key: string;
  value: string;
};

type RelationRow = {
  toCardId: string;
  relationType: string;
  properties: KeyValueRow[];
};

export function CreateCardPage({
  onCreated,
}: {
  onCreated: (path: string) => void;
}) {
  const [type, setType] = React.useState('media');
  const [title, setTitle] = React.useState('');
  const [image, setImage] = React.useState<File | undefined>();
  const [properties, setProperties] = React.useState<KeyValueRow[]>([]);
  const [relations, setRelations] = React.useState<RelationRow[]>([]);
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
        type,
        title: title.trim() || undefined,
        image,
        properties: toRecord(properties),
        relations: relations
          .filter((relation) => relation.toCardId)
          .map<CreateRelationInput>((relation) => ({
            toCardId: Number(relation.toCardId),
            relationType: relation.relationType,
            properties: toRecord(relation.properties),
          })),
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

      <form onSubmit={handleSubmit} className="grid gap-4">
        <UiCard>
          <CardHeader>
            <CardTitle>Card</CardTitle>
          </CardHeader>
          <CardContent className="grid gap-4">
            <label className="grid gap-2 text-sm font-medium">
              Type
              <select
                value={type}
                onChange={(event) => setType(event.target.value)}
                className="border-input bg-background h-9 rounded-lg border px-3 text-sm"
              >
                {cardTypes.map((cardType) => (
                  <option key={cardType} value={cardType}>
                    {cardType}
                  </option>
                ))}
              </select>
            </label>

            <label className="grid gap-2 text-sm font-medium">
              Title
              <Input value={title} onChange={(event) => setTitle(event.target.value)} />
            </label>

            <label className="grid gap-2 text-sm font-medium">
              Image
              <Input
                type="file"
                accept="image/png,image/jpeg,image/webp"
                onChange={(event) => setImage(event.target.files?.[0])}
              />
            </label>
          </CardContent>
        </UiCard>

        <KeyValueEditor
          title="Properties"
          rows={properties}
          onRowsChange={setProperties}
        />

        <UiCard>
          <CardHeader>
            <div className="flex items-center justify-between gap-3">
              <CardTitle>Relations</CardTitle>
              <Button
                type="button"
                variant="outline"
                onClick={() =>
                  setRelations((current) => [
                    ...current,
                    { toCardId: '', relationType: 'related_to', properties: [] },
                  ])
                }
              >
                <Plus className="size-4" />
                Add relation
              </Button>
            </div>
          </CardHeader>
          <CardContent className="grid gap-4">
            {relations.map((relation, index) => (
              <div key={index} className="grid gap-3 rounded-lg border p-3">
                <div className="grid gap-3 md:grid-cols-[1fr_1fr_auto]">
                  <select
                    value={relation.toCardId}
                    onChange={(event) =>
                      updateRelation(index, { ...relation, toCardId: event.target.value })
                    }
                    className="border-input bg-background h-9 rounded-lg border px-3 text-sm"
                  >
                    <option value="">Target card</option>
                    {availableCards.map((card) => (
                      <option key={card.id} value={card.id}>
                        #{card.id} {card.title ?? card.type}
                      </option>
                    ))}
                  </select>

                  <select
                    value={relation.relationType}
                    onChange={(event) =>
                      updateRelation(index, { ...relation, relationType: event.target.value })
                    }
                    className="border-input bg-background h-9 rounded-lg border px-3 text-sm"
                  >
                    {relationTypes.map((relationType) => (
                      <option key={relationType} value={relationType}>
                        {relationType}
                      </option>
                    ))}
                  </select>

                  <Button
                    type="button"
                    variant="ghost"
                    size="icon"
                    onClick={() =>
                      setRelations((current) => current.filter((_, relationIndex) => relationIndex !== index))
                    }
                  >
                    <Trash2 className="size-4" />
                  </Button>
                </div>

                <KeyValueRows
                  rows={relation.properties}
                  onRowsChange={(rows) => updateRelation(index, { ...relation, properties: rows })}
                />
              </div>
            ))}
          </CardContent>
        </UiCard>

        {error ? <div className="text-destructive text-sm">{error}</div> : null}

        <div className="flex justify-end">
          <Button type="submit" disabled={isSubmitting}>
            {isSubmitting ? 'Creating...' : 'Create card'}
          </Button>
        </div>
      </form>
    </section>
  );

  function updateRelation(index: number, nextRelation: RelationRow) {
    setRelations((current) =>
      current.map((relation, relationIndex) =>
        relationIndex === index ? nextRelation : relation
      )
    );
  }
}

function KeyValueEditor({
  title,
  rows,
  onRowsChange,
}: {
  title: string;
  rows: KeyValueRow[];
  onRowsChange: (rows: KeyValueRow[]) => void;
}) {
  return (
    <UiCard>
      <CardHeader>
        <div className="flex items-center justify-between gap-3">
          <CardTitle>{title}</CardTitle>
          <Button
            type="button"
            variant="outline"
            onClick={() => onRowsChange([...rows, { key: '', value: '' }])}
          >
            <Plus className="size-4" />
            Add property
          </Button>
        </div>
      </CardHeader>
      <CardContent>
        <KeyValueRows rows={rows} onRowsChange={onRowsChange} />
      </CardContent>
    </UiCard>
  );
}

function KeyValueRows({
  rows,
  onRowsChange,
}: {
  rows: KeyValueRow[];
  onRowsChange: (rows: KeyValueRow[]) => void;
}) {
  return (
    <div className="grid gap-2">
      {rows.map((row, index) => (
        <div key={index} className="grid gap-2 md:grid-cols-[1fr_1fr_auto]">
          <Input
            value={row.key}
            placeholder="Key"
            onChange={(event) =>
              onRowsChange(rows.map((item, itemIndex) =>
                itemIndex === index ? { ...item, key: event.target.value } : item
              ))
            }
          />
          <Input
            value={row.value}
            placeholder="Value"
            onChange={(event) =>
              onRowsChange(rows.map((item, itemIndex) =>
                itemIndex === index ? { ...item, value: event.target.value } : item
              ))
            }
          />
          <Button
            type="button"
            variant="ghost"
            size="icon"
            onClick={() => onRowsChange(rows.filter((_, itemIndex) => itemIndex !== index))}
          >
            <Trash2 className="size-4" />
          </Button>
        </div>
      ))}
    </div>
  );
}

function toRecord(rows: KeyValueRow[]) {
  return rows.reduce<Record<string, string>>((record, row) => {
    const key = row.key.trim();

    if (key) {
      record[key] = row.value;
    }

    return record;
  }, {});
}
