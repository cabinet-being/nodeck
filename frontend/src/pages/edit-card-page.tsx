import * as React from 'react';

import { getCard, listCards, resolveApiUrl, updateCard, updateCardPreview, updateCollectionChildren, type Card } from '@/api/cards';
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
import { ExistingMediaPicker } from '@/components/existing-media-picker';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';

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
  const [selectedMedia, setSelectedMedia] = React.useState<Card[]>([]);
  const [isSavingChildren, setIsSavingChildren] = React.useState(false);
  const [childrenMessage, setChildrenMessage] = React.useState<string | null>(null);

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
        setSelectedMedia((loadedCard.containedCards ?? []).map((item) => ({
          ...item,
          properties: null,
          metadata: {},
        })));
        setAvailableCards(cards.filter((item) => item.id !== loadedCard.id));
        setValue({
          type: loadedCard.type,
          title: loadedCard.title ?? '',
          images: [],
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
        incomingRelations={
          <>
            {card.type === 'media' ? <CurrentMediaInfo card={card} /> : null}
            {card.type === 'media' && !value.image ? (
              <PreviewStartControl key={card.id} card={card} onUpdated={setCard} />
            ) : null}
            <IncomingRelations card={card} />
          </>
        }
      />
      {card.type === 'comic' || card.type === 'set' ? (
        <UiCard>
          <CardHeader><CardTitle>Collection contents</CardTitle></CardHeader>
          <CardContent className="grid gap-4">
            <p className="text-muted-foreground text-sm">Add, remove, or reorder existing media. Removed media remain in the library.</p>
            <ExistingMediaPicker selected={selectedMedia} onChange={(cards) => { setSelectedMedia(cards); setChildrenMessage(null); }} disabled={isSavingChildren} />
            {childrenMessage ? <p className="text-sm" role="status">{childrenMessage}</p> : null}
            <Button type="button" disabled={isSavingChildren || selectedMedia.length < 2} onClick={saveChildren}>
              {isSavingChildren ? 'Saving contents...' : 'Save contents'}
            </Button>
          </CardContent>
        </UiCard>
      ) : null}
    </section>
  );

  async function saveChildren() {
    setIsSavingChildren(true);
    setChildrenMessage(null);
    try {
      const updated = await updateCollectionChildren(cardId, selectedMedia.map((item) => item.id));
      setCard(updated);
      setChildrenMessage('Collection contents saved.');
    } catch (cause) {
      setChildrenMessage(cause instanceof Error ? cause.message : 'Unable to save contents.');
    } finally {
      setIsSavingChildren(false);
    }
  }
}

