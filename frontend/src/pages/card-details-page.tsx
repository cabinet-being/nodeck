import * as React from 'react';

import { getCard, getCardRelations, resolveApiUrl, type Card, type CardRelations } from '@/api/cards';
import { FavoriteToggle } from '@/components/favorite-toggle';
import { DailyVote } from '@/components/daily-vote';
import { CardCatalogPanel } from '@/components/card-catalog-panel';
import { MediaViewer } from '@/components/media-viewer';
import { SetViewer } from '@/components/set-viewer';
import { Button } from '@/components/ui/button';
import { ComicReader } from '@/components/comic-reader';
import { DeckMembershipControl } from '@/components/deck-membership-control';
import { RecommendationRail } from '@/components/recommendation-rail';
import { Card as UiCard, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import {
  useVisualCardView,
  VisualCardViewToggle,
  VisualCardWall,
} from '@/components/visual-card-wall';
import { RelationDashboard } from '@/components/relation-dashboard';
import { useCardViewTracking } from '@/hooks/use-card-view-tracking';

export function CardDetailsPage({
  cardId,
  onNavigate,
}: {
  cardId: number;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
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
    return <div className="text-destructive p-6 text-sm">{error}</div>;
  }

  if (!card || card.id !== cardId || !relations) {
    return <div className="text-muted-foreground p-6 text-sm">Loading</div>;
  }

  function updateFavorite(isFavorite: boolean) {
    setCard((current) => (current ? { ...current, isFavorite } : current));
  }

  if (card.type === 'comic') {
    return (
      <ComicDetails
        card={card}
        relations={relations}
        onRelationsChange={setRelations}
        onNavigate={onNavigate}
        onFavoriteChange={updateFavorite}
      />
    );
  }

  if (card.type === 'set') {
    return (
      <SetDetails
        card={card}
        relations={relations}
        onRelationsChange={setRelations}
        onNavigate={onNavigate}
        onFavoriteChange={updateFavorite}
      />
    );
  }

  if (card.type === 'tag' || card.type === 'source') {
    return (
      <TextCardDetails
        card={card}
        relations={relations}
        onRelationsChange={setRelations}
        onNavigate={onNavigate}
        onFavoriteChange={updateFavorite}
      />
    );
  }

  return (
    <MediaDetails
      card={card}
      relations={relations}
      onRelationsChange={setRelations}
      onNavigate={onNavigate}
      onFavoriteChange={updateFavorite}
    />
  );
}

function MediaDetails({
  card,
  relations,
  onRelationsChange,
  onNavigate,
  onFavoriteChange,
}: {
  card: Card;
  relations: CardRelations;
  onRelationsChange: (relations: CardRelations) => void;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onFavoriteChange: (isFavorite: boolean) => void;
}) {
  const isVideo = card.metadata.media_type === 'video';
  const videoUrl = card.playbackUrl || card.contentUrl;
  const contentRef = useCardViewTracking(card.id, 'media');

  return (
    <section className="grid gap-6 px-4 py-6 sm:px-6 sm:py-8">
      <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_20rem]">
        <div className="grid min-w-0 gap-2">
          {videoUrl && isVideo ? (
            <MediaViewer kind="video" src={videoUrl} alt={card.title ?? ''} posterUrl={card.posterUrl} contentRef={contentRef} />
          ) : null}
          {card.contentUrl && !isVideo ? (
            <MediaViewer kind="image" src={card.contentUrl} alt={card.title ?? ''} contentRef={contentRef} />
          ) : null}

          {isVideo && card.contentUrl ? (
            <a href={resolveApiUrl(card.contentUrl)} className="text-primary w-fit text-sm underline">
              Original file
            </a>
          ) : null}
        </div>

        <CardAside card={card} onFavoriteChange={onFavoriteChange} />
      </div>

      <MoreSection card={card} relations={relations} onRelationsChange={onRelationsChange} onNavigate={onNavigate} />
      <RecommendationRail contextCardId={card.id} onNavigate={onNavigate} />
    </section>
  );
}

function ComicDetails({
  card,
  relations,
  onRelationsChange,
  onNavigate,
  onFavoriteChange,
}: {
  card: Card;
  relations: CardRelations;
  onRelationsChange: (relations: CardRelations) => void;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onFavoriteChange: (isFavorite: boolean) => void;
}) {
  const containedCards = card.containedCards ?? [];
  const contentRef = useCardViewTracking(card.id, 'collection');

  return (
    <section className="grid gap-6 px-4 py-6 sm:px-6 sm:py-8">
      <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_20rem]">
        <ComicReader key={card.id} pages={containedCards} contentRef={contentRef} onNavigate={onNavigate} />

        <CardAside card={card} onFavoriteChange={onFavoriteChange} />
      </div>

      <MoreSection card={card} relations={relations} onRelationsChange={onRelationsChange} onNavigate={onNavigate} />
      <RecommendationRail contextCardId={card.id} onNavigate={onNavigate} />
    </section>
  );
}

