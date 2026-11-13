import * as React from 'react';

import { getFavoritesDeck } from '@/api/decks';
import { Button } from '@/components/ui/button';
import { DeckDetailsPage } from '@/pages/deck-details-page';

/** Favorites is the system favorites deck shown in the regular deck view, at its own address. */
export function FavoritesPage({
  currentSearch,
  onNavigate,
  onNavigateTo,
}: {
  currentSearch: string;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onNavigateTo: (path: string) => void;
}) {
  const [deckId, setDeckId] = React.useState<number | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [retryKey, setRetryKey] = React.useState(0);

  React.useEffect(() => {
    let active = true;
    setError(null);
    getFavoritesDeck()
      .then((deck) => { if (active) setDeckId(deck.id); })
      .catch((cause) => { if (active) setError(cause instanceof Error ? cause.message : 'Unable to load favorites.'); });
    return () => { active = false; };
  }, [retryKey]);

  if (error) {
    return (
      <div className="flex flex-wrap items-center gap-3 p-6" role="alert">
        <p className="text-destructive text-sm">{error}</p>
        <Button type="button" size="sm" variant="outline" onClick={() => setRetryKey((key) => key + 1)}>Try again</Button>
      </div>
    );
  }
  if (deckId === null) return <div className="text-muted-foreground p-6 text-sm">Loading...</div>;

  return <DeckDetailsPage deckId={deckId} basePath="/favorites" currentSearch={currentSearch}
    onNavigate={onNavigate} onNavigateTo={onNavigateTo} />;
}
