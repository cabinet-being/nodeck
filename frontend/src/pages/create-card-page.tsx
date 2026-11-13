import * as React from 'react';
import { Layers3, Plus, Upload } from 'lucide-react';

import { createCard, createCollectionFromCards, listCards, type Card } from '@/api/cards';
import {
  CardForm,
  toRecord,
  toRelationInputs,
  type CardFormValue,
} from '@/components/card-form';
import { ExistingMediaPicker } from '@/components/existing-media-picker';
import { FormActions, FormPage, FormSection } from '@/components/form-layout';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Segmented } from '@/components/ui/segmented';

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

      if ((value.type === 'comic' || value.type === 'set') && value.images.length < 1) {
        throw new Error(`${value.type} cards require at least one file.`);
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
    if (selectedMedia.length < 1) {
      setError('Select at least one media card.');
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
    <FormPage title="Create card">
      <Segmented
        ariaLabel="Creation method"
        value={useExisting ? 'existing' : 'upload'}
        onValueChange={(mode) => { setUseExisting(mode === 'existing'); setError(null); }}
        options={[
          { value: 'upload', label: 'Upload', icon: <Upload /> },
          { value: 'existing', label: 'Group existing', icon: <Layers3 /> },
        ]}
        className="justify-self-start self-start"
      />

      {useExisting ? (
        <form onSubmit={handleExistingSubmit} className="grid gap-4">
          <FormSection>
            <div className="grid gap-2 text-sm font-medium">Type
              <Segmented
                ariaLabel="Collection type"
                value={collectionType}
                onValueChange={setCollectionType}
                options={[{ value: 'comic', label: 'Comic' }, { value: 'set', label: 'Set' }]}
                className="justify-self-start"
              />
            </div>
            <label className="grid gap-2 text-sm font-medium">Title
              <Input value={collectionTitle} onChange={(event) => setCollectionTitle(event.target.value)} />
            </label>
          </FormSection>
          <p className="text-muted-foreground text-sm">
            Tip: in the Gallery, a deck or Favorites, press <span className="text-foreground font-medium">Select</span> and
            tap media in page order, then choose Make comic or Make set.
          </p>
          <ExistingMediaPicker selected={selectedMedia} onChange={setSelectedMedia} disabled={isSubmitting} />
          <FormActions error={error}>
            <Button type="button" variant="outline" disabled={isSubmitting} onClick={() => onCreated('/gallery')}>Cancel</Button>
            <Button type="submit" disabled={isSubmitting || selectedMedia.length < 1}><Plus className="size-4" />{isSubmitting ? 'Creating...' : 'Create collection'}</Button>
          </FormActions>
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
          onCancel={() => onCreated('/gallery')}
        />
      )}
    </FormPage>
  );
}
