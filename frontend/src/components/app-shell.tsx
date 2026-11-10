import * as React from 'react';
import { ChevronDown, Ellipsis, Plus, Search } from 'lucide-react';

import { CommandPalette } from '@/components/command-palette';
import { ThemeToggle, useTheme } from '@/components/theme';
import { Button } from '@/components/ui/button';
import { Menu, MenuContent, MenuItem, MenuLabel, MenuTrigger } from '@/components/ui/menu';
import { Segmented } from '@/components/ui/segmented';
import { Sheet } from '@/components/ui/sheet';
import { TooltipProvider } from '@/components/ui/tooltip';
import { cn } from '@/lib/utils';
import { isRouteActive, routes } from '@/routes';

type ShellProps = {
  currentPath: string;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onNavigateTo: (path: string) => void;
  children: React.ReactNode;
};

function Wordmark({ onNavigate }: { onNavigate: ShellProps['onNavigate'] }) {
  return (
    <a href="/" onClick={onNavigate} aria-label="no·deck home" className="flex items-center rounded-md text-lg font-semibold tracking-tight">
      no
      <span aria-hidden="true" className="bg-primary mx-[3px] size-2 rounded-full" />
      deck
    </a>
  );
}

function AddMenu({ onNavigateTo, compact }: { onNavigateTo: (path: string) => void; compact?: boolean }) {
  return (
    <Menu>
      <MenuTrigger
        render={
          compact ? (
            <Button type="button" size="icon" aria-label="Add" />
          ) : (
            <Button type="button" aria-label="Add" />
          )
        }
      >
        <Plus />
        {compact ? null : (
          <>
            Add
            <ChevronDown className="size-3.5 opacity-70" />
          </>
        )}
      </MenuTrigger>
      <MenuContent>
        {routes.create.map((route) => (
          <MenuItem key={route.path} onClick={() => onNavigateTo(route.path)}>
            <route.icon />
            {route.label}
          </MenuItem>
        ))}
      </MenuContent>
    </Menu>
  );
}

function TopBar({
  currentPath,
  onNavigate,
  onNavigateTo,
  onOpenPalette,
}: Omit<ShellProps, 'children'> & { onOpenPalette: () => void }) {
  const libraryActive = routes.library.some((route) => isRouteActive(currentPath, route.path));
  const isMac = typeof navigator !== 'undefined' && /mac|iphone|ipad/i.test(navigator.platform);

  return (
    <header className="bg-background/80 sticky top-0 z-40 h-14 border-b backdrop-blur-md">
      <div className="mx-auto flex h-full max-w-screen-2xl items-center gap-2 px-4 sm:px-6">
        <Wordmark onNavigate={onNavigate} />

        <nav aria-label="Primary" className="ml-4 hidden items-center gap-1 md:flex">
          {routes.primary.map((route) => {
            const active = isRouteActive(currentPath, route.path);
            return (
              <a
                key={route.path}
                href={route.path}
                onClick={onNavigate}
                aria-current={active ? 'page' : undefined}
                className={cn(
                  'rounded-[var(--radius-button)] px-3 py-1.5 text-sm font-medium transition-colors duration-[var(--dur-fast)]',
                  active ? 'bg-accent text-accent-foreground' : 'text-muted-foreground hover:bg-muted hover:text-foreground'
                )}
              >
                {route.label}
              </a>
            );
          })}
          <Menu>
            <MenuTrigger
              className={cn(
                'inline-flex items-center gap-1 rounded-[var(--radius-button)] px-3 py-1.5 text-sm font-medium transition-colors duration-[var(--dur-fast)]',
                libraryActive ? 'bg-accent text-accent-foreground' : 'text-muted-foreground hover:bg-muted hover:text-foreground'
              )}
            >
              Library
              <ChevronDown className="size-3.5 opacity-70" />
            </MenuTrigger>
            <MenuContent align="start">
              {routes.library.map((route) => (
                <MenuItem key={route.path} onClick={() => onNavigateTo(route.path)}>
                  <route.icon />
                  {route.label}
                </MenuItem>
              ))}
            </MenuContent>
          </Menu>
        </nav>

        <div className="flex flex-1 justify-center px-2">
          <button
            type="button"
            onClick={onOpenPalette}
            className="bg-surface text-muted-foreground hover:bg-muted hidden h-9 w-full max-w-md items-center gap-2 rounded-full border px-3.5 text-sm shadow-1 transition-colors duration-[var(--dur-fast)] md:flex"
          >
            <Search className="size-4" />
            <span className="flex-1 truncate text-left">Search or jump to…</span>
            <kbd className="bg-muted rounded px-1.5 py-0.5 text-[11px]">{isMac ? '⌘K' : 'Ctrl K'}</kbd>
          </button>
        </div>

        <div className="ml-auto flex items-center gap-1 md:ml-0">
          <Button type="button" variant="ghost" size="icon" className="md:hidden" aria-label="Search" onClick={onOpenPalette}>
            <Search />
          </Button>
          <AddMenu onNavigateTo={onNavigateTo} compact={false} />
          <ThemeToggle className="hidden md:inline-flex" />
        </div>
      </div>
    </header>
  );
}

