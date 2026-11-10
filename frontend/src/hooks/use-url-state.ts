import * as React from 'react';

import { registerPendingFlush, replaceUrl } from '@/lib/history-state';

type Options<T> = {
  parse?: (raw: string) => T | undefined;
  serialize?: (value: T) => string;
  /** Delay for writing to the URL, for text inputs. The returned value itself updates immediately. */
  debounce?: number;
};

/**
 * State mirrored in a query parameter. Changes use replaceState (no extra history entries),
 * the parameter is omitted when equal to the default, and Back/Forward re-read it.
 */
export function useUrlState<T = string>(key: string, defaultValue: T, options: Options<T> = {}) {
  const optionsRef = React.useRef(options);
  optionsRef.current = options;
  const defaultRef = React.useRef(defaultValue);

  const read = React.useCallback((): T => {
    const raw = new URLSearchParams(window.location.search).get(key);
    if (raw === null) return defaultRef.current;
    const parse = optionsRef.current.parse;
    if (!parse) return raw as unknown as T;
    const parsed = parse(raw);
    return parsed === undefined ? defaultRef.current : parsed;
  }, [key]);

  const [value, setValue] = React.useState<T>(read);
  const latest = React.useRef(value);
  const timer = React.useRef<number | null>(null);

  const write = React.useCallback(() => {
    if (timer.current !== null) {
      window.clearTimeout(timer.current);
      timer.current = null;
    }
    const current = latest.current;
    replaceUrl((params) => {
      const serialize = optionsRef.current.serialize ?? ((item: T) => String(item));
      if (current === defaultRef.current) params.delete(key);
      else params.set(key, serialize(current));
    });
  }, [key]);

  React.useEffect(() => {
    const onPopState = () => {
      if (timer.current !== null) {
        window.clearTimeout(timer.current);
        timer.current = null;
      }
      const next = read();
      latest.current = next;
      setValue(next);
    };
    window.addEventListener('popstate', onPopState);
    const unregister = registerPendingFlush(() => { if (timer.current !== null) write(); });
    return () => {
      window.removeEventListener('popstate', onPopState);
      unregister();
      if (timer.current !== null) window.clearTimeout(timer.current);
    };
  }, [read, write]);

  const update = React.useCallback((next: T | ((previous: T) => T)) => {
    const resolved = typeof next === 'function' ? (next as (previous: T) => T)(latest.current) : next;
    if (resolved === latest.current) return;
    latest.current = resolved;
    setValue(resolved);
    const delay = optionsRef.current.debounce ?? 0;
    if (delay > 0) {
      if (timer.current !== null) window.clearTimeout(timer.current);
      timer.current = window.setTimeout(write, delay);
    } else {
      write();
    }
  }, [write]);

  return [value, update] as const;
}

/** Parser for 1-based positive integer parameters (returns undefined for anything else). */
export function parsePositiveInt(raw: string) {
  const value = Number(raw);
  return Number.isSafeInteger(value) && value > 0 ? value : undefined;
}
