import * as React from 'react';

import { AppShell } from '@/components/app-shell';
import { BulkImportPage } from '@/pages/bulk-import-page';
import { CardDetailsPage } from '@/pages/card-details-page';
import { CardsPage } from '@/pages/cards-page';
import { CreateCardPage } from '@/pages/create-card-page';
import { CreateDeckPage } from '@/pages/create-deck-page';
import { DeckDetailsPage } from '@/pages/deck-details-page';
import { DecksPage } from '@/pages/decks-page';
import { EditDeckPage } from '@/pages/edit-deck-page';
import { EditCardPage } from '@/pages/edit-card-page';
import { FavoritesPage } from '@/pages/favorites-page';
import { FeedPage } from '@/pages/feed-page';
import { GalleryPage } from '@/pages/gallery-page';
import { HomePage } from '@/pages/home-page';
import { SourcesPage } from '@/pages/sources-page';
import { TagsPage } from '@/pages/tags-page';

function getCurrentPath() {
  return window.location.pathname || '/';
}

function getCurrentUrl() {
  return `${window.location.pathname}${window.location.search}${window.location.hash}`;
}

export function App() {
  const [currentUrl, setCurrentUrl] = React.useState(getCurrentUrl);

  React.useEffect(() => {
    const handlePopState = () => setCurrentUrl(getCurrentUrl());

    window.addEventListener('popstate', handlePopState);

    return () => window.removeEventListener('popstate', handlePopState);
  }, []);

  const currentPath = React.useMemo(() => {
    return new URL(currentUrl, window.location.origin).pathname || '/';
  }, [currentUrl]);
  const currentSearch = React.useMemo(() => {
    return new URL(currentUrl, window.location.origin).search;
  }, [currentUrl]);

  const navigate = React.useCallback((event: React.MouseEvent<HTMLAnchorElement>) => {
    if (
      event.defaultPrevented ||
      event.button !== 0 ||
      event.metaKey ||
      event.altKey ||
      event.ctrlKey ||
      event.shiftKey
    ) {
      return;
    }

    const target = event.currentTarget;
    const nextUrl = new URL(target.href);

    if (nextUrl.origin !== window.location.origin) {
      return;
    }

    event.preventDefault();

    const nextPath = `${nextUrl.pathname}${nextUrl.search}${nextUrl.hash}`;

    if (nextPath !== currentUrl) {
      window.history.pushState(null, '', nextPath);
      setCurrentUrl(nextPath);
    }
  }, [currentUrl]);

  const navigateTo = React.useCallback((path: string) => {
    if (path !== currentUrl) {
      window.history.pushState(null, '', path);
      setCurrentUrl(path);
    }
  }, [currentUrl]);

  return (
    <AppShell currentPath={currentPath} onNavigate={navigate} onNavigateTo={navigateTo}>
      <RouteContent
        currentPath={currentPath}
        currentSearch={currentSearch}
        onNavigate={navigate}
        onNavigateTo={navigateTo}
      />
    </AppShell>
  );
}

function NotFoundPage({ onNavigate }: { onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void }) {
  return (
    <div className="flex min-h-[calc(100vh-7.5rem)] items-center justify-center p-6">
      <div className="grid gap-4 text-center">
        <div className="text-2xl font-semibold">Page not found</div>
        <div className="text-muted-foreground text-sm">The route you requested does not exist.</div>
        <div className="flex items-center justify-center gap-3">
          <a href="/gallery" onClick={onNavigate} className="text-primary text-sm font-medium">
            Gallery
          </a>
          <a href="/cards" onClick={onNavigate} className="text-primary text-sm font-medium">
            Cards
          </a>
        </div>
      </div>
    </div>
  );
}

function RouteContent({
  currentPath,
  currentSearch,
  onNavigate,
  onNavigateTo,
}: {
  currentPath: string;
  currentSearch: string;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onNavigateTo: (path: string) => void;
}) {
  if (currentPath === '/') {
    return <HomePage onNavigate={onNavigate} />;
  }

  if (currentPath === '/feed/recommended' || currentPath === '/feed/daily') {
    return <FeedPage kind={currentPath === '/feed/daily' ? 'daily' : 'recommended'}
      currentSearch={currentSearch} onNavigate={onNavigate} onNavigateTo={onNavigateTo} />;
  }

  if (currentPath === '/cards/new') {
    return <CreateCardPage onCreated={onNavigateTo} />;
  }

  if (currentPath === '/cards/import') {
    return <BulkImportPage />;
  }

  if (currentPath === '/decks/new') {
    return <CreateDeckPage onCreated={onNavigateTo} onCancel={() => onNavigateTo('/decks')} />;
  }

  if (currentPath === '/cards') {
    return <CardsPage onNavigate={onNavigate} />;
  }

  if (currentPath === '/decks') {
    return <DecksPage onNavigate={onNavigate} />;
  }

  if (currentPath === '/gallery') {
    return <GalleryPage currentSearch={currentSearch} onNavigate={onNavigate} onNavigateTo={onNavigateTo} />;
  }

  if (currentPath === '/favorites') {
    return <FavoritesPage onNavigate={onNavigate} />;
  }

  if (currentPath === '/tags') {
    return <TagsPage />;
  }

  if (currentPath === '/sources') {
    return <SourcesPage />;
  }

  const editCardMatch = currentPath.match(/^\/cards\/(\d+)\/edit$/);

  if (editCardMatch) {
    return <EditCardPage cardId={Number(editCardMatch[1])} onSaved={onNavigateTo} />;
  }

  const editDeckMatch = currentPath.match(/^\/decks\/(\d+)\/edit$/);

  if (editDeckMatch) {
    return (
      <EditDeckPage
        deckId={Number(editDeckMatch[1])}
        onSaved={onNavigateTo}
        onCancel={() => onNavigateTo(`/decks/${editDeckMatch[1]}`)}
      />
    );
  }

  const cardMatch = currentPath.match(/^\/cards\/(\d+)$/);

  if (cardMatch) {
    return <CardDetailsPage cardId={Number(cardMatch[1])} onNavigate={onNavigate} />;
  }

  const deckMatch = currentPath.match(/^\/decks\/(\d+)$/);

  if (deckMatch) {
    return <DeckDetailsPage deckId={Number(deckMatch[1])} currentSearch={currentSearch} onNavigate={onNavigate} onNavigateTo={onNavigateTo} />;
  }

  return <NotFoundPage onNavigate={onNavigate} />;
}
