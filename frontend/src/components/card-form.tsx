import * as React from 'react';
import { Plus, Trash2 } from 'lucide-react';

import { cardTypes, relationTypes, type Card, type CreateRelationInput } from '@/api/cards';
import { Button } from '@/components/ui/button';
import { Card as UiCard, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';

export type KeyValueRow = {
  key: string;
  value: string;
};

export type RelationRow = {
  toCardId: string;
  relationType: string;
  properties: KeyValueRow[];
};

export type CardFormValue = {
  type: string;
  title: string;
  image?: File;
  properties: KeyValueRow[];
  relations: RelationRow[];
};

export function CardForm({
  value,
  availableCards,
  isSubmitting,
  submitLabel,
  submittingLabel,
  error,
  typeLocked = false,
  onValueChange,
  onSubmit,
  incomingRelations,
}: {
  value: CardFormValue;
  availableCards: Card[];
  isSubmitting: boolean;
  submitLabel: string;
  submittingLabel: string;
  error: string | null;
  typeLocked?: boolean;
  onValueChange: (value: CardFormValue) => void;
  onSubmit: (event: React.FormEvent<HTMLFormElement>) => void;
  incomingRelations?: React.ReactNode;
}) {
  return (
    <form onSubmit={onSubmit} className="grid gap-4">
      <UiCard>
        <CardHeader>
          <CardTitle>Card</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-4">
          <label className="grid gap-2 text-sm font-medium">
            Type
            <select
              value={value.type}
              disabled={typeLocked}
              onChange={(event) => onValueChange({ ...value, type: event.target.value })}
              className="border-input bg-background h-9 rounded-lg border px-3 text-sm disabled:opacity-70"
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
            <Input
              value={value.title}
              onChange={(event) => onValueChange({ ...value, title: event.target.value })}
            />
          </label>

          <label className="grid gap-2 text-sm font-medium">
            Image
            <Input
              type="file"
              accept="image/png,image/jpeg,image/webp"
              disabled={value.type !== 'media'}
              onChange={(event) =>
                onValueChange({ ...value, image: event.target.files?.[0] })
              }
            />
          </label>
        </CardContent>
      </UiCard>

      <KeyValueEditor
        title="Properties"
        rows={value.properties}
        onRowsChange={(properties) => onValueChange({ ...value, properties })}
      />

      <UiCard>
        <CardHeader>
          <div className="flex items-center justify-between gap-3">
            <CardTitle>Outgoing relations</CardTitle>
            <Button
              type="button"
              variant="outline"
              onClick={() =>
                onValueChange({
                  ...value,
                  relations: [
                    ...value.relations,
                    { toCardId: '', relationType: 'related_to', properties: [] },
                  ],
                })
              }
            >
              <Plus className="size-4" />
              Add relation
            </Button>
          </div>
        </CardHeader>
        <CardContent className="grid gap-4">
          {value.relations.map((relation, index) => (
            <div key={index} className="grid gap-3 rounded-lg border p-3">
              <div className="grid gap-3 md:grid-cols-[1fr_1fr_auto]">
                <select
                  value={relation.toCardId}
                  onChange={(event) =>
                    updateRelation(value, onValueChange, index, {
                      ...relation,
                      toCardId: event.target.value,
                    })
                  }
                  className="border-input bg-background h-9 rounded-lg border px-3 text-sm"
                >
                  <option value="">Target card</option>
                  {availableCards
                    .filter((card) => String(card.id) !== relation.toCardId || relation.toCardId)
                    .map((card) => (
                      <option key={card.id} value={card.id}>
                        #{card.id} {card.title ?? card.type}
                      </option>
                    ))}
                </select>

                <select
                  value={relation.relationType}
                  onChange={(event) =>
                    updateRelation(value, onValueChange, index, {
                      ...relation,
                      relationType: event.target.value,
                    })
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
                    onValueChange({
                      ...value,
                      relations: value.relations.filter((_, relationIndex) => relationIndex !== index),
                    })
                  }
                >
                  <Trash2 className="size-4" />
                </Button>
              </div>

              <KeyValueRows
                rows={relation.properties}
                onRowsChange={(rows) =>
                  updateRelation(value, onValueChange, index, {
                    ...relation,
                    properties: rows,
                  })
                }
              />
            </div>
          ))}
        </CardContent>
      </UiCard>

      {incomingRelations}

      {error ? <div className="text-destructive text-sm">{error}</div> : null}

      <div className="flex justify-end">
        <Button type="submit" disabled={isSubmitting}>
          {isSubmitting ? submittingLabel : submitLabel}
        </Button>
      </div>
    </form>
  );
}

export function toRecord(rows: KeyValueRow[]) {
  return rows.reduce<Record<string, string>>((record, row) => {
    const key = row.key.trim();

    if (key) {
      record[key] = row.value;
    }

    return record;
  }, {});
}

export function toRelationInputs(rows: RelationRow[]) {
  return rows
    .filter((relation) => relation.toCardId)
    .map<CreateRelationInput>((relation) => ({
      toCardId: Number(relation.toCardId),
      relationType: relation.relationType,
      properties: toRecord(relation.properties),
    }));
}

export function keyValueRowsFromRecord(record: Record<string, unknown> | null | undefined) {
  return Object.entries(record ?? {}).map<KeyValueRow>(([key, value]) => ({
    key,
    value: String(value ?? ''),
  }));
}

export function relationRowsFromCard(card: Card) {
  return (card.outgoingRelations ?? []).map<RelationRow>((relation) => ({
    toCardId: String(relation.toCardId),
    relationType: relation.relationType,
    properties: keyValueRowsFromRecord(relation.properties),
  }));
}

function updateRelation(
  value: CardFormValue,
  onValueChange: (value: CardFormValue) => void,
  index: number,
  nextRelation: RelationRow
) {
  onValueChange({
    ...value,
    relations: value.relations.map((relation, relationIndex) =>
      relationIndex === index ? nextRelation : relation
    ),
  });
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
