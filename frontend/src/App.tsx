import * as React from 'react';

import { Compass } from 'lucide-react';

import { AppShell } from '@/components/app-shell';
import { EmptyState } from '@/components/empty-state';
import { buttonVariants } from '@/components/ui/button';
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
    <EmptyState
      icon={Compass}
      title="Page not found"
      className="min-h-[calc(100vh-7.5rem)]"
      action={(
        <a href="/" onClick={onNavigate} className={buttonVariants({ variant: 'outline' })}>
          Go home
        </a>
      )}
    />
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
    return <TagsPage onNavigate={onNavigate} />;
  }

  if (currentPath === '/sources') {
    return <SourcesPage onNavigate={onNavigate} />;
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
    return <CardDetailsPage cardId={Number(cardMatch[1])} onNavigate={onNavigate} onNavigateTo={onNavigateTo} />;
  }

  const deckMatch = currentPath.match(/^\/decks\/(\d+)$/);

  if (deckMatch) {
    return <DeckDetailsPage deckId={Number(deckMatch[1])} currentSearch={currentSearch} onNavigate={onNavigate} onNavigateTo={onNavigateTo} />;
  }

  return <NotFoundPage onNavigate={onNavigate} />;
}
