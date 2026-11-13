import * as React from 'react';
import { ArrowDown, ArrowUp, GripVertical, Trash2 } from 'lucide-react';

import { resolveApiUrl, type Card } from '@/api/cards';
import { cardLabel } from '@/components/card-tile';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';

export type SortableCard = Pick<Card, 'id' | 'type' | 'title' | 'previewUrl'>;

/**
 * Ordered list of cards (the page order of a comic or set). Rows are dragged by their handle
 * with a mouse, pen or finger; the arrow buttons do the same from the keyboard.
 */
export function SortableCardList<T extends SortableCard>({
  items,
  onChange,
  disabled = false,
  label = 'Pages',
  renderNote,
}: {
  items: T[];
  onChange: (items: T[]) => void;
  disabled?: boolean;
  /** Accessible name of the list. */
  label?: string;
  /** Optional line under a row's title, such as a warning. */
  renderNote?: (item: T) => React.ReactNode;
}) {
  const [dragId, setDragId] = React.useState<number | null>(null);
  const rowRefs = React.useRef(new Map<number, HTMLLIElement>());
  const itemsRef = React.useRef(items);
  itemsRef.current = items;

  function move(from: number, to: number) {
    if (to < 0 || to >= items.length || from === to) return;
    const next = [...items];
    const [item] = next.splice(from, 1);
    next.splice(to, 0, item);
    onChange(next);
  }

  function startDrag(event: React.PointerEvent<HTMLButtonElement>, id: number) {
    if (disabled || event.button !== 0) return;
    event.preventDefault();
    event.currentTarget.setPointerCapture(event.pointerId);
    setDragId(id);
  }

  function dragMove(event: React.PointerEvent<HTMLButtonElement>) {
    if (dragId === null) return;
    const current = itemsRef.current;
    const from = current.findIndex((item) => item.id === dragId);
    if (from < 0) return;
    // The target slot is the number of other rows whose middle lies above the pointer.
    let to = 0;
    for (const item of current) {
      if (item.id === dragId) continue;
      const rect = rowRefs.current.get(item.id)?.getBoundingClientRect();
      if (rect && event.clientY > rect.top + rect.height / 2) to += 1;
    }
    if (to !== from) {
      const next = [...current];
      const [dragged] = next.splice(from, 1);
      next.splice(to, 0, dragged);
      itemsRef.current = next;
      onChange(next);
    }
  }

  function endDrag() {
    setDragId(null);
  }

  if (items.length === 0) return null;

  return (
    <ol className="grid gap-2" aria-label={label}>
      {items.map((item, index) => {
        const name = cardLabel(item);
        return (
          <li
            key={item.id}
            ref={(element) => {
              if (element) rowRefs.current.set(item.id, element);
              else rowRefs.current.delete(item.id);
            }}
            className={cn(
              'bg-surface flex items-center gap-2 rounded-xl border p-2 transition-shadow',
              dragId === item.id && 'shadow-2 ring-primary relative z-10 ring-2'
            )}
          >
            <button
              type="button"
              aria-label={`Drag ${name} to reorder`}
              title="Drag to reorder"
              disabled={disabled}
              onPointerDown={(event) => startDrag(event, item.id)}
              onPointerMove={dragMove}
              onPointerUp={endDrag}
              onPointerCancel={endDrag}
              onLostPointerCapture={endDrag}
              className="text-muted-foreground hover:text-foreground flex h-11 w-8 shrink-0 cursor-grab touch-none items-center justify-center rounded-lg active:cursor-grabbing disabled:opacity-50"
            >
              <GripVertical className="size-4" />
            </button>
            <span className="text-muted-foreground w-6 shrink-0 text-right text-sm tabular-nums">{index + 1}</span>
            {item.previewUrl
              ? <img src={resolveApiUrl(item.previewUrl)} alt="" draggable={false} className="size-12 shrink-0 rounded-lg object-cover" />
              : <div className="bg-muted size-12 shrink-0 rounded-lg" />}
            <div className="min-w-0 flex-1">
              <div className="truncate text-sm">{name}</div>
              {renderNote?.(item)}
            </div>
            <Button type="button" variant="ghost" size="icon" disabled={disabled || index === 0}
              aria-label={`Move ${name} up`} onClick={() => move(index, index - 1)} className="max-sm:hidden">
              <ArrowUp className="size-4" />
            </Button>
            <Button type="button" variant="ghost" size="icon" disabled={disabled || index === items.length - 1}
              aria-label={`Move ${name} down`} onClick={() => move(index, index + 1)} className="max-sm:hidden">
              <ArrowDown className="size-4" />
            </Button>
            <Button type="button" variant="ghost" size="icon" disabled={disabled}
              aria-label={`Remove ${name}`} onClick={() => onChange(items.filter((other) => other.id !== item.id))}>
              <Trash2 className="size-4" />
            </Button>
          </li>
        );
      })}
    </ol>
  );
}
