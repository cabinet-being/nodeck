/**
 * Helpers that keep per-page UI state attached to browser history entries:
 * query-string state written with replaceState, scroll positions saved per entry
 * and restored on Back/Forward, and small per-entry values kept in sessionStorage.
 */

type EntryState = { key?: string; scrollY?: number } & Record<string, unknown>;

const flushers = new Set<() => void>();

function currentState(): EntryState {
  const state = window.history.state;
  return state && typeof state === 'object' ? (state as EntryState) : {};
}

export function currentUrl() {
  return `${window.location.pathname}${window.location.search}${window.location.hash}`;
}

/** Rewrites the current entry's URL without adding a history entry. */
export function replaceUrl(mutate: (params: URLSearchParams) => void) {
  const params = new URLSearchParams(window.location.search);
  mutate(params);
  const query = params.toString();
  const next = `${window.location.pathname}${query ? `?${query}` : ''}${window.location.hash}`;
  if (next !== currentUrl()) window.history.replaceState(window.history.state, '', next);
}

/** Registers a callback that writes pending (debounced) state; returns an unregister function. */
export function registerPendingFlush(flush: () => void) {
  flushers.add(flush);
  return () => { flushers.delete(flush); };
}

function newKey() {
  return Math.random().toString(36).slice(2, 10);
}

/** Identifier of the current history entry; assigned lazily for entries without one. */
export function getEntryKey() {
  const state = currentState();
  if (state.key) return state.key;
  const key = newKey();
  window.history.replaceState({ ...state, key }, '');
  return key;
}

export function readEntryValue(name: string): string | null {
  try {
    return window.sessionStorage.getItem(`nodeck.entry.${getEntryKey()}.${name}`);
  } catch {
    return null;
  }
}

export function writeEntryValue(name: string, value: string, entryKey = getEntryKey()) {
  try {
    window.sessionStorage.setItem(`nodeck.entry.${entryKey}.${name}`, value);
  } catch {
    // Storage may be unavailable; losing the hint is harmless.
  }
}

/** Call right before leaving the current entry: flushes pending URL state and stores the scroll position. */
export function saveEntryBeforeLeave() {
  flushers.forEach((flush) => flush());
  const state = currentState();
  window.history.replaceState({ ...state, key: state.key ?? newKey(), scrollY: window.scrollY }, '');
}

/** Pushes a new entry; the page starts at the top when the path changes. */
export function pushEntry(url: string) {
  const previousPath = window.location.pathname;
  saveEntryBeforeLeave();
  window.history.pushState({ key: newKey() }, '', url);
  if (window.location.pathname !== previousPath) window.scrollTo(0, 0);
}

let cancelRestore: (() => void) | null = null;

/** Restores the scroll position saved for the current entry, waiting for async content to grow the page. */
export function restoreEntryScroll() {
  cancelRestore?.();
  const target = currentState().scrollY;
  if (typeof target !== 'number' || target <= 0) {
    window.scrollTo(0, 0);
    return;
  }
  const started = performance.now();
  let frame = 0;
  let done = false;
  const stop = () => {
    done = true;
    window.cancelAnimationFrame(frame);
    userEvents.forEach((name) => window.removeEventListener(name, stop));
    if (cancelRestore === stop) cancelRestore = null;
  };
  const userEvents = ['wheel', 'touchstart', 'keydown', 'pointerdown'];
  userEvents.forEach((name) => window.addEventListener(name, stop, { passive: true }));
  cancelRestore = stop;
  const tick = () => {
    if (done) return;
    const maxScroll = document.documentElement.scrollHeight - window.innerHeight;
    window.scrollTo(0, Math.min(target, Math.max(0, maxScroll)));
    if (maxScroll >= target && Math.abs(window.scrollY - target) < 2) {
      // Reached; keep trying a few more frames in case late layout shifts move it.
      if (performance.now() - started > 400) { stop(); return; }
    } else if (performance.now() - started > 2000) {
      stop();
      return;
    }
    frame = window.requestAnimationFrame(tick);
  };
  frame = window.requestAnimationFrame(tick);
}

export function initHistoryScroll() {
  if ('scrollRestoration' in window.history) window.history.scrollRestoration = 'manual';
  window.addEventListener('pagehide', () => {
    const state = currentState();
    window.history.replaceState({ ...state, key: state.key ?? newKey(), scrollY: window.scrollY }, '');
  });
  if (typeof currentState().scrollY === 'number') restoreEntryScroll();
}
