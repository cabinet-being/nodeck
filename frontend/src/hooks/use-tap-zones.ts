import * as React from 'react';

export type TapZone = 'prev' | 'open' | 'next';

/** Manga-style zones: left third = previous, right third = next, middle = open. */
export function tapZoneAt(event: React.MouseEvent<HTMLElement>): TapZone {
  // Keyboard activation (Enter on a link) reports no pointer position: treat it as "open".
  if (event.detail === 0) return 'open';
  const rect = event.currentTarget.getBoundingClientRect();
  if (rect.width <= 0) return 'open';
  const ratio = (event.clientX - rect.left) / rect.width;
  if (ratio < 1 / 3) return 'prev';
  if (ratio > 2 / 3) return 'next';
  return 'open';
}

/** Sets a matching cursor while the mouse moves over the element (mouse events only, so touch is unaffected). */
export function onTapZoneMouseMove(event: React.MouseEvent<HTMLElement>, canPrev: boolean, canNext: boolean) {
  const rect = event.currentTarget.getBoundingClientRect();
  const ratio = rect.width > 0 ? (event.clientX - rect.left) / rect.width : 0.5;
  event.currentTarget.style.cursor = ratio < 1 / 3
    ? (canPrev ? 'w-resize' : 'default')
    : ratio > 2 / 3
      ? (canNext ? 'e-resize' : 'default')
      : 'pointer';
}
