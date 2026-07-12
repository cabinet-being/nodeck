import * as React from 'react';

import { getCard, resolveApiUrl, type Card, type CardRelation } from '@/api/cards';
import { Badge } from '@/components/ui/badge';
import { Card as UiCard, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import {
  useVisualCardView,
  VisualCardViewToggle,
  VisualCardWall,
} from '@/components/visual-card-wall';

export function CardDetailsPage({ cardId }: { cardId: number }) {
  const [card, setCard] = React.useState<Card | null>(null);
  const [error, setError] = React.useState<string | null>(null);

  React.useEffect(() => {
    setCard(null);
    setError(null);

    getCard(cardId)
      .then(setCard)
      .catch((requestError) => setError(requestError.message));
  }, [cardId]);

  if (error) {
    return <div className="text-destructive p-6 text-sm">{error}</div>;
  }

  if (!card) {
    return <div className="text-muted-foreground p-6 text-sm">Loading</div>;
  }

  if (card.type === 'comic') {
    return <ComicDetails card={card} />;
  }

  if (card.type === 'set') {
    return <SetDetails card={card} />;
  }

  if (card.type === 'tag' || card.type === 'source') {
    return <TextCardDetails card={card} />;
  }

  return <MediaDetails card={card} />;
}

function MediaDetails({ card }: { card: Card }) {
  return (
    <section className="grid min-h-full gap-4 p-6 xl:grid-cols-[1fr_24rem]">
      <div className="bg-muted flex min-h-[28rem] items-center justify-center overflow-hidden rounded-lg border">
        {card.contentUrl ? (
          <img
            src={resolveApiUrl(card.contentUrl)}
            alt={card.title ?? ''}
            className="max-h-[calc(100vh-8rem)] max-w-full object-contain"
          />
        ) : null}
      </div>

      <CardAside card={card} />
    </section>
  );
}

function ComicDetails({ card }: { card: Card }) {
  const containedCards = card.containedCards ?? [];

  return (
    <section className="grid gap-4 p-6 xl:grid-cols-[minmax(0,1fr)_24rem]">
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

      <CardAside card={card} />
    </section>
  );
}

function SetDetails({ card }: { card: Card }) {
  const containedCards = card.containedCards ?? [];
  const [view, setView] = useVisualCardView('nodeck.setView');

  return (
    <section className="grid gap-4 p-6 xl:grid-cols-[minmax(0,1fr)_24rem]">
      <div className="grid gap-4">
        <div className="flex justify-end">
          <VisualCardViewToggle view={view} onChange={setView} />
        </div>
        <VisualCardWall cards={containedCards} view={view} density="compact" />
      </div>

      <CardAside card={card} />
    </section>
  );
}

function TextCardDetails({ card }: { card: Card }) {
  return (
    <section className="mx-auto grid w-full max-w-4xl gap-4 p-6">
      <UiCard>
        <CardHeader>
          <div className="flex items-start justify-between gap-3">
            <CardTitle>{card.title}</CardTitle>
            <div className="flex items-center gap-2">
              <Badge variant="outline">{card.type}</Badge>
              <div className="text-muted-foreground shrink-0 text-sm">#{card.id}</div>
            </div>
          </div>
        </CardHeader>
      </UiCard>
      <JsonPanel title="Properties" value={card.properties} />
      <JsonPanel title="Metadata" value={card.metadata} />
      <RelationsPanel title="Outgoing relations" relations={card.outgoingRelations ?? []} />
      <RelationsPanel title="Incoming relations" relations={card.incomingRelations ?? []} />
    </section>
  );
}

function CardAside({ card }: { card: Card }) {
  return (
    <aside className="grid content-start gap-4">
      <UiCard>
        <CardHeader>
          <div className="flex items-start justify-between gap-3">
            <CardTitle>{card.title}</CardTitle>
            <div className="flex items-center gap-2">
              <Badge variant="outline">{card.type}</Badge>
              <div className="text-muted-foreground shrink-0 text-sm">#{card.id}</div>
            </div>
          </div>
        </CardHeader>
      </UiCard>

      <JsonPanel title="Properties" value={card.properties} />
      <JsonPanel title="Metadata" value={card.metadata} />
      <RelationsPanel title="Outgoing relations" relations={card.outgoingRelations ?? []} />
      <RelationsPanel title="Incoming relations" relations={card.incomingRelations ?? []} />
    </aside>
  );
}

function RelationsPanel({
  title,
  relations,
}: {
  title: string;
  relations: CardRelation[];
}) {
  return (
    <UiCard>
      <CardHeader>
        <CardTitle>{title}</CardTitle>
      </CardHeader>
      <CardContent>
        {relations.length > 0 ? (
          <div className="grid gap-2 text-sm">
            {relations.map((relation) => (
              <div key={relation.id} className="grid gap-1 rounded-lg border p-2">
                <div className="flex flex-wrap gap-2">
                  <Badge variant="outline">{relation.relationType}</Badge>
                  <span>#{relation.fromCardId}</span>
                  <span className="text-muted-foreground">to</span>
                  <span>#{relation.toCardId}</span>
                </div>
                {relation.properties ? (
                  <pre className="bg-muted/50 overflow-auto rounded p-2 text-xs">
                    {JSON.stringify(relation.properties, null, 2)}
                  </pre>
                ) : null}
              </div>
            ))}
          </div>
        ) : (
          <div className="text-muted-foreground text-sm">No relations</div>
        )}
      </CardContent>
    </UiCard>
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
