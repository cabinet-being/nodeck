import * as React from 'react';

import { getCard, listCards, moveCardChild, resolveApiUrl, updateCard, updateCardPreview, updateCollectionChildren, type Card } from '@/api/cards';
import {
  CardForm,
  keyValueRowsFromRecord,
  relationRowsFromCard,
  toRecord,
  toRelationInputs,
  type CardFormValue,
} from '@/components/card-form';
import { FormPage, FormSection, relationLabel } from '@/components/form-layout';
import { Badge } from '@/components/ui/badge';
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

  const loadedType = card?.type;
  React.useEffect(() => {
    // The card page links straight to the contents section with #contents.
    if ((loadedType === 'comic' || loadedType === 'set') && window.location.hash === '#contents') {
      document.getElementById('contents')?.scrollIntoView({ block: 'start' });
    }
  }, [loadedType]);

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
    return <section role="status" className="text-muted-foreground p-6 text-sm">Loading...</section>;
  }

  if (!value || !card) {
    return <section className="p-6 text-sm text-destructive">{error ?? 'Card not found.'}</section>;
  }

  return (
    <FormPage title="Edit card">
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
        onCancel={() => onSaved(`/cards/${card.id}`)}
        incomingRelations={
          <>
            {card.type === 'media' ? <CurrentMediaInfo card={card} /> : null}
            {card.type === 'media' && !value.image ? (
              <PreviewStartControl key={card.id} card={card} onUpdated={setCard} />
            ) : null}
            <IncomingRelations card={card} knownCards={availableCards} />
          </>
        }
      />
      {card.type === 'comic' || card.type === 'set' ? (
        <div id="contents" className="scroll-mt-20">
        <FormSection title="Collection contents" hint="Add, remove or drag media into order. Removed media stays in the library.">
            <ExistingMediaPicker selected={selectedMedia} onChange={(cards) => { setSelectedMedia(cards); setChildrenMessage(null); }} disabled={isSavingChildren} />
            {childrenMessage ? <p className="text-sm" role="status">{childrenMessage}</p> : null}
            <Button type="button" disabled={isSavingChildren} onClick={saveChildren}>
              {isSavingChildren ? 'Saving contents...' : 'Save contents'}
            </Button>
            {(card.containedCards ?? []).length > 0 ? (
              <MoveChildren card={card} onMoved={reloadCard} />
            ) : null}
        </FormSection>
        </div>
      ) : null}
    </FormPage>
  );

  async function reloadCard() {
    const loaded = await getCard(cardId);
    setCard(loaded);
    setSelectedMedia((loaded.containedCards ?? []).map((item) => ({ ...item, properties: null, metadata: {} })));
    setChildrenMessage(null);
  }

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

