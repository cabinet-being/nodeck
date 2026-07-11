import * as React from 'react';

import { getCard, resolveApiUrl, type Card } from '@/api/cards';
import { Card as UiCard, CardContent, CardHeader, CardTitle } from '@/components/ui/card';

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

      <aside className="grid content-start gap-4">
        <UiCard>
          <CardHeader>
            <div className="flex items-start justify-between gap-3">
              <CardTitle>{card.title}</CardTitle>
              <div className="text-muted-foreground shrink-0 text-sm">#{card.id}</div>
            </div>
          </CardHeader>
        </UiCard>

        <JsonPanel title="Properties" value={card.properties} />
        <JsonPanel title="Metadata" value={card.metadata} />
      </aside>
    </section>
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
