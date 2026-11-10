import * as React from 'react';

/**
 * Arrow-key (while the container is focused) and horizontal-swipe paging.
 * Spread `containerProps` on the focusable container and `swipeProps` on the swipe area.
 * `consumeSwipeClick` returns true (once) when a click directly follows a swipe.
 */
export function usePagerInput(onChange: (delta: number) => void, enabled = true) {
  const touchStart = React.useRef<{ x: number; y: number } | null>(null);
  const lastSwipeAt = React.useRef(0);

  const onKeyDown = (event: React.KeyboardEvent<HTMLElement>) => {
    if (!enabled || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
    const target = event.target;
    if (target instanceof HTMLElement && (target.isContentEditable || target.closest('input, textarea, select, [contenteditable]'))) return;
    if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
      event.preventDefault();
      onChange(event.key === 'ArrowRight' ? 1 : -1);
    }
  };

  const swipeProps = {
    onTouchStart: (event: React.TouchEvent<HTMLElement>) => {
      lastSwipeAt.current = 0;
      touchStart.current = { x: event.touches[0].clientX, y: event.touches[0].clientY };
    },
    onTouchEnd: (event: React.TouchEvent<HTMLElement>) => {
      if (!touchStart.current) return;
      const changeX = event.changedTouches[0].clientX - touchStart.current.x;
      const changeY = event.changedTouches[0].clientY - touchStart.current.y;
      touchStart.current = null;
      if (Math.abs(changeX) >= 50 && Math.abs(changeX) > Math.abs(changeY) * 1.3) {
        lastSwipeAt.current = Date.now();
        onChange(changeX < 0 ? 1 : -1);
      }
    },
    onTouchCancel: () => {
      touchStart.current = null;
      lastSwipeAt.current = 0;
    },
  };

  const consumeSwipeClick = () => {
    if (Date.now() - lastSwipeAt.current < 500) {
      lastSwipeAt.current = 0;
      return true;
    }
    return false;
  };

  return { containerProps: { onKeyDown }, swipeProps, consumeSwipeClick };
}
