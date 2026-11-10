import * as React from 'react';
import { Images } from 'lucide-react';

import { listCards, type Card } from '@/api/cards';
import { getDailyRecommendations, getRecommendations, type Recommendation } from '@/api/recommendations';
import { EmptyState } from '@/components/empty-state';
import { Rail } from '@/components/rail';
import { buttonVariants } from '@/components/ui/button';
import { useLocalDay } from '@/hooks/use-local-day';

export function HomePage({
  onNavigate,
}: {
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const [recent, setRecent] = React.useState<Card[]>([]);
  const [recommended, setRecommended] = React.useState<Recommendation[]>([]);
  const [daily, setDaily] = React.useState<Recommendation[]>([]);
  const [recentLoading, setRecentLoading] = React.useState(true);
  const [recommendedLoading, setRecommendedLoading] = React.useState(true);
  const [dailyLoading, setDailyLoading] = React.useState(true);
  const [recentError, setRecentError] = React.useState<string | null>(null);
  const [recommendedError, setRecommendedError] = React.useState<string | null>(null);
  const [dailyError, setDailyError] = React.useState<string | null>(null);
  const [requestVersion, retry] = React.useReducer((value: number) => value + 1, 0);
  const today = useLocalDay();

  React.useEffect(() => {
    let active = true;
    setRecentLoading(true);
    setRecommendedLoading(true);
    setRecentError(null);
    setRecommendedError(null);

    listCards({ types: ['media', 'comic', 'set'], excludeContainedMedia: true,
      sort: 'created_at', order: 'desc' })
      .then((cards) => { if (active) setRecent(cards.slice(0, 24)); })
      .catch((cause) => {
        if (active) setRecentError(cause instanceof Error ? cause.message : 'Unable to load recent cards.');
      })
      .finally(() => { if (active) setRecentLoading(false); });

    getRecommendations({ mode: 'collections', limit: 50 })
      .then((response) => { if (active) setRecommended(response.items); })
      .catch((cause) => {
        if (active) setRecommendedError(cause instanceof Error ? cause.message : 'Unable to load recommendations.');
      })
      .finally(() => { if (active) setRecommendedLoading(false); });

    return () => { active = false; };
  }, [requestVersion]);

  React.useEffect(() => {
    let active = true;
    setDailyLoading(true);
    setDailyError(null);
    setDaily([]);
    getDailyRecommendations(30)
      .then((response) => { if (active) setDaily(response.items); })
      .catch((cause) => {
        if (active) setDailyError(cause instanceof Error ? cause.message : 'Unable to load today’s picks.');
      })
      .finally(() => { if (active) setDailyLoading(false); });
    return () => { active = false; };
  }, [requestVersion, today]);

  const libraryEmpty = !recentLoading && !recentError && recent.length === 0;

  if (libraryEmpty) {
    return (
      <div className="px-4 py-6 sm:px-6 sm:py-8">
        <EmptyState icon={Images} title="Your library is empty"
          action={<a href="/cards/import" onClick={onNavigate} className={buttonVariants({ size: 'lg' })}>Import files</a>} />
      </div>
    );
  }

  return (
    <div className="mx-auto grid max-w-screen-2xl gap-10 px-4 py-6 sm:px-6 sm:py-8">
      <Rail title="Today's picks" cards={daily.slice(0, 12).map(({ card }) => card)}
        onNavigate={onNavigate} loading={dailyLoading} error={dailyError} onRetry={retry}
        viewAllHref="/feed/daily" />
      <Rail title="For you" cards={recommended.slice(0, 20).map(({ card }) => card)}
        onNavigate={onNavigate} loading={recommendedLoading}
        error={recommendedError} onRetry={retry} viewAllHref="/feed/recommended" />
      <Rail title="Recently added" cards={recent} onNavigate={onNavigate}
        loading={recentLoading} error={recentError} onRetry={retry} viewAllHref="/gallery" />
    </div>
  );
}
