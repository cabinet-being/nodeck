import * as React from 'react';
import { Menu, Sparkles } from 'lucide-react';

import { buttonVariants } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { routes, type RouteItem } from '@/routes';

type AppSidebarProps = {
  currentPath: string;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
};

export function AppSidebar({ currentPath, onNavigate }: AppSidebarProps) {
  const mobileMenuRef = React.useRef<HTMLDetailsElement>(null);

  function navigateFromMenu(event: React.MouseEvent<HTMLAnchorElement>) {
    mobileMenuRef.current?.removeAttribute('open');
    onNavigate(event);
  }

  return (
    <aside className="border-sidebar-border bg-sidebar text-sidebar-foreground sticky top-0 z-20 flex h-16 items-center border-b md:static md:h-auto md:min-h-screen md:flex-col md:items-stretch md:border-b-0 md:border-r">
      <a
        href="/"
        onClick={navigateFromMenu}
        className="flex h-16 flex-1 items-center gap-2 px-5 text-base font-semibold md:flex-none md:border-b"
        aria-current={currentPath === '/' ? 'page' : undefined}
      >
        <span className="bg-primary text-primary-foreground flex size-8 items-center justify-center rounded-lg">
          <Sparkles className="size-4" />
        </span>
        Nodeck
      </a>

      <details ref={mobileMenuRef} className="md:hidden">
        <summary className="flex size-12 cursor-pointer list-none items-center justify-center rounded-lg" aria-label="Open navigation menu">
          <Menu className="size-5" />
        </summary>
        <nav className="border-sidebar-border bg-sidebar absolute left-0 top-16 grid max-h-[calc(100vh-4rem)] w-full gap-3 overflow-y-auto border-b p-3 shadow-lg"
          aria-label="Mobile navigation">
          <NavGroup label="Core" routes={routes.filter((route) => route.group === 'core')}
            currentPath={currentPath} onNavigate={navigateFromMenu} />
          <NavGroup label="Create" routes={routes.filter((route) => route.group === 'create')}
            currentPath={currentPath} onNavigate={navigateFromMenu} />
          <NavGroup label="Service" routes={routes.filter((route) => route.group === 'service')}
            currentPath={currentPath} onNavigate={navigateFromMenu} />
        </nav>
      </details>

      <nav className="hidden flex-1 flex-col gap-6 px-3 py-4 md:flex" aria-label="Primary navigation">
        <NavGroup
          label="Core"
          routes={routes.filter((route) => route.group === 'core')}
          currentPath={currentPath}
          onNavigate={onNavigate}
        />
        <NavGroup
          label="Create"
          routes={routes.filter((route) => route.group === 'create')}
          currentPath={currentPath}
          onNavigate={onNavigate}
        />
        <NavGroup
          label="Service"
          routes={routes.filter((route) => route.group === 'service')}
          currentPath={currentPath}
          onNavigate={onNavigate}
        />
      </nav>
    </aside>
  );
}

function NavGroup({
  label,
  routes: groupRoutes,
  currentPath,
  onNavigate,
}: {
  label: string;
  routes: RouteItem[];
  currentPath: string;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  return (
    <div className="flex flex-col gap-1">
      <div className="text-muted-foreground px-2 py-1 text-xs font-medium uppercase tracking-normal">
        {label}
      </div>
      {groupRoutes.map((route) => {
        const Icon = route.icon;
        const isActive = currentPath === route.path;

        return (
          <a
            key={route.path}
            href={route.path}
            onClick={onNavigate}
            aria-current={isActive ? 'page' : undefined}
            className={cn(
              buttonVariants({ variant: isActive ? 'secondary' : 'ghost' }),
              'h-9 justify-start gap-2 px-2 text-sidebar-foreground'
            )}
          >
            <Icon className="size-4" />
            {route.label}
          </a>
        );
      })}
    </div>
  );
}
