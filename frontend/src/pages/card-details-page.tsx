import * as React from 'react';
import { ImageOff } from 'lucide-react';

import { getCard, getCardRelations, type Card, type CardRelations } from '@/api/cards';
import { ActionBar } from '@/components/action-bar';
import { CardDetailsContent, CardInspector } from '@/components/card-inspector';
import { ComicReader } from '@/components/comic-reader';
import { DeleteCardDialog } from '@/components/delete-card-dialog';
import { EmptyState } from '@/components/empty-state';
import { MediaStage } from '@/components/media-stage';
import { MediaViewer } from '@/components/media-viewer';
import { SimilarGrid } from '@/components/similar-grid';
import { SetViewer } from '@/components/set-viewer';
import { buttonVariants } from '@/components/ui/button';
import { Segmented } from '@/components/ui/segmented';
import { Skeleton } from '@/components/ui/skeleton';
import { useVisualCardView, VisualCardViewToggle, VisualCardWall } from '@/components/visual-card-wall';
import { useCardViewTracking } from '@/hooks/use-card-view-tracking';

type PageProps = {
  card: Card;
  relations: CardRelations;
  onRelationsChange: (relations: CardRelations) => void;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onNavigateTo?: (path: string) => void;
  onFavoriteChange: (isFavorite: boolean) => void;
};

export function CardDetailsPage({
  cardId,
  onNavigate,
  onNavigateTo,
}: {
  cardId: number;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onNavigateTo?: (path: string) => void;
}) {
  const [card, setCard] = React.useState<Card | null>(null);
  const [relations, setRelations] = React.useState<CardRelations | null>(null);
  const [error, setError] = React.useState<string | null>(null);

  React.useEffect(() => {
    let active = true;
    setCard(null);
    setRelations(null);
    setError(null);

    Promise.all([getCard(cardId), getCardRelations(cardId)])
      .then(([loadedCard, loadedRelations]) => {
        if (active) {
          setCard(loadedCard);
          setRelations(loadedRelations);
        }
      })
      .catch((requestError) => {
        if (active) setError(requestError.message);
      });

    return () => { active = false; };
  }, [cardId]);

  if (error) {
    return (
      <EmptyState
        icon={ImageOff}
        title={error}
        action={<a href="/gallery" onClick={onNavigate} className={buttonVariants({ variant: 'outline' })}>Back to gallery</a>}
      />
    );
  }

  if (!card || card.id !== cardId || !relations) {
    return <CardPageSkeleton />;
  }

  function updateFavorite(isFavorite: boolean) {
    setCard((current) => (current ? { ...current, isFavorite } : current));
  }

  const props: PageProps = {
    card,
    relations,
    onRelationsChange: setRelations,
    onNavigate,
    onNavigateTo,
    onFavoriteChange: updateFavorite,
  };

  if (card.type === 'comic') return <ComicDetails {...props} />;
  if (card.type === 'set') return <SetDetails {...props} />;
  if (card.type === 'tag' || card.type === 'source') return <TextCardDetails {...props} />;
  return <MediaDetails {...props} />;
}

function CardPageSkeleton() {
  return (
    <div className="mx-auto grid w-full max-w-screen-2xl gap-6 sm:px-6 sm:py-6 lg:grid-cols-[minmax(0,1fr)_22rem]" role="status" aria-label="Loading card">
      <Skeleton className="aspect-[4/3] w-full sm:rounded-[var(--radius-panel)]" />
      <div className="grid content-start gap-4 px-4 sm:px-0">
        <Skeleton className="h-7 w-2/3" />
        <Skeleton className="h-10 w-full" />
        <Skeleton className="h-32 w-full" />
      </div>
    </div>
  );
}

/** Stage on the left, inspector on the right (stacked on narrow screens), Similar rail under the stage. */
function CardLayout({
  stage,
  inspector,
  similar,
}: {
  stage: React.ReactNode;
  inspector: React.ReactNode;
  similar: React.ReactNode;
}) {
  return (
    <section className="mx-auto grid w-full max-w-screen-2xl gap-6 pb-8 sm:px-6 sm:pt-6 lg:grid-cols-[minmax(0,1fr)_22rem] lg:grid-rows-[auto_1fr]">
      <div className="min-w-0 lg:col-start-1 lg:row-start-1">{stage}</div>
      <div className="px-4 sm:px-0 lg:sticky lg:top-[4.5rem] lg:col-start-2 lg:row-span-2 lg:row-start-1 lg:max-h-[calc(100dvh-5.5rem)] lg:self-start lg:overflow-y-auto">
        {inspector}
      </div>
      <div className="min-w-0 px-4 sm:px-0 lg:col-start-1 lg:row-start-2">{similar}</div>
    </section>
  );
}

