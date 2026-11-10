import * as React from 'react';
import { ArrowDown, ArrowUp, GripVertical, Plus, Trash2, Upload } from 'lucide-react';

import { cardTypes, relationTypes, type Card, type CreateRelationInput } from '@/api/cards';
import { Button } from '@/components/ui/button';
import { Advanced, FormActions, FormSection, relationLabel } from '@/components/form-layout';
import { Input } from '@/components/ui/input';
import { Segmented } from '@/components/ui/segmented';

export type KeyValueRow = {
  key: string;
  value: string;
};

export type RelationRow = {
  toCardId: string;
  relationType: string;
  properties: KeyValueRow[];
};

export type UploadedImage = {
  id: string;
  file: File;
  previewUrl: string;
};

export type CardFormValue = {
  type: string;
  title: string;
  image?: File;
  images: UploadedImage[];
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
  onCancel,
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
  onCancel?: () => void;
  incomingRelations?: React.ReactNode;
}) {
  const isMultiImageCard = !typeLocked && (value.type === 'comic' || value.type === 'set');
  const isContentCard = value.type === 'media' || value.type === 'comic' || value.type === 'set';
  const [uploadError, setUploadError] = React.useState<string | null>(null);
  const [isDraggingOver, setIsDraggingOver] = React.useState(false);
  const [draggedIndex, setDraggedIndex] = React.useState<number | null>(null);

  return (
    <form onSubmit={onSubmit} className="grid gap-4">
      <FormSection>
          <div className="grid gap-2 text-sm font-medium">
            Type
            {typeLocked ? (
              <span className="text-muted-foreground font-normal capitalize">{value.type}</span>
            ) : (
              <Segmented
                ariaLabel="Card type"
                value={value.type}
                onValueChange={(type) => onValueChange({ ...value, type, image: undefined, images: [] })}
                options={cardTypes.map((cardType) => ({
                  value: cardType,
                  label: <span className="capitalize">{cardType}</span>,
                }))}
              />
            )}
          </div>

          <label className="grid gap-2 text-sm font-medium">
            Title
            <Input
              value={value.title}
              onChange={(event) => onValueChange({ ...value, title: event.target.value })}
            />
          </label>

          {value.type === 'media' ? (
            <label className="grid gap-2 text-sm font-medium">
              Media file
              <Input
                type="file"
                accept="image/png,image/jpeg,image/webp,image/gif,video/mp4,video/webm,video/quicktime,.mov"
                onChange={(event) =>
                  onValueChange({ ...value, image: event.target.files?.[0] })
                }
              />
            </label>
          ) : null}

          {isMultiImageCard ? (
            <div className="grid gap-3">
              <div
                className={[
                  'grid min-h-32 place-items-center rounded-lg border border-dashed p-4 text-center transition-colors',
                  isDraggingOver ? 'border-primary bg-muted' : 'border-border',
                ].join(' ')}
                onDragOver={(event) => {
                  event.preventDefault();
                  setIsDraggingOver(true);
                }}
                onDragLeave={() => setIsDraggingOver(false)}
                onDrop={(event) => {
                  event.preventDefault();
                  setIsDraggingOver(false);
                  addImages(Array.from(event.dataTransfer.files));
                }}
              >
                <div className="grid gap-2">
                  <Upload className="text-muted-foreground mx-auto size-6" />
                  <div className="text-sm font-medium">Drop images here</div>
                  <label className="inline-flex justify-center">
                    <span className="sr-only">Choose images</span>
                    <Input
                      type="file"
                      accept="image/png,image/jpeg,image/webp"
                      multiple
                      className="max-w-64"
                      onChange={(event) => addImages(Array.from(event.target.files ?? []))}
                    />
                  </label>
                </div>
              </div>

              {uploadError ? <div className="text-destructive text-sm">{uploadError}</div> : null}

              {value.images.length > 0 ? (
                <div className="grid gap-2">
                  {value.images.map((image, index) => (
                    <div
                      key={image.id}
                      draggable
                      onDragStart={() => setDraggedIndex(index)}
                      onDragOver={(event) => event.preventDefault()}
                      onDrop={() => {
                        if (draggedIndex !== null) {
                          moveImage(draggedIndex, index);
                        }
                        setDraggedIndex(null);
                      }}
                      className="grid gap-3 rounded-lg border p-3 md:grid-cols-[auto_5rem_1fr_auto]"
                    >
                      <div className="flex items-center gap-2">
                        <GripVertical className="text-muted-foreground size-4" />
                        <span className="text-muted-foreground w-7 text-sm">{index + 1}</span>
                      </div>
                      <img
                        src={image.previewUrl}
                        alt=""
                        className="bg-muted size-20 rounded object-cover"
                      />
                      <div className="min-w-0">
                        <div className="truncate text-sm font-medium">{image.file.name}</div>
                        {index === 0 ? (
                          <div className="text-muted-foreground text-xs">Cover</div>
                        ) : null}
                      </div>
                      <div className="flex items-center justify-end gap-1">
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon"
                          aria-label={`Move ${image.file.name} up`}
                          title="Move up"
                          disabled={index === 0}
                          onClick={() => moveImage(index, index - 1)}
                        >
                          <ArrowUp className="size-4" />
                        </Button>
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon"
                          aria-label={`Move ${image.file.name} down`}
                          title="Move down"
                          disabled={index === value.images.length - 1}
                          onClick={() => moveImage(index, index + 1)}
                        >
                          <ArrowDown className="size-4" />
                        </Button>
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon"
                          aria-label={`Remove ${image.file.name}`}
                          title="Remove image"
                          onClick={() => removeImage(index)}
                        >
                          <Trash2 className="size-4" />
                        </Button>
                      </div>
                    </div>
                  ))}
                </div>
              ) : null}
            </div>
          ) : null}
      </FormSection>

      <Advanced summary={`Advanced${value.properties.length > 0 ? ` (${value.properties.length} properties)` : ''}`}
        defaultOpen={value.properties.length > 0}>
        <div className="grid gap-2 text-sm font-medium">
          Properties
          <KeyValueEditor
            rows={value.properties}
            onRowsChange={(properties) => onValueChange({ ...value, properties })}
          />
        </div>
      </Advanced>

      <FormSection title="Related cards" action={
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
      }>
          {value.relations.map((relation, index) => isContentCard && relation.relationType !== 'related_to' ? null : (
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
                  aria-label="Related card" className="border-input bg-background h-9 rounded-lg border px-3 text-sm pointer-coarse:h-11"
                >
                  <option value="">Choose a card</option>
                  {availableCards
                    .filter((card) => String(card.id) !== relation.toCardId || relation.toCardId)
                    .map((card) => (
                      <option key={card.id} value={card.id}>
                        {card.title || `Untitled ${card.type}`}
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
                  aria-label="Relation kind" className="border-input bg-background h-9 rounded-lg border px-3 text-sm pointer-coarse:h-11"
                >
                  {(isContentCard ? ['related_to'] : relationTypes).map((relationType) => (
                    <option key={relationType} value={relationType}>
                      {relationLabel(relationType)}
                    </option>
                  ))}
                </select>

                <Button
                  type="button"
                  variant="ghost"
                  size="icon"
                  aria-label={`Remove relation ${index + 1}`}
                  title="Remove relation"
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
      </FormSection>

      {incomingRelations}

      <FormActions error={error}>
        {onCancel ? (
          <Button type="button" variant="outline" disabled={isSubmitting} onClick={onCancel}>Cancel</Button>
        ) : null}
        <Button type="submit" disabled={isSubmitting}>
          {isSubmitting ? submittingLabel : submitLabel}
        </Button>
      </FormActions>
    </form>
  );

  function addImages(files: File[]) {
    const supportedImages = files.filter(isSupportedImage);
    const rejectedCount = files.length - supportedImages.length;

    setUploadError(rejectedCount > 0 ? `${rejectedCount} unsupported file(s) skipped.` : null);

    if (supportedImages.length === 0) {
      return;
    }

    onValueChange({
      ...value,
      images: [
        ...value.images,
        ...supportedImages.map((file) => ({
          id: `${file.name}-${file.size}-${file.lastModified}-${crypto.randomUUID()}`,
          file,
          previewUrl: URL.createObjectURL(file),
        })),
      ],
    });
  }

  function moveImage(fromIndex: number, toIndex: number) {
    if (toIndex < 0 || toIndex >= value.images.length) {
      return;
    }

    const nextImages = [...value.images];
    const [image] = nextImages.splice(fromIndex, 1);
    nextImages.splice(toIndex, 0, image);
    onValueChange({ ...value, images: nextImages });
  }

  function removeImage(index: number) {
    const image = value.images[index];

    if (image) {
      URL.revokeObjectURL(image.previewUrl);
    }

    onValueChange({
      ...value,
      images: value.images.filter((_, imageIndex) => imageIndex !== index),
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

function isSupportedImage(file: File) {
  return ['image/jpeg', 'image/png', 'image/webp'].includes(file.type);
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
  return (card.outgoingRelations ?? [])
    .filter((relation) => relationTypes.includes(relation.relationType as (typeof relationTypes)[number]))
    .map<RelationRow>((relation) => ({
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
  rows,
  onRowsChange,
}: {
  rows: KeyValueRow[];
  onRowsChange: (rows: KeyValueRow[]) => void;
}) {
  return (
    <div className="grid gap-3">
      <KeyValueRows rows={rows} onRowsChange={onRowsChange} />
      <Button
        type="button"
        variant="outline"
        className="justify-self-start"
        onClick={() => onRowsChange([...rows, { key: '', value: '' }])}
      >
        <Plus className="size-4" /> Add property
      </Button>
    </div>
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
            aria-label={`Remove property ${index + 1}`}
            title="Remove property"
            onClick={() => onRowsChange(rows.filter((_, itemIndex) => itemIndex !== index))}
          >
            <Trash2 className="size-4" />
          </Button>
        </div>
      ))}
    </div>
  );
}