function MoveChildren({ card, onMoved }: { card: Card; onMoved: () => Promise<void> }) {
  const children = card.containedCards ?? [];
  const [movingId, setMovingId] = React.useState<number | null>(null);
  const [search, setSearch] = React.useState('');
  const [targets, setTargets] = React.useState<Card[]>([]);
  const [targetId, setTargetId] = React.useState<number | null>(null);
  const [position, setPosition] = React.useState('');
  const [isBusy, setIsBusy] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [message, setMessage] = React.useState<string | null>(null);

  React.useEffect(() => {
    if (movingId === null) return;
    let isCurrent = true;
    const timer = window.setTimeout(() => {
      listCards({ types: ['comic', 'set'], search: search.trim() || undefined, limit: 20, sort: 'created_at', order: 'desc' })
        .then((cards) => { if (isCurrent) setTargets(cards.filter((item) => item.id !== card.id)); })
        .catch((cause) => { if (isCurrent) setError(cause instanceof Error ? cause.message : 'Unable to search collections.'); });
    }, 250);
    return () => { isCurrent = false; window.clearTimeout(timer); };
  }, [movingId, search, card.id]);

  function open(childId: number) {
    setMovingId(childId);
    setSearch('');
    setTargets([]);
    setTargetId(null);
    setPosition('');
    setError(null);
    setMessage(null);
  }

  const parsedPosition = position.trim() === '' ? null : Number(position);
  const positionValid = parsedPosition === null || (Number.isInteger(parsedPosition) && parsedPosition >= 0);

  async function move() {
    if (movingId === null || targetId === null || !positionValid) return;
    setIsBusy(true);
    setError(null);
    try {
      await moveCardChild(card.id, movingId, targetId, parsedPosition);
      setMovingId(null);
      setMessage('Item moved.');
      await onMoved();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Unable to move the item.');
    } finally {
      setIsBusy(false);
    }
  }

  return (
    <div className="grid gap-2 text-sm">
      <h3 className="font-medium">Move saved items</h3>
      {message ? <p role="status">{message}</p> : null}
      {children.map((child) => (
        <div key={child.id} className="grid gap-2 rounded-lg border px-3 py-2">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <span>{child.title || `Untitled ${child.type}`}</span>
            <Button type="button" size="sm" variant="outline" aria-label={`Move ${child.title || child.type} to another collection`}
              disabled={isBusy} onClick={() => (movingId === child.id ? setMovingId(null) : open(child.id))}>
              {movingId === child.id ? 'Close' : 'Move to...'}
            </Button>
          </div>
          {movingId === child.id ? (
            <div className="grid gap-2">
              <Input aria-label="Search target comic or set" placeholder="Search comics and sets" value={search}
                onChange={(event) => setSearch(event.currentTarget.value)} />
              <div className="grid max-h-48 gap-1 overflow-auto" role="listbox" aria-label="Target collections">
                {targets.length === 0 ? <span className="text-muted-foreground">No matching comics or sets.</span> : null}
                {targets.map((target) => (
                  <button key={target.id} type="button" role="option" aria-selected={targetId === target.id}
                    className={`rounded border px-2 py-1 text-left ${targetId === target.id ? 'border-primary bg-accent' : ''}`}
                    onClick={() => setTargetId(target.id)}>
                    <Badge variant="outline">{target.type}</Badge> {target.title || `Untitled ${target.type}`}
                  </button>
                ))}
              </div>
              <label className="flex flex-wrap items-center gap-2">
                Position (0-based, empty to append)
                <Input type="number" min={0} step={1} className="w-24" value={position}
                  onChange={(event) => setPosition(event.currentTarget.value)} />
              </label>
              {error ? <p role="alert" className="text-destructive">{error}</p> : null}
              <div>
                <Button type="button" size="sm" disabled={isBusy || targetId === null || !positionValid} onClick={move}>
                  {isBusy ? 'Moving...' : 'Move'}
                </Button>
              </div>
            </div>
          ) : null}
        </div>
      ))}
    </div>
  );
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
  const selection = card.metadata.preview_selection;
  const startLabel = selection === 'poster_only' ? 'Poster only'
    : selection === 'fallback' ? 'Auto (low quality)'
    : manual ? 'Manual' : 'Automatic';
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
    <FormSection title="Preview start">
      <div className="grid gap-4 text-sm">
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
        <p className="text-muted-foreground">{startLabel} start: {currentStart.toFixed(1)}s of {duration.toFixed(1)}s</p>
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
      </div>
    </FormSection>
  );
}

function versionedUrl(url: string, version: number) {
  return version ? `${url}${url.includes('?') ? '&' : '?'}v=${version}` : url;
}

function CurrentMediaInfo({ card }: { card: Card }) {
  return (
    <FormSection title="Current media">
      <div className="grid gap-2 text-sm">
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
      </div>
    </FormSection>
  );
}

function IncomingRelations({ card, knownCards }: { card: Card; knownCards: Card[] }) {
  const incomingRelations = card.incomingRelations ?? [];

  return (
    <FormSection title="Linked from">
      <div>
        {incomingRelations.length > 0 ? (
          <div className="grid gap-2 text-sm">
            {incomingRelations.map((relation) => (
              <div
                key={relation.id}
                className="flex flex-wrap items-center gap-2 rounded-lg border px-3 py-2"
              >
                <span className="font-medium">
                  {knownCards.find((item) => item.id === relation.fromCardId)?.title?.trim() || 'Untitled card'}
                </span>
                <Badge variant="outline">{relationLabel(relation.relationType)}</Badge>
              </div>
            ))}
          </div>
        ) : (
          <div className="text-muted-foreground text-sm">No cards link here</div>
        )}
      </div>
    </FormSection>
  );
}