function MobileTabBar({
  currentPath,
  onNavigate,
  onNavigateTo,
}: Omit<ShellProps, 'children'>) {
  const [moreOpen, setMoreOpen] = React.useState(false);
  const { theme, setTheme } = useTheme();
  const moreActive = routes.library.some((route) => isRouteActive(currentPath, route.path));

  return (
    <>
      <nav
        aria-label="Tabs"
        className="bg-background/90 fixed inset-x-0 bottom-0 z-40 border-t pb-[env(safe-area-inset-bottom)] backdrop-blur-md md:hidden"
      >
        <ul className="grid grid-cols-5">
          {routes.primary.map((route) => {
            const active = isRouteActive(currentPath, route.path);
            return (
              <li key={route.path}>
                <a
                  href={route.path}
                  onClick={onNavigate}
                  aria-current={active ? 'page' : undefined}
                  className={cn(
                    'flex h-14 flex-col items-center justify-center gap-0.5 text-[11px] font-medium',
                    active ? 'text-primary' : 'text-muted-foreground'
                  )}
                >
                  <route.icon className="size-5" />
                  {route.label}
                </a>
              </li>
            );
          })}
          <li>
            <button
              type="button"
              onClick={() => setMoreOpen(true)}
              className={cn(
                'flex h-14 w-full flex-col items-center justify-center gap-0.5 text-[11px] font-medium',
                moreActive ? 'text-primary' : 'text-muted-foreground'
              )}
            >
              <Ellipsis className="size-5" />
              More
            </button>
          </li>
        </ul>
      </nav>

      <Sheet open={moreOpen} onOpenChange={setMoreOpen} title="More">
        <div className="grid gap-1">
          {routes.library.map((route) => (
            <button
              key={route.path}
              type="button"
              className="hover:bg-muted flex items-center gap-3 rounded-lg px-3 py-3 text-left text-sm font-medium"
              onClick={() => {
                setMoreOpen(false);
                onNavigateTo(route.path);
              }}
            >
              <route.icon className="text-muted-foreground size-4" />
              {route.label}
            </button>
          ))}
        </div>
        <div className="grid gap-2">
          <MenuLabel className="px-0">Theme</MenuLabel>
          <Segmented
            ariaLabel="Theme"
            value={theme}
            onValueChange={setTheme}
            options={[
              { value: 'system', label: 'System' },
              { value: 'light', label: 'Light' },
              { value: 'dark', label: 'Dark' },
            ]}
          />
        </div>
      </Sheet>
    </>
  );
}

export function AppShell({ currentPath, onNavigate, onNavigateTo, children }: ShellProps) {
  const [paletteOpen, setPaletteOpen] = React.useState(false);

  React.useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault();
        setPaletteOpen((open) => !open);
      }
    }
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, []);

  return (
    <TooltipProvider>
      <div className="text-foreground min-h-screen">
        <TopBar
          currentPath={currentPath}
          onNavigate={onNavigate}
          onNavigateTo={onNavigateTo}
          onOpenPalette={() => setPaletteOpen(true)}
        />
        <main className="min-w-0 pb-[calc(3.5rem+env(safe-area-inset-bottom))] md:pb-0">{children}</main>
        <MobileTabBar currentPath={currentPath} onNavigate={onNavigate} onNavigateTo={onNavigateTo} />
        <CommandPalette open={paletteOpen} onOpenChange={setPaletteOpen} onNavigateTo={onNavigateTo} />
      </div>
    </TooltipProvider>
  );
}
