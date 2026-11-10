import * as React from 'react';
import { Images, Layers3 } from 'lucide-react';

import { getRecommendations, type Recommendation, type RecommendationMode } from '@/api/recommendations';
import { Rail } from '@/components/rail';
import { Segmented } from '@/components/ui/segmented';

export { Rail as CardRail, type RailCard } from '@/components/rail';

export function RecommendationRail({
  contextCardId,
  onNavigate,
  title = 'Recommended',
}: {
  contextCardId?: number;
  title?: string;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const [mode, setMode] = React.useState<RecommendationMode>('collections');
  const [items, setItems] = React.useState<Recommendation[]>([]);
  const [loading, setLoading] = React.useState(true);
  const [error, setError] = React.useState<string | null>(null);
  const [requestVersion, retry] = React.useReducer((value: number) => value + 1, 0);

  React.useEffect(() => {
    let active = true;
    setLoading(true);
    setError(null);
    setItems([]);
    getRecommendations({ mode, contextCardId, limit: 20 })
      .then((response) => { if (active) setItems(response.items); })
      .catch((cause) => {
        if (active) setError(cause instanceof Error ? cause.message : 'Unable to load recommendations.');
      })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [contextCardId, mode, requestVersion]);

  return (
    <Rail title={title} cards={items.map(({ card }) => card)} onNavigate={onNavigate}
      loading={loading} error={error} onRetry={retry}
      viewAllHref={`/feed/recommended?mode=${mode}${contextCardId ? `&contextCardId=${contextCardId}` : ''}`}
      controls={<RecommendationModeControl value={mode} onChange={setMode} />}
    />
  );
}

export function RecommendationModeControl({
  value,
  onChange,
}: {
  value: RecommendationMode;
  onChange: (mode: RecommendationMode) => void;
}) {
  return (
    <Segmented
      ariaLabel="Show"
      value={value}
      onValueChange={onChange}
      options={[
        { value: 'collections', label: 'Collections', icon: <Layers3 /> },
        { value: 'pages', label: 'Pages', icon: <Images /> },
      ]}
    />
  );
}
