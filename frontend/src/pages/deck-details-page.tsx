import * as React from 'react';
import { Heart, Info, LoaderCircle, MinusCircle, MoreHorizontal, Pencil, Trash2 } from 'lucide-react';

import { type Card } from '@/api/cards';
import { getDeck, removeCardFromDeck, type DeckDetails } from '@/api/decks';
import { CardBrowser } from '@/components/card-browser';
import { cardLabel } from '@/components/card-tile';
import { deckCardCountLabel } from '@/components/deck-stack';
import { DeleteDeckDialog } from '@/components/delete-deck-dialog';
import { Button, buttonVariants } from '@/components/ui/button';
import { Menu, MenuContent, MenuItem, MenuTrigger } from '@/components/ui/menu';
import { Sheet } from '@/components/ui/sheet';

export function DeckDetailsPage({
  deckId,
  basePath = `/decks/${deckId}`,
  currentSearch,
  onNavigate,
  onNavigateTo,
}: {
  deckId: number;
  /** Address of this page; filters and paging keep it (Favorites lives at /favorites). */
  basePath?: string;
  currentSearch: string;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onNavigateTo: (path: string) => void;
}) {
  const [result, setResult] = React.useState<{
    deckId: number;
    deck: DeckDetails | null;
    error: string | null;
  } | null>(null);
  const [reloadKey, setReloadKey] = React.useState(0);
  const [removingId, setRemovingId] = React.useState<number | null>(null);
  const [removeError, setRemoveError] = React.useState<string | null>(null);
  const [headerRetry, setHeaderRetry] = React.useState(0);
  const [detailsOpen, setDetailsOpen] = React.useState(false);
  const [deleteOpen, setDeleteOpen] = React.useState(false);
  const pageCount = React.useRef(0);
  const currentDeckId = React.useRef(deckId);
  currentDeckId.current = deckId;
  const pageParam = Number(new URLSearchParams(currentSearch).get('page'));
  const page = Number.isInteger(pageParam) && pageParam > 0 ? pageParam : 1;

  React.useEffect(() => {
    let active = true;
    setRemovingId(null);
    setRemoveError(null);
    setDetailsOpen(false);
    setDeleteOpen(false);

    getDeck(deckId, { includeCards: false })
      .then((deck) => {
        if (active) setResult({ deckId, deck, error: null });
      })
      .catch((requestError) => {
        if (active) setResult({
          deckId,
          deck: null,
          error: requestError instanceof Error ? requestError.message : 'Unable to load deck.',
        });
      });

    return () => { active = false; };
  }, [deckId, headerRetry]);

  if (result?.deckId === deckId && result.error) {
    return (
      <div className="flex flex-wrap items-center gap-3 p-6" role="alert">
        <p className="text-destructive text-sm">{result.error}</p>
        <Button type="button" size="sm" variant="outline" onClick={() => {
          setResult(null);
          setHeaderRetry((key) => key + 1);
        }}>Retry</Button>
      </div>
    );
  }

  if (result?.deckId !== deckId || !result.deck) {
    return <div className="text-muted-foreground p-6 text-sm">Loading...</div>;
  }

  const deck = result.deck;
  const canRemove = deck.systemKey !== 'favorites';

  async function removeCard(cardId: number) {
    if (removingId !== null) return;
    setRemovingId(cardId);
    setRemoveError(null);

    try {
      await removeCardFromDeck(deckId, cardId);
      await refreshAfterRemoval();
    } catch (error) {
      if (currentDeckId.current === deckId) {
        setRemoveError(error instanceof Error ? error.message : 'Unable to remove card.');
      }
    } finally {
      if (currentDeckId.current === deckId) setRemovingId(null);
    }
  }

  // Re-reads the card count and the current page, stepping back when the last card of a page left.
  async function refreshAfterRemoval() {
    if (currentDeckId.current !== deckId) return;
    const updated = await getDeck(deckId, { includeCards: false }).catch(() => null);
    if (currentDeckId.current !== deckId) return;
    if (updated) setResult({ deckId, deck: updated, error: null });
    if (pageCount.current <= 1 && page > 1) {
      const params = new URLSearchParams(currentSearch);
      if (page - 1 <= 1) params.delete('page');
      else params.set('page', String(page - 1));
      onNavigateTo(`${basePath}${params.toString() ? `?${params.toString()}` : ''}`);
    } else {
      setReloadKey((key) => key + 1);
    }
  }

  const isFavorites = deck.systemKey === 'favorites';

  return (
    <section className="grid gap-5 px-4 py-6 sm:px-6 sm:py-8">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="flex min-w-0 items-center gap-2 text-2xl font-semibold tracking-tight">
          {isFavorites ? <Heart className="text-primary size-5 shrink-0 fill-current" aria-hidden="true" /> : null}
          <span className="truncate">{deck.title}</span>
          <span className="text-muted-foreground text-base font-normal whitespace-nowrap tabular-nums">
            · {deckCardCountLabel(deck.cardCount)}
          </span>
        </h1>
        <div className="flex items-center gap-1">
          {isFavorites ? null : (
            <a href={`/decks/${deck.id}/edit`} onClick={onNavigate} className={buttonVariants({ variant: 'outline' })}>
              <Pencil className="size-4" /> Edit
            </a>
          )}
          <Menu>
            <MenuTrigger render={<Button type="button" variant="ghost" size="icon" aria-label="More actions" />}>
              <MoreHorizontal />
            </MenuTrigger>
            <MenuContent>
              <MenuItem onClick={() => setDetailsOpen(true)}><Info /> Details</MenuItem>
              {isFavorites ? null : (
                <MenuItem onClick={() => setDeleteOpen(true)} className="text-destructive [&_svg]:text-destructive">
                  <Trash2 /> Delete
                </MenuItem>
              )}
            </MenuContent>
          </Menu>
        </div>
      </div>

      {removeError ? <p className="text-destructive text-sm" role="alert">{removeError}</p> : null}
      <CardBrowser
        scope={{ kind: 'deck', deckId }}
        basePath={basePath}
        currentSearch={currentSearch}
        onNavigate={onNavigate}
        onNavigateTo={onNavigateTo}
        reloadKey={reloadKey}
        emptyMessage="This deck has no cards yet."
        onPageLoaded={({ count }) => { pageCount.current = count; }}
        // In Favorites, un-hearting a card removes it from this deck, so the list follows at once.
        onFavoriteChange={isFavorites ? (_card, isFavorite) => { if (!isFavorite) void refreshAfterRemoval(); } : undefined}
        renderActions={canRemove ? (card) => (
          <RemoveFromDeckButton
            card={card}
            isRemoving={removingId === card.id}
            disabled={removingId !== null}
            onRemove={removeCard}
          />
        ) : undefined}
      />

      <DeckDetailsSheet deck={deck} open={detailsOpen} onOpenChange={setDetailsOpen} />
      <DeleteDeckDialog
        deck={deleteOpen ? deck : null}
        onClose={() => setDeleteOpen(false)}
        onDeleted={() => { setDeleteOpen(false); onNavigateTo('/decks'); }}
      />
    </section>
  );
}

