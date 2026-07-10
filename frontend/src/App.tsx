import * as React from 'react';
import { Home, Search } from 'lucide-react';

import { AppSidebar } from '@/components/app-sidebar';
import { Badge } from '@/components/ui/badge';
import { buttonVariants } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { cn } from '@/lib/utils';
import { routeDetails, routeTitles } from '@/routes';

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

  const title = routeTitles.get(currentPath) ?? 'Not Found';
  const routeDetail = routeDetails.get(currentPath) ?? {
    eyebrow: 'Missing',
    title,
    summary: 'No matching Nodeck route exists for this URL.',
    items: ['Use the sidebar', 'Return home', 'Choose a known route'],
  };

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

          <main className="min-w-0 flex-1 p-6">
            <section className="mx-auto flex w-full max-w-5xl flex-col gap-5">
              <div className="flex items-center justify-between gap-3">
                <div>
                  <Badge variant="outline">{routeDetail.eyebrow}</Badge>
                  <h1 className="mt-3 text-2xl font-semibold tracking-normal">
                    {routeDetail.title}
                  </h1>
                </div>
                {currentPath !== '/' ? (
                  <a
                    href="/"
                    onClick={navigate}
                    className={cn(buttonVariants({ variant: 'outline' }), 'gap-2')}
                  >
                    <Home className="size-4" />
                    Home
                  </a>
                ) : null}
              </div>

              <Card>
                <CardHeader>
                  <CardTitle>{routeDetail.summary}</CardTitle>
                </CardHeader>
                <CardContent className="grid gap-3 sm:grid-cols-3">
                  {routeDetail.items.map((item) => (
                    <div
                      key={item}
                      className="border-border bg-muted/30 rounded-lg border px-3 py-2 text-sm"
                    >
                      {item}
                    </div>
                  ))}
                </CardContent>
              </Card>
            </section>
          </main>
        </div>
      </div>
    </div>
  );
}
