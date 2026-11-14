import * as React from 'react';
import { Dialog as DialogPrimitive } from '@base-ui/react/dialog';
import { CornerDownLeft, Globe, Image, Search, Shuffle, Tag } from 'lucide-react';

import { listCards } from '@/api/cards';
import { listSources, listTags } from '@/api/catalog';
import { routes } from '@/routes';
import { cn } from '@/lib/utils';

type PaletteItem = {
  id: string;
  label: string;
  hint?: string;
  icon: React.ComponentType<{ className?: string }>;
  path: string;
  /** Runs instead of navigating to path. */
  run?: () => void;
};

type Results = { cards: PaletteItem[]; tags: PaletteItem[]; sources: PaletteItem[] };

const emptyResults: Results = { cards: [], tags: [], sources: [] };
const actions: PaletteItem[] = [...routes.primary, ...routes.library, ...routes.create].map((route) => ({
  id: `action:${route.path}`,
  label: route.label,
  icon: route.icon,
  path: route.path,
}));

export function CommandPalette({
  open,
  onOpenChange,
  onNavigateTo,
  onRandomCard,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onNavigateTo: (path: string) => void;
  onRandomCard?: () => void;
}) {
  const [query, setQuery] = React.useState('');
  const [results, setResults] = React.useState<Results>(emptyResults);
  const [active, setActive] = React.useState(0);
  const listId = React.useId();
  const trimmed = query.trim();

  React.useEffect(() => {
    if (!open) {
      setQuery('');
      setResults(emptyResults);
      setActive(0);
    }
  }, [open]);

  React.useEffect(() => {
    if (!open || !trimmed) {
      setResults(emptyResults);
      return;
    }

    let stale = false;
    const timer = window.setTimeout(() => {
      const needle = trimmed.toLowerCase();
      Promise.allSettled([
        // Pages are found through their comic or set, so the list does not repeat them.
        listCards({ search: trimmed, limit: 8, excludeContainedMedia: true }),
        listTags(trimmed),
        listSources(trimmed),
      ]).then(([cards, tags, sources]) => {
        if (stale) return;
        setResults({
          cards: cards.status === 'fulfilled'
            ? cards.value.map((card) => ({
                id: `card:${card.id}`,
                label: card.title?.trim() || `Card #${card.id}`,
                hint: card.type,
                icon: Image,
                path: `/cards/${card.id}`,
              }))
            : [],
          tags: tags.status === 'fulfilled'
            ? tags.value.filter((tag) => tag.name.toLowerCase().includes(needle) || tag.aliases.some((alias) => alias.toLowerCase().includes(needle)))
                .slice(0, 5)
                .map((tag) => ({
                  id: `tag:${tag.id}`,
                  label: tag.name,
                  hint: tag.category,
                  icon: Tag,
                  path: `/gallery?catalog_tags=${tag.id}`,
                }))
            : [],
          sources: sources.status === 'fulfilled'
            ? sources.value.slice(0, 5).map((source) => ({
                id: `source:${source.id}`,
                label: source.name,
                hint: 'Source',
                icon: Globe,
                path: `/gallery?catalog_source=${source.id}`,
              }))
            : [],
        });
        setActive(0);
      });
    }, 180);

    return () => {
      stale = true;
      window.clearTimeout(timer);
    };
  }, [open, trimmed]);

  const sections = React.useMemo(() => {
    const needle = trimmed.toLowerCase();
    const allActions: PaletteItem[] = onRandomCard
      ? [{ id: 'action:random', label: 'Random card', hint: 'Open', icon: Shuffle, path: '', run: onRandomCard }, ...actions]
      : actions;
    const matchedActions = needle
      ? allActions.filter((item) => item.label.toLowerCase().includes(needle))
      : allActions;
    const list: { title: string; items: PaletteItem[] }[] = [];
    if (trimmed) {
      list.push({
        title: 'Search',
        items: [{
          id: 'search',
          label: `Search gallery for “${trimmed}”`,
          icon: Search,
          path: `/gallery?search=${encodeURIComponent(trimmed)}`,
        }],
      });
    }
    list.push({ title: 'Cards', items: results.cards });
    list.push({ title: 'Tags', items: results.tags });
    list.push({ title: 'Sources', items: results.sources });
    list.push({ title: trimmed ? 'Go to' : 'Navigate', items: matchedActions });
    return list.filter((section) => section.items.length > 0);
  }, [trimmed, results, onRandomCard]);

  const flat = React.useMemo(() => sections.flatMap((section) => section.items), [sections]);
  const activeIndex = Math.min(active, Math.max(flat.length - 1, 0));

  function choose(item: PaletteItem | undefined) {
    if (!item) return;
    onOpenChange(false);
    if (item.run) item.run();
    else onNavigateTo(item.path);
  }

  function onKeyDown(event: React.KeyboardEvent) {
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      setActive(flat.length ? (activeIndex + 1) % flat.length : 0);
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      setActive(flat.length ? (activeIndex - 1 + flat.length) % flat.length : 0);
    } else if (event.key === 'Enter') {
      event.preventDefault();
      choose(flat[activeIndex]);
    }
  }

  React.useEffect(() => {
    document.getElementById(`${listId}-${activeIndex}`)?.scrollIntoView({ block: 'nearest' });
  }, [activeIndex, listId]);

  let index = -1;

  return (
    <DialogPrimitive.Root open={open} onOpenChange={(next) => onOpenChange(next)}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Backdrop className="fixed inset-0 z-50 bg-black/40 backdrop-blur-[2px] transition-opacity duration-[var(--dur-base)] data-[ending-style]:opacity-0 data-[starting-style]:opacity-0" />
        <DialogPrimitive.Viewport className="fixed inset-0 z-50 grid place-items-start justify-items-center overflow-y-auto p-4 pt-[12vh]">
          <DialogPrimitive.Popup
            className="bg-popover text-popover-foreground shadow-3 w-full max-w-xl overflow-hidden rounded-[var(--radius-panel)] border outline-none transition-[opacity,transform] duration-[var(--dur-base)] ease-out data-[ending-style]:scale-95 data-[ending-style]:opacity-0 data-[starting-style]:scale-95 data-[starting-style]:opacity-0"
            aria-label="Search or jump to"
          >
            <DialogPrimitive.Title className="sr-only">Search or jump to</DialogPrimitive.Title>
            <div className="flex items-center gap-3 border-b px-4">
              <Search className="text-muted-foreground size-4 shrink-0" />
              <input
                autoFocus
                role="combobox"
                aria-expanded="true"
                aria-controls={listId}
                aria-activedescendant={flat.length ? `${listId}-${activeIndex}` : undefined}
                aria-label="Search or jump to"
                placeholder="Search cards, tags, sources…"
                className="placeholder:text-muted-foreground h-12 w-full bg-transparent text-base outline-none"
                value={query}
                onChange={(event) => {
                  setQuery(event.target.value);
                  setActive(0);
                }}
                onKeyDown={onKeyDown}
              />
              <kbd className="text-muted-foreground bg-muted rounded px-1.5 py-0.5 text-[11px]">Esc</kbd>
            </div>
            <div id={listId} role="listbox" aria-label="Results" className="max-h-[60vh] overflow-y-auto p-2">
              {flat.length === 0 ? (
                <div className="text-muted-foreground px-3 py-8 text-center text-sm">No results</div>
              ) : null}
              {sections.map((section) => (
                <div key={section.title} role="group" aria-label={section.title} className="mb-1">
                  <div className="text-muted-foreground px-3 pt-2 pb-1 text-xs font-medium">{section.title}</div>
                  {section.items.map((item) => {
                    index += 1;
                    const itemIndex = index;
                    const selected = itemIndex === activeIndex;
                    return (
                      <div
                        key={item.id}
                        id={`${listId}-${itemIndex}`}
                        role="option"
                        aria-selected={selected}
                        className={cn(
                          'flex cursor-pointer items-center gap-3 rounded-lg px-3 py-2 text-sm',
                          selected && 'bg-accent text-accent-foreground'
                        )}
                        onMouseMove={() => setActive(itemIndex)}
                        onClick={() => choose(item)}
                      >
                        <item.icon className="text-muted-foreground size-4 shrink-0" />
                        <span className="min-w-0 flex-1 truncate">{item.label}</span>
                        {item.hint ? <span className="text-muted-foreground text-xs capitalize">{item.hint}</span> : null}
                        {selected ? <CornerDownLeft className="text-muted-foreground size-3.5" /> : null}
                      </div>
                    );
                  })}
                </div>
              ))}
            </div>
          </DialogPrimitive.Popup>
        </DialogPrimitive.Viewport>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  );
}