function RemoveFromDeckButton({
  card,
  isRemoving,
  disabled,
  onRemove,
}: {
  card: Card;
  isRemoving: boolean;
  disabled: boolean;
  onRemove: (cardId: number) => void;
}) {
  return (
    <Button
      type="button"
      variant="ghost"
      size="icon"
      disabled={disabled}
      aria-label="Remove from deck"
      title={`Remove ${cardLabel(card)} from deck`}
      className="bg-surface/95 text-foreground shadow-1 size-8 rounded-full pointer-coarse:size-11"
      onClick={(event) => { event.preventDefault(); event.stopPropagation(); onRemove(card.id); }}
    >
      {isRemoving ? <LoaderCircle className="animate-spin" /> : <MinusCircle />}
    </Button>
  );
}

function DeckDetailsSheet({
  deck,
  open,
  onOpenChange,
}: {
  deck: DeckDetails;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const [members, setMembers] = React.useState<DeckDetails['cards'] | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const deckId = deck.id;

  React.useEffect(() => {
    if (!open) return;
    let active = true;
    setMembers(null);
    setError(null);
    getDeck(deckId)
      .then((full) => { if (active) setMembers(full.cards); })
      .catch((cause) => { if (active) setError(cause instanceof Error ? cause.message : 'Unable to load details.'); });
    return () => { active = false; };
  }, [open, deckId]);

  const withProperties = (members ?? []).filter(
    (card) => card.membershipProperties && Object.keys(card.membershipProperties).length > 0
  );

  return (
    <Sheet open={open} onOpenChange={onOpenChange} title="Details" side="right">
      <JsonPanel title="Properties" value={deck.properties} />
      <JsonPanel title="Metadata" value={deck.metadata} />
      <div className="grid gap-2">
        <h3 className="text-sm font-medium">Card properties in this deck</h3>
        {error ? <p className="text-destructive text-sm" role="alert">{error}</p> : null}
        {!members && !error ? <p className="text-muted-foreground text-sm">Loading...</p> : null}
        {members && withProperties.length === 0 ? <p className="text-muted-foreground text-sm">None</p> : null}
        {withProperties.map((card) => (
          <div key={card.id} className="bg-muted/50 rounded-lg p-3 text-xs">
            <div className="mb-1 truncate text-sm font-medium">{cardLabel(card)}</div>
            <dl className="space-y-1 break-words">
              {Object.entries(card.membershipProperties ?? {}).map(([key, value]) => (
                <div key={key}>
                  <dt className="inline font-medium">{key}: </dt>
                  <dd className="inline">{typeof value === 'string' ? value : JSON.stringify(value)}</dd>
                </div>
              ))}
            </dl>
          </div>
        ))}
      </div>
    </Sheet>
  );
}

function JsonPanel({
  title,
  value,
}: {
  title: string;
  value: Record<string, unknown> | null;
}) {
  return (
    <div className="grid gap-2">
      <h3 className="text-sm font-medium">{title}</h3>
      <pre className="bg-muted/50 max-h-60 overflow-auto rounded-lg p-3 text-xs">
        {JSON.stringify(value ?? {}, null, 2)}
      </pre>
    </div>
  );
}
