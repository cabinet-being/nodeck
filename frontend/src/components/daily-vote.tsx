import * as React from 'react';

import { getDailyVote, putDailyVote, type DailyVote as DailyVoteState } from '@/api/cards';
import { ThumbsDown, ThumbsUp } from 'lucide-react';

import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';

export function DailyVote({ cardId }: { cardId: number }) {
  const [vote, setVote] = React.useState<DailyVoteState | null>(null);
  const [loading, setLoading] = React.useState(true);
  const [saving, setSaving] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const timeZone = React.useMemo(() => Intl.DateTimeFormat().resolvedOptions().timeZone, []);

  React.useEffect(() => {
    let active = true;
    setVote(null);
    setLoading(true);
    setError(null);

    getDailyVote(cardId, timeZone)
      .then((result) => {
        if (active) setVote(result);
      })
      .catch((requestError: Error) => {
        if (active) setError(requestError.message);
      })
      .finally(() => {
        if (active) setLoading(false);
      });

    return () => { active = false; };
  }, [cardId, timeZone]);

  async function choose(value: -1 | 0 | 1) {
    if (saving) return;
    setSaving(true);
    setError(null);
    try {
      setVote(await putDailyVote(cardId, value, timeZone));
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : 'Could not save vote.');
    } finally {
      setSaving(false);
    }
  }

  const current = vote?.value ?? 0;

  // Pressing the active thumb again resets the vote to 0.
  const thumbs = [
    { value: 1 as const, label: 'Vote up today', Icon: ThumbsUp },
    { value: -1 as const, label: 'Vote down today', Icon: ThumbsDown },
  ];

  return (
    <div className="flex items-center gap-1" role="group" aria-label="Daily vote">
      {thumbs.map(({ value, label, Icon }) => {
        const active = current === value;
        return (
          <Button
            key={value}
            type="button"
            size="icon"
            variant={active ? 'secondary' : 'ghost'}
            aria-pressed={active}
            aria-label={label}
            title={label}
            disabled={loading || saving}
            onClick={() => void choose(active ? 0 : value)}
          >
            <Icon className={cn('size-4', active && 'fill-current')} />
          </Button>
        );
      })}
      {error ? <span className="text-destructive text-xs" role="alert">{error}</span> : null}
    </div>
  );
}