function MediaDetails(props: PageProps) {
  const { card, onNavigate } = props;
  const isVideo = card.metadata.media_type === 'video';
  const videoUrl = card.playbackUrl || card.contentUrl;
  const contentRef = useCardViewTracking(card.id, 'media');

  return (
    <CardLayout
      stage={
        <MediaStage>
          {videoUrl && isVideo ? (
            <MediaViewer kind="video" src={videoUrl} alt={card.title ?? ''} posterUrl={card.posterUrl}
              contentRef={contentRef} originalUrl={card.contentUrl} />
          ) : null}
          {card.contentUrl && !isVideo ? (
            <MediaViewer kind="image" src={card.contentUrl} alt={card.title ?? ''}
              contentRef={contentRef} originalUrl={card.contentUrl} />
          ) : null}
        </MediaStage>
      }
      inspector={<CardInspector {...props} originalUrl={card.contentUrl} />}
      similar={<SimilarGrid contextCardId={card.id} onNavigate={onNavigate} />}
    />
  );
}

function ComicDetails(props: PageProps) {
  const { card, onNavigate } = props;
  const contentRef = useCardViewTracking(card.id, 'collection');

  return (
    <CardLayout
      stage={
        <MediaStage>
          <ComicReader key={card.id} pages={card.containedCards ?? []} contentRef={contentRef} onNavigate={onNavigate} />
        </MediaStage>
      }
      inspector={<CardInspector {...props} />}
      similar={<SimilarGrid contextCardId={card.id} onNavigate={onNavigate} />}
    />
  );
}

function SetDetails(props: PageProps) {
  const { card, onNavigate } = props;
  const containedCards = card.containedCards ?? [];
  const [wall, setWall] = React.useState(false);
  const contentRef = useCardViewTracking(card.id, 'collection');
  const [view, setView] = useVisualCardView();

  const modeSwitch = (
    <Segmented
      ariaLabel="Set layout"
      value={wall ? 'grid' : 'viewer'}
      onValueChange={(value) => setWall(value === 'grid')}
      options={[{ value: 'viewer', label: 'Viewer' }, { value: 'grid', label: 'Grid' }]}
    />
  );

  return (
    <CardLayout
      stage={
        wall ? (
          <div className="grid gap-4 px-4 sm:px-0">
            <div className="flex flex-wrap items-center justify-between gap-2">
              {modeSwitch}
              <VisualCardViewToggle view={view} onChange={setView} />
            </div>
            <div ref={contentRef}>
              <VisualCardWall cards={containedCards} view={view} density="compact" onNavigate={onNavigate} />
            </div>
          </div>
        ) : (
          <MediaStage>
            <SetViewer key={card.id} cards={containedCards} contentRef={contentRef} onNavigate={onNavigate} toolbar={modeSwitch} />
          </MediaStage>
        )
      }
      inspector={<CardInspector {...props} />}
      similar={<SimilarGrid contextCardId={card.id} onNavigate={onNavigate} />}
    />
  );
}

/** Legacy tag and source cards: no media, so the relation dashboard is the main content. */
function TextCardDetails({ card, relations, onRelationsChange, onNavigate, onNavigateTo, onFavoriteChange }: PageProps) {
  const [deleting, setDeleting] = React.useState(false);

  return (
    <section className="mx-auto grid w-full max-w-6xl gap-6 px-4 py-6 sm:px-6 sm:py-8">
      <div className="grid gap-2">
        <h1 className="line-clamp-3 text-xl font-semibold tracking-tight">{card.title || 'Untitled'}</h1>
        <ActionBar
          cardId={card.id}
          isFavorite={card.isFavorite}
          onFavoriteChange={onFavoriteChange}
          editHref={`/cards/${card.id}/edit`}
          onNavigate={onNavigate}
          onDelete={() => setDeleting(true)}
        />
      </div>
      <CardDetailsContent card={card} relations={relations} onRelationsChange={onRelationsChange} onNavigate={onNavigate} />
      <DeleteCardDialog
        card={deleting ? card : null}
        onClose={() => setDeleting(false)}
        onDeleted={() => { setDeleting(false); onNavigateTo?.('/gallery'); }}
      />
    </section>
  );
}
