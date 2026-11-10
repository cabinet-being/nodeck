import * as React from 'react';

import { resolveApiUrl, type ContainedCard } from '@/api/cards';
import { usePagerInput } from '@/hooks/use-pager-input';
import { ChevronLeft, ChevronRight } from 'lucide-react';

import { stageButtonClass } from '@/components/media-stage';
import { Segmented } from '@/components/ui/segmented';

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
    <div className="grid min-w-0" role="region" aria-label="Comic reader" tabIndex={0} {...pager.containerProps}>
      <div ref={contentRef} className="grid">
        {mode === 'page' ? (
          currentPage ? (
            <div {...pager.swipeProps} className="flex min-h-[16rem] items-center justify-center overflow-hidden sm:min-h-[28rem]">
              <a href={`/cards/${currentPage.id}`} onClick={handlePageClick} aria-label={`Open page ${pageIndex + 1}${currentPage.title ? `: ${currentPage.title}` : ''}`} className="block max-w-full">
                {currentPage.contentUrl ? <img src={resolveApiUrl(currentPage.contentUrl)} alt={currentPage.title ?? `Page ${pageIndex + 1}`} className="max-h-[calc(100dvh-7rem)] max-w-full object-contain" /> : <span className="p-4 text-sm">Open page {pageIndex + 1}</span>}
              </a>
            </div>
          ) : <p className="text-stage-foreground/70 p-6 text-sm">No pages in this comic.</p>
        ) : pages.map((page, index) => (
          <a key={page.id} href={`/cards/${page.id}`} onClick={onNavigate} className="block" aria-label={`Open page ${index + 1}${page.title ? `: ${page.title}` : ''}`}>
            {page.contentUrl ? <img src={resolveApiUrl(page.contentUrl)} alt={page.title ?? `Page ${index + 1}`} className="mx-auto max-h-none w-full max-w-5xl object-contain" loading="lazy" /> : <span className="block p-4 text-sm">Open page {index + 1}</span>}
          </a>
        ))}
      </div>

      <div className="flex flex-wrap items-center justify-between gap-2 px-3 py-2">
        <Segmented
          ariaLabel="Reading mode"
          value={mode}
          onValueChange={setMode}
          options={[{ value: 'page', label: 'Single page' }, { value: 'scroll', label: 'Long scroll' }]}
        />
        {mode === 'page' ? (
          <div className="flex items-center gap-2">
            <button type="button" className={stageButtonClass} onClick={() => changePage(-1)} disabled={pageIndex === 0} aria-label="Previous page"><ChevronLeft /></button>
            <span className="text-stage-foreground/80 min-w-12 text-center text-sm tabular-nums" aria-live="polite">{pages.length ? pageIndex + 1 : 0} / {pages.length}</span>
            <button type="button" className={stageButtonClass} onClick={() => changePage(1)} disabled={pageIndex >= pages.length - 1} aria-label="Next page"><ChevronRight /></button>
          </div>
        ) : null}
      </div>

      {mode === 'page' && pages.length > 0 ? (
        <div className="flex gap-2 overflow-x-auto px-3 pb-3" aria-label="Comic pages">
          {pages.map((page, index) => (
            <button key={page.id} type="button" onClick={() => setSelectedIndex(index)} aria-label={`Page ${index + 1}${page.title ? `: ${page.title}` : ''}`} aria-current={index === pageIndex ? 'page' : undefined} className="bg-stage-foreground/10 focus-visible:ring-ring h-20 w-16 shrink-0 overflow-hidden rounded-lg opacity-70 focus-visible:ring-2 aria-[current=page]:opacity-100 aria-[current=page]:ring-2 aria-[current=page]:ring-primary">
              {page.previewUrl || page.contentUrl ? <img src={resolveApiUrl(page.previewUrl || page.contentUrl!)} alt="" className="h-full w-full object-cover" loading="lazy" /> : <span className="text-xs">{index + 1}</span>}
            </button>
          ))}
        </div>
      ) : null}
    </div>
  );
}
