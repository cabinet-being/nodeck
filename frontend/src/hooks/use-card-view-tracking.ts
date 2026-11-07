import * as React from 'react';

import { recordCardView } from '@/api/cards';

// A view needs two uninterrupted seconds of visible, loaded content.
const minimumDwellMs = 2000;

function isMeaningfullyVisible(element: Element) {
  const rect = element.getBoundingClientRect();
  const width = Math.max(0, Math.min(rect.right, window.innerWidth) - Math.max(rect.left, 0));
  const height = Math.max(0, Math.min(rect.bottom, window.innerHeight) - Math.max(rect.top, 0));
  const visibleArea = width * height;
  const contentArea = rect.width * rect.height;
  const viewportArea = window.innerWidth * window.innerHeight;
  return contentArea > 0 && visibleArea >= Math.min(contentArea, viewportArea) * 0.25;
}

export function useCardViewTracking(cardId: number, kind: 'media' | 'collection') {
  const contentRef = React.useRef<HTMLDivElement>(null);
  const sentRef = React.useRef(false);

  React.useEffect(() => {
    const content = contentRef.current;
    if (!content) return;

    sentRef.current = false;
    let videoPlaying = false;
    let startedAt: number | null = null;
    let timer: ReturnType<typeof setTimeout> | null = null;

    function hasVisibleContent() {
      if (document.visibilityState !== 'visible') return false;

      if (kind === 'media') {
        const video = content!.querySelector('video');
        if (video) {
          return videoPlaying && !video.paused && !video.ended && isMeaningfullyVisible(video);
        }
      }

      return Array.from(content!.querySelectorAll('img')).some(
        (image) => image.complete && image.naturalWidth > 0 && isMeaningfullyVisible(image)
      );
    }

    function updateDwell() {
      if (sentRef.current) return;
      if (!hasVisibleContent()) {
        startedAt = null;
        if (timer !== null) clearTimeout(timer);
        timer = null;
        return;
      }
      if (startedAt !== null) return;

      startedAt = performance.now();
      timer = setTimeout(() => {
        timer = null;
        if (!hasVisibleContent() || startedAt === null || sentRef.current) {
          updateDwell();
          return;
        }
        const durationMs = Math.min(600000, Math.max(minimumDwellMs, Math.round(performance.now() - startedAt)));
        sentRef.current = true;
        void recordCardView(cardId, crypto.randomUUID(), durationMs).catch(() => {
          // A view is attempted once per opening; avoid duplicate events after a network failure.
        });
      }, minimumDwellMs);
    }

    function onPlaying() {
      videoPlaying = true;
      updateDwell();
    }

    function onPlaybackStopped() {
      videoPlaying = false;
      updateDwell();
    }

    content.addEventListener('load', updateDwell, true);
    content.addEventListener('playing', onPlaying, true);
    content.addEventListener('pause', onPlaybackStopped, true);
    content.addEventListener('waiting', onPlaybackStopped, true);
    content.addEventListener('ended', onPlaybackStopped, true);
    window.addEventListener('scroll', updateDwell, { passive: true });
    window.addEventListener('resize', updateDwell);
    document.addEventListener('visibilitychange', updateDwell);
    updateDwell();

    return () => {
      if (timer !== null) clearTimeout(timer);
      content.removeEventListener('load', updateDwell, true);
      content.removeEventListener('playing', onPlaying, true);
      content.removeEventListener('pause', onPlaybackStopped, true);
      content.removeEventListener('waiting', onPlaybackStopped, true);
      content.removeEventListener('ended', onPlaybackStopped, true);
      window.removeEventListener('scroll', updateDwell);
      window.removeEventListener('resize', updateDwell);
      document.removeEventListener('visibilitychange', updateDwell);
    };
  }, [cardId, kind]);

  return contentRef;
}
