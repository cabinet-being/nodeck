import * as React from 'react';

import { containedVideoUrl, resolveApiUrl, type ContainedCard } from '@/api/cards';
import { usePagerInput } from '@/hooks/use-pager-input';
import { parsePositiveInt, useUrlState } from '@/hooks/use-url-state';
import { StagePager } from '@/components/stage-pager';

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
  const [mode, setMode] = useUrlState<'page' | 'scroll'>('mode', 'page', {
    parse: (raw) => (raw === 'scroll' ? 'scroll' : undefined),
  });
  // `p` is the 1-based page number; it is clamped to the available range below.
  const [pageNumber, setPageNumber] = useUrlState('p', 1, { parse: parsePositiveInt });
  const selectedIndex = pageNumber - 1;
  const setSelectedIndex = React.useCallback(
    (next: number | ((index: number) => number)) => setPageNumber((number) => {
      const index = typeof next === 'function' ? next(number - 1) : next;
      return Math.max(0, index) + 1;
    }),
    [setPageNumber],
  );
  const pageIndex = Math.min(selectedIndex, Math.max(0, pages.length - 1));
  const currentPage = pages[pageIndex];

  function changePage(delta: number) {
    setSelectedIndex((index) => Math.max(0, Math.min(pages.length - 1, index + delta)));
  }

  const pager = usePagerInput(changePage, mode === 'page');

  return (
    <div className="grid min-w-0" role="region" aria-label="Comic reader" tabIndex={0} {...pager.containerProps}>
      <div ref={contentRef} className="grid">
        {mode === 'page' ? (
          currentPage ? (
            <div {...pager.swipeProps} className="relative flex min-h-[16rem] items-center justify-center overflow-hidden sm:min-h-[28rem]">
              {containedVideoUrl(currentPage) ? (
                <video key={currentPage.id} src={resolveApiUrl(containedVideoUrl(currentPage)!)} controls playsInline preload="metadata"
                  poster={currentPage.posterUrl ? resolveApiUrl(currentPage.posterUrl) : undefined}
                  aria-label={currentPage.title ?? `Page ${pageIndex + 1}`}
                  className="relative z-10 max-h-[calc(100dvh-7rem)] max-w-full object-contain" />
              ) : currentPage.contentUrl ? <img src={resolveApiUrl(currentPage.contentUrl)} alt={currentPage.title ?? `Page ${pageIndex + 1}`} className="max-h-[calc(100dvh-7rem)] max-w-full object-contain" /> : <span className="p-4 text-sm">Page {pageIndex + 1}</span>}
              <StagePager
                canPrev={pageIndex > 0}
                canNext={pageIndex < pages.length - 1}
                onPrev={() => changePage(-1)}
                onNext={() => changePage(1)}
                openHref={`/cards/${currentPage.id}`}
                openLabel={`Open page ${pageIndex + 1}${currentPage.title ? `: ${currentPage.title}` : ''}`}
                onOpen={onNavigate}
                consumeSwipeClick={pager.consumeSwipeClick}
              />
            </div>
          ) : <p className="text-stage-foreground/70 p-6 text-sm">No pages in this comic.</p>
        ) : pages.map((page, index) => containedVideoUrl(page) ? (
          // A link around <video> would swallow the player controls, so videos stay unwrapped.
          <video key={page.id} src={resolveApiUrl(containedVideoUrl(page)!)} controls playsInline preload="metadata"
            poster={page.posterUrl ? resolveApiUrl(page.posterUrl) : undefined}
            aria-label={page.title ?? `Page ${index + 1}`}
            className="mx-auto block max-h-[calc(100dvh-7rem)] w-full max-w-5xl object-contain" />
        ) : (
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
          <span className="text-stage-foreground/80 min-w-12 text-center text-sm tabular-nums" aria-live="polite">{pages.length ? pageIndex + 1 : 0} / {pages.length}</span>
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
