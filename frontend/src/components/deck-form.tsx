import * as React from 'react';
import { ArrowDown, ArrowUp, Plus, Trash2 } from 'lucide-react';

import { resolveApiUrl, type Card } from '@/api/cards';
import { type DeckCardInput } from '@/api/decks';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card as UiCard, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';

export type KeyValueRow = {
  key: string;
  value: string;
};

export type SelectedDeckCard = {
  card: Card;
  properties: KeyValueRow[];
};

export type DeckFormValue = {
  title: string;
  properties: KeyValueRow[];
  cards: SelectedDeckCard[];
};

export function DeckForm({
  value,
  availableCards,
  isSubmitting,
  submitLabel,
  submittingLabel,
  error,
  onValueChange,
  onSubmit,
  onCancel,
}: {
  value: DeckFormValue;
  availableCards: Card[];
  isSubmitting: boolean;
  submitLabel: string;
  submittingLabel: string;
  error: string | null;
  onValueChange: (value: DeckFormValue) => void;
  onSubmit: (event: React.FormEvent<HTMLFormElement>) => void;
  onCancel: () => void;
}) {
  const [cardSearch, setCardSearch] = React.useState('');
  const selectedCardIds = new Set(value.cards.map((item) => item.card.id));
  const filteredCards = availableCards
    .filter((card) => !selectedCardIds.has(card.id))
    .filter((card) => {
      const search = cardSearch.trim().toLowerCase();

      if (!search) {
        return true;
      }

      return String(card.id) === search || (card.title ?? card.type).toLowerCase().includes(search);
    })
    .slice(0, 30);

  return (
    <form onSubmit={onSubmit} className="grid gap-4">
      <UiCard>
        <CardHeader>
          <CardTitle>Deck</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-4">
          <label className="grid gap-2 text-sm font-medium">
            Title
            <Input
              value={value.title}
              onChange={(event) => onValueChange({ ...value, title: event.target.value })}
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
          <CardTitle>Add cards</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-3">
          <Input
            value={cardSearch}
            placeholder="Search by ID or title"
            onChange={(event) => setCardSearch(event.target.value)}
          />
          <div className="grid max-h-80 gap-2 overflow-auto">
            {filteredCards.map((card) => (
              <div
                key={card.id}
                className="grid gap-3 rounded-lg border p-3 md:grid-cols-[3rem_1fr_auto]"
              >
                <CardThumb card={card} />
                <div className="min-w-0">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="text-muted-foreground text-sm">#{card.id}</span>
                    <Badge variant="outline">{card.type}</Badge>
                  </div>
                  <div className="truncate text-sm font-medium">{card.title ?? card.type}</div>
                </div>
                <Button type="button" variant="outline" onClick={() => addCard(card)}>
                  <Plus className="size-4" />
                  Add
                </Button>
              </div>
            ))}
            {filteredCards.length === 0 ? (
              <div className="text-muted-foreground rounded-lg border p-4 text-center text-sm">
                No cards
              </div>
            ) : null}
          </div>
        </CardContent>
      </UiCard>

      <UiCard>
        <CardHeader>
          <CardTitle>Selected cards</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-3">
          {value.cards.map((item, index) => (
            <div key={item.card.id} className="grid gap-3 rounded-lg border p-3">
              <div className="grid gap-3 md:grid-cols-[auto_3rem_1fr_auto]">
                <span className="text-muted-foreground w-7 text-sm">{index + 1}</span>
                <CardThumb card={item.card} />
                <div className="min-w-0">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="text-muted-foreground text-sm">#{item.card.id}</span>
                    <Badge variant="outline">{item.card.type}</Badge>
                  </div>
                  <div className="truncate text-sm font-medium">{item.card.title ?? item.card.type}</div>
                </div>
                <div className="flex items-center justify-end gap-1">
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon"
                    disabled={index === 0}
                    onClick={() => moveCard(index, index - 1)}
                  >
                    <ArrowUp className="size-4" />
                  </Button>
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon"
                    disabled={index === value.cards.length - 1}
                    onClick={() => moveCard(index, index + 1)}
                  >
                    <ArrowDown className="size-4" />
                  </Button>
                  <Button
                    type="button"
                    variant="ghost"
                    onClick={() => removeCard(index)}
                  >
                    <Trash2 className="size-4" />
                    Remove from deck
                  </Button>
                </div>
              </div>
              <KeyValueRows
                rows={item.properties}
                onRowsChange={(properties) => updateCardProperties(index, properties)}
              />
            </div>
          ))}
          {value.cards.length === 0 ? (
            <div className="text-muted-foreground rounded-lg border p-4 text-center text-sm">
              No cards selected
            </div>
          ) : null}
        </CardContent>
      </UiCard>

      {error ? <div className="text-destructive text-sm">{error}</div> : null}

      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" disabled={isSubmitting} onClick={onCancel}>
          Cancel
        </Button>
        <Button type="submit" disabled={isSubmitting}>
          {isSubmitting ? submittingLabel : submitLabel}
        </Button>
      </div>
    </form>
  );

  function addCard(card: Card) {
    onValueChange({
      ...value,
      cards: [...value.cards, { card, properties: [] }],
    });
  }

  function moveCard(fromIndex: number, toIndex: number) {
    if (toIndex < 0 || toIndex >= value.cards.length) {
      return;
    }

    const nextCards = [...value.cards];
    const [card] = nextCards.splice(fromIndex, 1);
    nextCards.splice(toIndex, 0, card);
    onValueChange({ ...value, cards: nextCards });
  }

  function removeCard(index: number) {
    onValueChange({
      ...value,
      cards: value.cards.filter((_, itemIndex) => itemIndex !== index),
    });
  }

  function updateCardProperties(index: number, properties: KeyValueRow[]) {
    onValueChange({
      ...value,
      cards: value.cards.map((item, itemIndex) =>
        itemIndex === index ? { ...item, properties } : item
      ),
    });
  }
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

export function toDeckCardInputs(cards: SelectedDeckCard[]) {
  return cards.map<DeckCardInput>((item) => ({
    cardId: item.card.id,
    properties: toRecord(item.properties),
  }));
}

export function keyValueRowsFromRecord(record: Record<string, unknown> | null | undefined) {
  return Object.entries(record ?? {}).map<KeyValueRow>(([key, value]) => ({
    key,
    value: String(value ?? ''),
  }));
}

function CardThumb({ card }: { card: Card }) {
  if (!card.previewUrl) {
    return (
      <div className="bg-muted text-muted-foreground grid size-12 place-items-center rounded border text-xs uppercase">
        {card.type}
      </div>
    );
  }

  return (
    <img
      src={resolveApiUrl(card.previewUrl)}
      alt=""
      className="bg-muted size-12 rounded border object-cover"
      loading="lazy"
    />
  );
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
