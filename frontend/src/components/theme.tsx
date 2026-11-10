import * as React from 'react';
import { Monitor, Moon, Sun } from 'lucide-react';

import { Button } from '@/components/ui/button';

export type Theme = 'light' | 'dark' | 'system';

const storageKey = 'nodeck.theme';
const order: Theme[] = ['system', 'light', 'dark'];
const listeners = new Set<() => void>();

function readTheme(): Theme {
  try {
    const value = window.localStorage.getItem(storageKey);
    return value === 'light' || value === 'dark' ? value : 'system';
  } catch {
    return 'system';
  }
}

let current: Theme = typeof window === 'undefined' ? 'system' : readTheme();

function apply(theme: Theme) {
  const dark = theme === 'dark' || (theme === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches);
  document.documentElement.classList.toggle('dark', dark);
}

function setTheme(theme: Theme) {
  current = theme;
  try {
    window.localStorage.setItem(storageKey, theme);
  } catch {
    // Storage can be unavailable; the theme still applies for this session.
  }
  apply(theme);
  listeners.forEach((listener) => listener());
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export function useTheme() {
  const theme = React.useSyncExternalStore(subscribe, () => current, () => 'system' as Theme);

  React.useEffect(() => {
    apply(theme);
    if (theme !== 'system') return;
    const query = window.matchMedia('(prefers-color-scheme: dark)');
    const onChange = () => apply('system');
    query.addEventListener('change', onChange);
    return () => query.removeEventListener('change', onChange);
  }, [theme]);

  const cycle = React.useCallback(() => {
    setTheme(order[(order.indexOf(current) + 1) % order.length]);
  }, []);

  return { theme, setTheme, cycle };
}

const labels: Record<Theme, string> = { system: 'System', light: 'Light', dark: 'Dark' };
const icons = { system: Monitor, light: Sun, dark: Moon };

export function ThemeToggle({ className }: { className?: string }) {
  const { theme, cycle } = useTheme();
  const Icon = icons[theme];
  const next = order[(order.indexOf(theme) + 1) % order.length];

  return (
    <Button
      type="button"
      variant="ghost"
      size="icon"
      className={className}
      aria-label={`Theme: ${labels[theme]}. Switch to ${labels[next]}`}
      title={`Theme: ${labels[theme]}`}
      onClick={cycle}
    >
      <Icon />
    </Button>
  );
}
