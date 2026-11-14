import * as React from 'react';

import { getRandomCardId, type RandomCardParams } from '@/api/cards';

/** Opens a random card; the card that is already open is skipped. */
export function useRandomCard(onNavigateTo: (path: string) => void) {
  const [busy, setBusy] = React.useState(false);
  const [message, setMessage] = React.useState<string | null>(null);

  const openRandom = React.useCallback(async (params: RandomCardParams = {}) => {
    setBusy(true);
    setMessage(null);
    try {
      const open = /^\/cards\/(\d+)/.exec(window.location.pathname);
      const id = await getRandomCardId({ exclude: open ? Number(open[1]) : undefined, ...params });
      if (id === null) setMessage('No card matches.');
      else onNavigateTo(`/cards/${id}`);
    } catch (cause) {
      setMessage(cause instanceof Error ? cause.message : 'Could not pick a random card.');
    } finally {
      setBusy(false);
    }
  }, [onNavigateTo]);

  return { openRandom, busy, message };
}
