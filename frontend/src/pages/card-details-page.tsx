import * as React from 'react';

import { getCard, getCardRelations, resolveApiUrl, type Card, type CardRelations } from '@/api/cards';
import { FavoriteToggle } from '@/components/favorite-toggle';
import { Badge } from '@/components/ui/badge';
import { Card as UiCard, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import {
  useVisualCardView,
  VisualCardViewToggle,
  VisualCardWall,
} from '@/components/visual-card-wall';
import { RelationDashboard } from '@/components/relation-dashboard';

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
    setCard(null);
    setRelations(null);
    setError(null);

    Promise.all([getCard(cardId), getCardRelations(cardId)])
      .then(([loadedCard, loadedRelations]) => {
        setCard(loadedCard);
        setRelations(loadedRelations);
      })
      .catch((requestError) => setError(requestError.message));
  }, [cardId]);

  if (error) {
    return <div className="text-destructive p-6 text-sm">{error}</div>;
  }

  if (!card || !relations) {
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

  return (
    <section className="grid gap-4 p-6">
      <div className="grid gap-4 xl:grid-cols-[1fr_24rem]">
        <div className="bg-muted flex min-h-[28rem] items-center justify-center overflow-hidden rounded-lg border">
          {card.contentUrl && isVideo ? (
            <video
              src={resolveApiUrl(card.contentUrl)}
              controls
              preload="metadata"
              className="max-h-[calc(100vh-8rem)] max-w-full object-contain"
            />
          ) : null}
          {card.contentUrl && !isVideo ? (
            <img
              src={resolveApiUrl(card.contentUrl)}
              alt={card.title ?? ''}
              className="max-h-[calc(100vh-8rem)] max-w-full object-contain"
            />
          ) : null}
        </div>

        <CardAside card={card} onFavoriteChange={onFavoriteChange} />
      </div>

      <RelationDashboard
        card={card}
        relations={relations}
        onRelationsChange={onRelationsChange}
        onNavigate={onNavigate}
      />
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

  return (
    <section className="grid gap-4 p-6">
      <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_24rem]">
        <div className="grid gap-4">
          {containedCards.map((item) => (
            <a
              key={item.id}
              href={`/cards/${item.id}`}
              className="bg-muted block overflow-hidden rounded-lg border"
            >
              {item.contentUrl ? (
                <img
                  src={resolveApiUrl(item.contentUrl)}
                  alt={item.title ?? ''}
                  className="mx-auto max-h-none w-full max-w-5xl object-contain"
                  loading="lazy"
                />
              ) : null}
            </a>
          ))}
        </div>

        <CardAside card={card} onFavoriteChange={onFavoriteChange} />
      </div>

      <RelationDashboard
        card={card}
        relations={relations}
        onRelationsChange={onRelationsChange}
        onNavigate={onNavigate}
      />
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
  const [view, setView] = useVisualCardView('nodeck.setView');

  return (
    <section className="grid gap-4 p-6">
      <div className="grid gap-4 2xl:grid-cols-[minmax(0,1fr)_24rem]">
        <div className="grid min-w-0 gap-4">
          <div className="flex justify-end">
            <VisualCardViewToggle view={view} onChange={setView} />
          </div>
          <VisualCardWall cards={containedCards} view={view} density="compact" />
        </div>

        <CardAside card={card} onFavoriteChange={onFavoriteChange} />
      </div>

      <RelationDashboard
        card={card}
        relations={relations}
        onRelationsChange={onRelationsChange}
        onNavigate={onNavigate}
      />
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
    <section className="mx-auto grid w-full max-w-6xl gap-4 p-6">
      <UiCard>
        <CardHeader>
          <div className="flex items-start justify-between gap-3">
            <CardTitle>{card.title}</CardTitle>
            <div className="flex items-center gap-2">
              <FavoriteToggle cardId={card.id} isFavorite={card.isFavorite} onChange={onFavoriteChange} />
              <Badge variant="outline">{card.type}</Badge>
              <div className="text-muted-foreground shrink-0 text-sm">#{card.id}</div>
            </div>
          </div>
        </CardHeader>
      </UiCard>
      <JsonPanel title="Properties" value={card.properties} />
      <JsonPanel title="Metadata" value={card.metadata} />
      <RelationDashboard
        card={card}
        relations={relations}
        onRelationsChange={onRelationsChange}
        onNavigate={onNavigate}
      />
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
      <UiCard>
        <CardHeader>
          <div className="flex items-start justify-between gap-3">
            <CardTitle>{card.title}</CardTitle>
            <div className="flex items-center gap-2">
              <FavoriteToggle cardId={card.id} isFavorite={card.isFavorite} onChange={onFavoriteChange} />
              <Badge variant="outline">{card.type}</Badge>
              <div className="text-muted-foreground shrink-0 text-sm">#{card.id}</div>
            </div>
          </div>
        </CardHeader>
      </UiCard>

      <JsonPanel title="Properties" value={card.properties} />
      <JsonPanel title="Metadata" value={card.metadata} />
    </aside>
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
