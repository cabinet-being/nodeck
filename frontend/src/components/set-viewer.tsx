import * as React from 'react';
import { ChevronLeft, ChevronRight, ExternalLink } from 'lucide-react';

import { resolveApiUrl, type ContainedCard } from '@/api/cards';
import { MediaViewer } from '@/components/media-viewer';
import { stageButtonClass } from '@/components/media-stage';
import { usePagerInput } from '@/hooks/use-pager-input';
import { StagePager } from '@/components/stage-pager';

export function SetViewer({
  cards,
  contentRef,
  onNavigate,
  toolbar,
}: {
  cards: ContainedCard[];
  contentRef: React.Ref<HTMLDivElement>;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  /** Extra controls rendered in the control row under the media (for example a view switch). */
  toolbar?: React.ReactNode;
}) {
  const [selected, setSelected] = React.useState(0);
  const index = Math.min(selected, Math.max(0, cards.length - 1));
  const current = cards[index];

  const pager = usePagerInput((delta) => setSelected((value) => Math.max(0, Math.min(cards.length - 1, value + delta))));

  if (!current) return <p className="text-stage-foreground/70 p-6 text-sm">No cards in this set.</p>;

  const mainSrc = current.contentUrl || current.previewUrl;
  const canPrev = index > 0;
  const canNext = index < cards.length - 1;
  const sideButtonClass = `${stageButtonClass} absolute top-1/2 z-10 -translate-y-1/2 disabled:pointer-events-none disabled:opacity-0`;
  const openLabel = `Open card${current.title ? `: ${current.title}` : ''}`;

  return (
    <div className="grid min-w-0" role="region" aria-label="Set viewer" tabIndex={0}
      {...pager.containerProps} onClick={(event) => { if (pager.consumeSwipeClick()) event.preventDefault(); }}>
      <div {...pager.swipeProps}>
        {mainSrc ? (
          <MediaViewer
            kind="image"
            src={mainSrc}
            fallbackSrc={current.previewUrl}
            alt={current.title ?? `Card ${index + 1}`}
            contentRef={contentRef}
            originalUrl={current.contentUrl}
            overlay={({ actualSize }) => actualSize ? (
              <>
                <button type="button" className={`${sideButtonClass} left-2`} aria-label="Previous card" title="Previous card"
                  disabled={!canPrev} onClick={() => setSelected(index - 1)}><ChevronLeft /></button>
                <button type="button" className={`${sideButtonClass} right-2`} aria-label="Next card" title="Next card"
                  disabled={!canNext} onClick={() => setSelected(index + 1)}><ChevronRight /></button>
              </>
            ) : (
              <StagePager
                canPrev={canPrev}
                canNext={canNext}
                onPrev={() => setSelected(Math.max(0, index - 1))}
                onNext={() => setSelected(Math.min(cards.length - 1, index + 1))}
                openHref={`/cards/${current.id}`}
                openLabel={openLabel}
                onOpen={onNavigate}
                prevLabel="Previous card"
                nextLabel="Next card"
                consumeSwipeClick={pager.consumeSwipeClick}
              />
            )}
            toolbar={
              <a href={`/cards/${current.id}`} onClick={onNavigate} className={stageButtonClass}
                aria-label={openLabel} title={openLabel}>
                <ExternalLink />
              </a>
            }
          />
        ) : (
          <div ref={contentRef} className="text-stage-foreground/70 flex min-h-[16rem] items-center justify-center text-sm sm:min-h-[28rem]">
            No preview
          </div>
        )}
      </div>

      <div className="flex flex-wrap items-center justify-between gap-2 px-3 py-2">
        <span className="text-stage-foreground/80 min-w-12 text-center text-sm tabular-nums" aria-live="polite">{index + 1} / {cards.length}</span>
        {toolbar}
      </div>

      <div className="flex gap-2 overflow-x-auto px-3 pb-3" aria-label="Set cards">
        {cards.map((card, cardIndex) => (
          <button key={card.id} type="button" onClick={() => setSelected(cardIndex)}
            aria-label={`Card ${cardIndex + 1}${card.title ? `: ${card.title}` : ''}`}
            aria-current={cardIndex === index ? 'true' : undefined}
            className="bg-stage-foreground/10 focus-visible:ring-ring aria-[current=true]:ring-primary h-20 w-16 shrink-0 overflow-hidden rounded-lg opacity-70 focus-visible:ring-2 aria-[current=true]:opacity-100 aria-[current=true]:ring-2">
            {card.previewUrl || card.contentUrl
              ? <img src={resolveApiUrl(card.previewUrl || card.contentUrl!)} alt="" className="h-full w-full object-cover" loading="lazy" />
              : <span className="text-xs">{cardIndex + 1}</span>}
          </button>
        ))}
      </div>
    </div>
  );
}