function PreviewStartControl({ card, onUpdated }: { card: Card; onUpdated: (card: Card) => void }) {
  const duration = card.metadata.duration;
  const mediaType = card.metadata.media_type;
  const canAdjust = (mediaType === 'video' || mediaType === 'gif')
    && typeof duration === 'number' && Number.isFinite(duration) && duration > 0;
  const maxStart = canAdjust ? Math.max(0, duration - Math.min(3, duration)) : 0;
  const currentStart = typeof card.metadata.preview_start_seconds === 'number'
    ? card.metadata.preview_start_seconds : 0;
  const [startSeconds, setStartSeconds] = React.useState(String(currentStart));
  const [isSaving, setIsSaving] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [message, setMessage] = React.useState<string | null>(null);
  const [previewVersion, setPreviewVersion] = React.useState(0);

  if (!canAdjust) return null;

  const manual = card.metadata.preview_start_manual === true;
  const selectedStart = Number(startSeconds);
  const validStart = startSeconds.trim() !== '' && Number.isFinite(selectedStart)
    && selectedStart >= 0 && selectedStart <= maxStart;
  const previewUrl = card.previewUrl ? versionedUrl(resolveApiUrl(card.previewUrl), previewVersion) : null;
  const posterUrl = card.posterUrl ? versionedUrl(resolveApiUrl(card.posterUrl), previewVersion) : null;

  async function savePreview(nextStart: number | null) {
    setIsSaving(true);
    setError(null);
    setMessage(null);
    try {
      const updated = await updateCardPreview(card.id, nextStart);
      onUpdated(updated);
      setStartSeconds(String(typeof updated.metadata.preview_start_seconds === 'number'
        ? updated.metadata.preview_start_seconds : 0));
      setPreviewVersion(Date.now());
      setMessage(nextStart === null ? 'Automatic preview restored.' : 'Preview saved.');
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Unable to update preview.');
    } finally {
      setIsSaving(false);
    }
  }

  function updateStart(rawValue: number) {
    if (Number.isFinite(rawValue)) {
      setStartSeconds(String(Math.min(maxStart, Math.max(0, rawValue))));
      setMessage(null);
    }
  }

  return (
    <UiCard>
      <CardHeader><CardTitle>Preview start</CardTitle></CardHeader>
      <CardContent className="grid gap-4 text-sm">
        <div className="flex flex-wrap gap-4">
          {posterUrl ? (
            <div className="grid gap-1">
              <span className="text-muted-foreground">Poster</span>
              <img src={posterUrl} alt="Current preview poster" className="bg-muted h-32 w-32 rounded object-cover" />
            </div>
          ) : null}
          {previewUrl ? (
            <div className="grid gap-1">
              <span className="text-muted-foreground">Animation</span>
              <img src={previewUrl} alt="Current animated preview" className="bg-muted h-32 w-32 rounded object-cover" />
            </div>
          ) : null}
        </div>
        <p className="text-muted-foreground">{manual ? 'Manual' : 'Automatic'} start: {currentStart.toFixed(1)}s of {duration.toFixed(1)}s</p>
        <label className="grid gap-2 font-medium">
          Start time (seconds)
          <div className="flex flex-wrap items-center gap-3">
            <input
              type="range"
              min={0}
              max={maxStart}
              step="any"
              value={validStart ? selectedStart : 0}
              disabled={isSaving || maxStart === 0}
              onChange={(event) => updateStart(event.currentTarget.valueAsNumber)}
              className="min-w-40 flex-1 accent-primary"
            />
            <Input
              type="number"
              min={0}
              max={maxStart}
              step="any"
              value={startSeconds}
              disabled={isSaving}
              onChange={(event) => { setStartSeconds(event.currentTarget.value); setMessage(null); }}
              className="w-24"
            />
          </div>
        </label>
        {error ? <p role="alert" className="text-destructive">{error}</p> : null}
        {message ? <p role="status">{message}</p> : null}
        <div className="flex flex-wrap gap-2">
          <Button type="button" disabled={isSaving || !validStart} onClick={() => savePreview(selectedStart)}>
            {isSaving ? 'Saving preview...' : 'Save preview'}
          </Button>
          <Button type="button" variant="outline" disabled={isSaving || !manual} onClick={() => savePreview(null)}>
            Reset to automatic
          </Button>
        </div>
      </CardContent>
    </UiCard>
  );
}

function versionedUrl(url: string, version: number) {
  return version ? `${url}${url.includes('?') ? '&' : '?'}v=${version}` : url;
}

function CurrentMediaInfo({ card }: { card: Card }) {
  return (
    <UiCard>
      <CardHeader>
        <CardTitle>Current media</CardTitle>
      </CardHeader>
      <CardContent className="grid gap-2 text-sm">
        <div className="flex flex-wrap gap-2">
          <Badge variant="outline">{String(card.metadata.media_type ?? 'unknown')}</Badge>
          <span className="text-muted-foreground">
            {String(card.metadata.original_file_name ?? 'No file name')}
          </span>
        </div>
        <div className="text-muted-foreground">
          {String(card.metadata.mime_type ?? 'unknown MIME')}
          {typeof card.metadata.file_size === 'number'
            ? ` - ${Math.round(card.metadata.file_size / 1024)} KB`
            : ''}
        </div>
      </CardContent>
    </UiCard>
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
