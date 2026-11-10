import * as React from 'react';

import { resolveApiUrl, type ContainedCard } from '@/api/cards';
import { usePagerInput } from '@/hooks/use-pager-input';
import { Button } from '@/components/ui/button';

export function ComicReader({
  pages,
  contentRef,
  onNavigate,
}: {
  pages: ContainedCard[];
  contentRef: React.Ref<HTMLDivElement>;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const [mode, setMode] = React.useState<'page' | 'scroll'>('page');
  const [selectedIndex, setSelectedIndex] = React.useState(0);
  const pageIndex = Math.min(selectedIndex, Math.max(0, pages.length - 1));
  const currentPage = pages[pageIndex];

  function changePage(delta: number) {
    setSelectedIndex((index) => Math.max(0, Math.min(pages.length - 1, index + delta)));
  }

  const pager = usePagerInput(changePage, mode === 'page');

  function handlePageClick(event: React.MouseEvent<HTMLAnchorElement>) {
    if (pager.consumeSwipeClick()) {
      event.preventDefault();
      return;
    }
    onNavigate(event);
  }

  return (
    <div className="grid min-w-0 gap-4" role="region" aria-label="Comic reader" tabIndex={0} {...pager.containerProps}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex gap-2" role="group" aria-label="Reading mode">
          <Button type="button" size="sm" variant={mode === 'page' ? 'default' : 'outline'} aria-pressed={mode === 'page'} onClick={() => setMode('page')}>Single page</Button>
          <Button type="button" size="sm" variant={mode === 'scroll' ? 'default' : 'outline'} aria-pressed={mode === 'scroll'} onClick={() => setMode('scroll')}>Long scroll</Button>
        </div>
        {mode === 'page' ? (
          <div className="flex items-center gap-3">
            <Button type="button" size="sm" variant="outline" onClick={() => changePage(-1)} disabled={pageIndex === 0} aria-label="Previous page">Previous</Button>
            <span className="text-muted-foreground text-sm" aria-live="polite">{pages.length ? pageIndex + 1 : 0} / {pages.length}</span>
            <Button type="button" size="sm" variant="outline" onClick={() => changePage(1)} disabled={pageIndex >= pages.length - 1} aria-label="Next page">Next</Button>
          </div>
        ) : null}
      </div>

      <div ref={contentRef} className="grid gap-4">
        {mode === 'page' ? (
          currentPage ? (
            <div
              {...pager.swipeProps}
              className="bg-muted flex min-h-[28rem] items-center justify-center overflow-hidden rounded-lg border"
            >
              <a href={`/cards/${currentPage.id}`} onClick={handlePageClick} aria-label={`Open page ${pageIndex + 1}${currentPage.title ? `: ${currentPage.title}` : ''}`} className="block max-w-full">
                {currentPage.contentUrl ? <img src={resolveApiUrl(currentPage.contentUrl)} alt={currentPage.title ?? `Page ${pageIndex + 1}`} className="max-h-[calc(100dvh-8rem)] max-w-full object-contain" /> : <span className="p-4 text-sm">Open page {pageIndex + 1}</span>}
              </a>
            </div>
          ) : <p className="text-muted-foreground text-sm">No pages in this comic.</p>
        ) : pages.map((page, index) => (
          <a key={page.id} href={`/cards/${page.id}`} onClick={onNavigate} className="bg-muted block overflow-hidden rounded-lg border" aria-label={`Open page ${index + 1}${page.title ? `: ${page.title}` : ''}`}>
            {page.contentUrl ? <img src={resolveApiUrl(page.contentUrl)} alt={page.title ?? `Page ${index + 1}`} className="mx-auto max-h-none w-full max-w-5xl object-contain" loading="lazy" /> : <span className="block p-4 text-sm">Open page {index + 1}</span>}
          </a>
        ))}
      </div>

      {mode === 'page' && pages.length > 0 ? (
        <div className="flex gap-2 overflow-x-auto pb-2" aria-label="Comic pages">
          {pages.map((page, index) => (
            <button key={page.id} type="button" onClick={() => setSelectedIndex(index)} aria-label={`Page ${index + 1}${page.title ? `: ${page.title}` : ''}`} aria-current={index === pageIndex ? 'page' : undefined} className="bg-muted h-20 w-16 shrink-0 overflow-hidden rounded border aria-[current=page]:ring-2 aria-[current=page]:ring-primary">
              {page.previewUrl || page.contentUrl ? <img src={resolveApiUrl(page.previewUrl || page.contentUrl!)} alt="" className="h-full w-full object-cover" loading="lazy" /> : <span className="text-xs">{index + 1}</span>}
            </button>
          ))}
        </div>
      ) : null}
    </div>
  );
}