function SetDetails({
  card,
  relations,
  onRelationsChange,
  onNavigate,
  onFavoriteChange,
}: {
  card: Card;
  relations: CardRelations;
  onRelationsChange: (relations: CardRelations) => void;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onFavoriteChange: (isFavorite: boolean) => void;
}) {
  const containedCards = card.containedCards ?? [];
  const [wall, setWall] = React.useState(false);
  const contentRef = useCardViewTracking(card.id, 'collection');
  const [view, setView] = useVisualCardView('nodeck.setView');

  return (
    <section className="grid gap-6 px-4 py-6 sm:px-6 sm:py-8">
      <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_20rem]">
        <div className="grid min-w-0 gap-4">
          {wall ? (
            <>
              <div className="flex flex-wrap items-center justify-end gap-2">
                <Button type="button" size="sm" variant="outline" onClick={() => setWall(false)}>Viewer</Button>
                <VisualCardViewToggle view={view} onChange={setView} />
              </div>
              <div ref={contentRef}>
                <VisualCardWall cards={containedCards} view={view} density="compact" onNavigate={onNavigate} />
              </div>
            </>
          ) : (
            <SetViewer
              key={card.id}
              cards={containedCards}
              contentRef={contentRef}
              onNavigate={onNavigate}
              toolbar={<Button type="button" size="sm" variant="outline" onClick={() => setWall(true)}>Wall</Button>}
            />
          )}
        </div>

        <CardAside card={card} onFavoriteChange={onFavoriteChange} />
      </div>

      <MoreSection card={card} relations={relations} onRelationsChange={onRelationsChange} onNavigate={onNavigate} />
      <RecommendationRail contextCardId={card.id} onNavigate={onNavigate} />
    </section>
  );
}

function TextCardDetails({
  card,
  relations,
  onRelationsChange,
  onNavigate,
  onFavoriteChange,
}: {
  card: Card;
  relations: CardRelations;
  onRelationsChange: (relations: CardRelations) => void;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onFavoriteChange: (isFavorite: boolean) => void;
}) {
  return (
    <section className="mx-auto grid w-full max-w-6xl gap-4 px-4 py-6 sm:px-6 sm:py-8">
      <UiCard>
        <CardHeader>
          <div className="flex items-start justify-between gap-3">
            <CardTitle>{card.title}</CardTitle>
            <div className="flex items-center gap-2">
              <FavoriteToggle cardId={card.id} isFavorite={card.isFavorite} onChange={onFavoriteChange} />
            </div>
          </div>
        </CardHeader>
      </UiCard>
      <MoreSection card={card} relations={relations} onRelationsChange={onRelationsChange} onNavigate={onNavigate} />
    </section>
  );
}

function CardAside({
  card,
  onFavoriteChange,
}: {
  card: Card;
  onFavoriteChange: (isFavorite: boolean) => void;
}) {
  return (
    <aside className="grid content-start gap-4">
      <UiCard className="rounded-xl">
        <CardHeader>
          <div className="flex items-start justify-between gap-3">
            <CardTitle className="line-clamp-2">{card.title || card.type}</CardTitle>
            <div className="flex items-center gap-2">
              <FavoriteToggle cardId={card.id} isFavorite={card.isFavorite} onChange={onFavoriteChange} />
            </div>
          </div>
        </CardHeader>
        <CardContent>
          <DailyVote key={card.id} cardId={card.id} />
        </CardContent>
      </UiCard>

      <CardCatalogPanel cardId={card.id} metadata={card.metadata} />
      <DeckMembershipControl key={card.id} cardId={card.id} />
    </aside>
  );
}

function MoreSection({
  card,
  relations,
  onRelationsChange,
  onNavigate,
}: {
  card: Card;
  relations: CardRelations;
  onRelationsChange: (relations: CardRelations) => void;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  return (
    <details className="rounded-lg border">
      <summary className="focus-visible:ring-ring cursor-pointer rounded-lg px-4 py-3 text-sm font-medium focus-visible:ring-2 focus-visible:outline-none">
        More: relations and technical details
      </summary>
      <div className="grid gap-4 border-t p-3">
        <RelationDashboard
          card={card}
          relations={relations}
          onRelationsChange={onRelationsChange}
          onNavigate={onNavigate}
        />
        <JsonPanel title="Properties" value={card.properties} />
        <JsonPanel title="Metadata" value={card.metadata} />
      </div>
    </details>
  );
}

function JsonPanel({
  title,
  value,
}: {
  title: string;
  value: Record<string, unknown> | null;
}) {
  return (
    <UiCard>
      <CardHeader>
        <CardTitle>{title}</CardTitle>
      </CardHeader>
      <CardContent>
        <pre className="bg-muted/50 max-h-72 overflow-auto rounded-lg p-3 text-xs">
          {JSON.stringify(value ?? {}, null, 2)}
        </pre>
      </CardContent>
    </UiCard>
  );
}
