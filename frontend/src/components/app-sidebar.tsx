import { Sparkles } from 'lucide-react';

import { buttonVariants } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { routes, type RouteItem } from '@/routes';

type AppSidebarProps = {
  currentPath: string;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
};

export function AppSidebar({ currentPath, onNavigate }: AppSidebarProps) {
  return (
    <aside className="border-sidebar-border bg-sidebar text-sidebar-foreground flex min-h-screen flex-col border-r">
      <a
        href="/"
        onClick={onNavigate}
        className="flex h-16 items-center gap-2 border-b px-5 text-base font-semibold"
        aria-current={currentPath === '/' ? 'page' : undefined}
      >
        <span className="bg-primary text-primary-foreground flex size-8 items-center justify-center rounded-lg">
          <Sparkles className="size-4" />
        </span>
        Nodeck
      </a>

      <nav className="flex flex-1 flex-col gap-6 px-3 py-4" aria-label="Primary navigation">
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
