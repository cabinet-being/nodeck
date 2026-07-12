import * as React from 'react';
import { Search } from 'lucide-react';

import { AppSidebar } from '@/components/app-sidebar';
import { Input } from '@/components/ui/input';
import { CardDetailsPage } from '@/pages/card-details-page';
import { CardsPage } from '@/pages/cards-page';
import { CreateCardPage } from '@/pages/create-card-page';
import { EditCardPage } from '@/pages/edit-card-page';
import { GalleryPage } from '@/pages/gallery-page';
import { SourcesPage } from '@/pages/sources-page';
import { TagsPage } from '@/pages/tags-page';

function getCurrentPath() {
  return window.location.pathname || '/';
}

export function App() {
  const [currentPath, setCurrentPath] = React.useState(getCurrentPath);

  React.useEffect(() => {
    const handlePopState = () => setCurrentPath(getCurrentPath());

    window.addEventListener('popstate', handlePopState);

    return () => window.removeEventListener('popstate', handlePopState);
  }, []);

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

    if (nextUrl.pathname !== currentPath) {
      window.history.pushState(null, '', nextUrl.pathname);
      setCurrentPath(nextUrl.pathname);
    }
  }, [currentPath]);

  const navigateTo = React.useCallback((path: string) => {
    if (path !== currentPath) {
      window.history.pushState(null, '', path);
      setCurrentPath(path);
    }
  }, [currentPath]);

  return (
    <div className="min-h-screen bg-background text-foreground">
      <div className="grid min-h-screen grid-cols-[17rem_1fr]">
        <AppSidebar currentPath={currentPath} onNavigate={navigate} />

        <div className="flex min-w-0 flex-col">
          <header className="bg-background/95 sticky top-0 z-10 flex h-16 items-center border-b px-6 backdrop-blur">
            <div className="relative w-full max-w-xl">
              <Search className="text-muted-foreground pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2" />
              <Input
                type="search"
                aria-label="Global search"
                placeholder="Search Nodeck"
                className="pl-9"
              />
            </div>
          </header>

          <main className="min-w-0 flex-1">
            <RouteContent
              currentPath={currentPath}
              onNavigate={navigate}
              onNavigateTo={navigateTo}
            />
          </main>
        </div>
      </div>
    </div>
  );
}

function RouteContent({
  currentPath,
  onNavigate,
  onNavigateTo,
}: {
  currentPath: string;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onNavigateTo: (path: string) => void;
}) {
  if (currentPath === '/cards/new') {
    return <CreateCardPage onCreated={onNavigateTo} />;
  }

  if (currentPath === '/cards') {
    return <CardsPage onNavigate={onNavigate} />;
  }

  if (currentPath === '/gallery') {
    return <GalleryPage onNavigate={onNavigate} />;
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

  const cardMatch = currentPath.match(/^\/cards\/(\d+)$/);

  if (cardMatch) {
    return <CardDetailsPage cardId={Number(cardMatch[1])} />;
  }

  return null;
}
