import * as React from 'react';
import { ChevronLeft, ChevronRight, ExternalLink } from 'lucide-react';

import { resolveApiUrl, type ContainedCard } from '@/api/cards';
import { MediaViewer } from '@/components/media-viewer';
import { Button } from '@/components/ui/button';
import { usePagerInput } from '@/hooks/use-pager-input';

export function SetViewer({
  cards,
  contentRef,
  onNavigate,
  toolbar,
}: {
  cards: ContainedCard[];
  contentRef: React.Ref<HTMLDivElement>;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  toolbar?: React.ReactNode;
}) {
  const [selected, setSelected] = React.useState(0);
  const index = Math.min(selected, Math.max(0, cards.length - 1));
  const current = cards[index];

  const pager = usePagerInput((delta) => setSelected((value) => Math.max(0, Math.min(cards.length - 1, value + delta))));

  if (!current) return <p className="text-muted-foreground text-sm">No cards in this set.</p>;

  const mainSrc = current.contentUrl || current.previewUrl;

  return (
    <div className="grid min-w-0 gap-3" role="region" aria-label="Set viewer" tabIndex={0}
      {...pager.containerProps} onClick={(event) => { if (pager.consumeSwipeClick()) event.preventDefault(); }}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2">
          <Button type="button" size="sm" variant="outline" aria-label="Previous card" disabled={index === 0}
            onClick={() => setSelected(index - 1)}><ChevronLeft className="size-4" /></Button>
          <span className="text-muted-foreground text-sm" aria-live="polite">{index + 1} / {cards.length}</span>
          <Button type="button" size="sm" variant="outline" aria-label="Next card" disabled={index >= cards.length - 1}
            onClick={() => setSelected(index + 1)}><ChevronRight className="size-4" /></Button>
        </div>
        <a href={`/cards/${current.id}`} onClick={onNavigate}
          className="text-primary focus-visible:ring-ring inline-flex items-center gap-1 rounded text-sm underline focus-visible:ring-2 focus-visible:outline-none">
          <ExternalLink className="size-4" aria-hidden="true" />
          Open card{current.title ? `: ${current.title}` : ''}
        </a>
        {toolbar}
      </div>

      <div {...pager.swipeProps}>
        {mainSrc ? (
          <MediaViewer
            kind="image"
            src={mainSrc}
            fallbackSrc={current.previewUrl}
            alt={current.title ?? `Card ${index + 1}`}
            contentRef={contentRef}
          />
        ) : (
          <div ref={contentRef} className="bg-muted text-muted-foreground flex min-h-[20rem] items-center justify-center rounded-xl text-sm">
            No preview
          </div>
        )}
      </div>

      <div className="flex gap-2 overflow-x-auto pb-2" aria-label="Set cards">
        {cards.map((card, cardIndex) => (
          <button key={card.id} type="button" onClick={() => setSelected(cardIndex)}
            aria-label={`Card ${cardIndex + 1}${card.title ? `: ${card.title}` : ''}`}
            aria-current={cardIndex === index ? 'true' : undefined}
            className="bg-muted focus-visible:ring-ring aria-[current=true]:ring-primary h-20 w-16 shrink-0 overflow-hidden rounded border focus-visible:ring-2 focus-visible:outline-none aria-[current=true]:ring-2">
            {card.previewUrl || card.contentUrl
              ? <img src={resolveApiUrl(card.previewUrl || card.contentUrl!)} alt="" className="h-full w-full object-cover" loading="lazy" />
              : <span className="text-xs">{cardIndex + 1}</span>}
          </button>
        ))}
      </div>
    </div>
  );
}
