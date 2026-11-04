import * as React from 'react';

import { createCard, createCollectionFromCards, listCards, type Card } from '@/api/cards';
import {
  CardForm,
  toRecord,
  toRelationInputs,
  type CardFormValue,
} from '@/components/card-form';
import { ExistingMediaPicker } from '@/components/existing-media-picker';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';

const initialValue: CardFormValue = {
  type: 'media',
  title: '',
  images: [],
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
  const [useExisting, setUseExisting] = React.useState(false);
  const [collectionType, setCollectionType] = React.useState<'comic' | 'set'>('comic');
  const [collectionTitle, setCollectionTitle] = React.useState('');
  const [selectedMedia, setSelectedMedia] = React.useState<Card[]>([]);

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
      if ((value.type === 'tag' || value.type === 'source') && !value.title.trim()) {
        throw new Error(`${value.type} cards require a title.`);
      }

      if ((value.type === 'comic' || value.type === 'set') && value.images.length < 2) {
        throw new Error(`${value.type} cards require at least two images.`);
      }

      const created = await createCard({
        type: value.type,
        title: value.title.trim() || undefined,
        image: value.image,
        images: value.images.map((image) => image.file),
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

  async function handleExistingSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (selectedMedia.length < 2) {
      setError('Select at least two media cards.');
      return;
    }
    setError(null);
    setIsSubmitting(true);
    try {
      const created = await createCollectionFromCards({
        type: collectionType,
        title: collectionTitle.trim() || undefined,
        childCardIds: selectedMedia.map((card) => card.id),
      });
      onCreated(`/cards/${created.id}`);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Unable to create collection.');
    } finally {
      setIsSubmitting(false);
    }
  }

  return (
    <section className="mx-auto flex w-full max-w-4xl flex-col gap-4 p-6">
      <div>
        <h1 className="text-2xl font-semibold">Create Card</h1>
      </div>

      <div className="flex gap-2" role="group" aria-label="Collection creation method">
        <Button type="button" variant={useExisting ? 'outline' : 'secondary'} onClick={() => { setUseExisting(false); setError(null); }}>Upload new files</Button>
        <Button type="button" variant={useExisting ? 'secondary' : 'outline'} onClick={() => { setUseExisting(true); setError(null); }}>Group existing media</Button>
      </div>

      {useExisting ? (
        <form onSubmit={handleExistingSubmit} className="grid gap-4">
          <label className="grid gap-2 text-sm font-medium">Type
            <select className="border-input bg-background h-9 rounded-lg border px-3" value={collectionType} onChange={(event) => setCollectionType(event.target.value as 'comic' | 'set')}>
              <option value="comic">Comic</option><option value="set">Set</option>
            </select>
          </label>
          <label className="grid gap-2 text-sm font-medium">Title
            <Input value={collectionTitle} onChange={(event) => setCollectionTitle(event.target.value)} />
          </label>
          <ExistingMediaPicker selected={selectedMedia} onChange={setSelectedMedia} disabled={isSubmitting} />
          {error ? <p className="text-destructive text-sm">{error}</p> : null}
          <Button type="submit" disabled={isSubmitting || selectedMedia.length < 2}>{isSubmitting ? 'Creating...' : 'Create collection'}</Button>
        </form>
      ) : (

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
      )}
    </section>
  );
}
