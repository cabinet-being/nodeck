import * as React from 'react';

import { getDailyVote, putDailyVote, type DailyVote as DailyVoteState } from '@/api/cards';
import { Button } from '@/components/ui/button';

const choices = [
  { value: 1, label: 'Up' },
  { value: 0, label: 'Neutral' },
  { value: -1, label: 'Down' },
] as const;

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

  return (
    <div className="grid gap-2" aria-label="Daily vote">
      <div className="text-sm font-medium">Today’s vote</div>
      <div className="flex flex-wrap gap-2">
        {choices.map(({ value, label }) => (
          <Button
            key={value}
            type="button"
            size="sm"
            variant={vote?.value === value ? 'secondary' : 'outline'}
            aria-pressed={vote?.value === value}
            disabled={loading || saving}
            onClick={() => choose(value)}
          >
            {label}
          </Button>
        ))}
      </div>
      {loading ? <span className="text-muted-foreground text-xs">Loading vote…</span> : null}
      {error ? <span className="text-destructive text-xs" role="alert">{error}</span> : null}
    </div>
  );
}
