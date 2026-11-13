import * as React from 'react';
import { ChevronLeft, ChevronRight } from 'lucide-react';

import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { cn } from '@/lib/utils';

/** Page numbers to show around the current one; `null` marks a gap (…). */
export function pageWindow(page: number, pageCount: number, siblings = 1): (number | null)[] {
  // First, last, current ± siblings and the two gaps fit in this many slots.
  const slots = siblings * 2 + 5;
  if (pageCount <= slots) return Array.from({ length: pageCount }, (_, index) => index + 1);
  const start = Math.max(2, Math.min(page - siblings, pageCount - siblings * 2 - 2));
  const end = Math.min(pageCount - 1, Math.max(page + siblings, siblings * 2 + 3));
  const pages: (number | null)[] = [1];
  if (start > 2) pages.push(null);
  for (let value = start; value <= end; value++) pages.push(value);
  if (end < pageCount - 1) pages.push(null);
  pages.push(pageCount);
  return pages;
}

export function pageCountOf(total: number, pageSize: number) {
  return Math.max(1, Math.ceil(total / pageSize));
}

/**
 * Numbered page selector: previous/next, page numbers with gaps, and a "go to page"
 * field for long lists. Hidden when everything fits on one page.
 */
export function Pagination({
  page,
  pageCount,
  onPageChange,
  disabled = false,
  total,
  className,
}: {
  /** 1-based current page. */
  page: number;
  pageCount: number;
  onPageChange: (page: number) => void;
  disabled?: boolean;
  /** Number of items, shown as context next to the selector. */
  total?: number;
  className?: string;
}) {
  const [draft, setDraft] = React.useState('');
  if (pageCount <= 1) return null;
  const current = Math.min(Math.max(1, page), pageCount);

  function go(target: number) {
    const next = Math.min(Math.max(1, target), pageCount);
    if (next !== current) onPageChange(next);
  }

  function submit(event: React.FormEvent) {
    event.preventDefault();
    const value = Number(draft);
    if (Number.isInteger(value)) go(value);
    setDraft('');
  }

  const numberClass = 'h-9 min-w-9 px-2 tabular-nums pointer-coarse:h-11 pointer-coarse:min-w-11';

  return (
    <nav aria-label="Pages" className={cn('flex flex-wrap items-center justify-center gap-x-3 gap-y-2 py-2', className)}>
      <div className="flex items-center gap-1">
        <Button type="button" variant="ghost" size="icon" aria-label="Previous page" title="Previous page"
          disabled={disabled || current <= 1} onClick={() => go(current - 1)}>
          <ChevronLeft className="size-4" />
        </Button>
        {/* Fewer numbers on narrow screens so the row never wraps mid-sequence. */}
        <ol className="flex items-center gap-1 sm:hidden">
          {pageWindow(current, pageCount, 0).map((value, index) => (
            <PageItem key={value ?? `gap-${index}`} value={value} current={current} disabled={disabled}
              className={numberClass} onSelect={go} />
          ))}
        </ol>
        <ol className="hidden items-center gap-1 sm:flex">
          {pageWindow(current, pageCount, 1).map((value, index) => (
            <PageItem key={value ?? `gap-${index}`} value={value} current={current} disabled={disabled}
              className={numberClass} onSelect={go} />
          ))}
        </ol>
        <Button type="button" variant="ghost" size="icon" aria-label="Next page" title="Next page"
          disabled={disabled || current >= pageCount} onClick={() => go(current + 1)}>
          <ChevronRight className="size-4" />
        </Button>
      </div>
      {pageCount > 7 ? (
        <form onSubmit={submit} className="flex items-center gap-2 text-sm">
          <label htmlFor="pagination-go" className="text-muted-foreground">Page</label>
          <Input id="pagination-go" type="number" inputMode="numeric" min={1} max={pageCount}
            placeholder={String(current)} value={draft} disabled={disabled}
            onChange={(event) => setDraft(event.target.value)}
            className="h-9 w-20 tabular-nums" aria-label={`Go to page (1–${pageCount})`} />
          <span className="text-muted-foreground tabular-nums">of {pageCount}</span>
          <Button type="submit" variant="outline" size="sm" disabled={disabled || draft === ''}>Go</Button>
        </form>
      ) : null}
      {total !== undefined ? (
        <span className="text-muted-foreground text-xs tabular-nums">{total.toLocaleString()} total</span>
      ) : null}
    </nav>
  );
}

function PageItem({
  value,
  current,
  disabled,
  className,
  onSelect,
}: {
  value: number | null;
  current: number;
  disabled: boolean;
  className: string;
  onSelect: (page: number) => void;
}) {
  if (value === null) return <li aria-hidden="true" className="text-muted-foreground px-1">…</li>;
  const active = value === current;
  return (
    <li>
      <Button type="button" variant={active ? 'default' : 'ghost'} className={className}
        aria-label={`Page ${value}`} aria-current={active ? 'page' : undefined}
        disabled={disabled && !active} onClick={() => onSelect(value)}>
        {value}
      </Button>
    </li>
  );
}

/** Scrolls to the top when the page changes (not on mount), so a new page starts at its first item. */
export function usePageScroll(page: number) {
  const previous = React.useRef(page);
  React.useEffect(() => {
    if (previous.current === page) return;
    previous.current = page;
    window.scrollTo({ top: 0, behavior: 'auto' });
  }, [page]);
}

/** Goes back to page 1 when any of `deps` changes (a new search or filter), not on mount. */
export function useResetPage(setPage: (page: number) => void, deps: React.DependencyList) {
  // Compared by value, so StrictMode's repeated mount effects do not count as a change.
  const key = JSON.stringify(deps);
  const previous = React.useRef(key);
  React.useEffect(() => {
    if (previous.current === key) return;
    previous.current = key;
    setPage(1);
  }, [key, setPage]);
}

/** Moves to the last page when the current one no longer exists (after deleting or filtering). */
export function useClampPage(page: number, pageCount: number, loaded: boolean, setPage: (page: number) => void) {
  React.useEffect(() => {
    if (loaded && page > pageCount) setPage(pageCount);
  }, [loaded, page, pageCount, setPage]);
}
