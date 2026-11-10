import * as React from 'react';
import { Expand, Maximize2, Minimize2, Shrink } from 'lucide-react';

import { resolveApiUrl } from '@/api/cards';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';

type MediaViewerProps = {
  kind: 'image' | 'video';
  src: string;
  alt: string;
  posterUrl?: string | null;
  /** Fallback shown when the image fails to load. */
  fallbackSrc?: string | null;
  contentRef?: React.Ref<HTMLDivElement>;
  className?: string;
  /** Extra controls rendered in the toolbar. */
  toolbar?: React.ReactNode;
};

export function MediaViewer({ kind, src, alt, posterUrl, fallbackSrc, contentRef, className, toolbar }: MediaViewerProps) {
  const frameRef = React.useRef<HTMLDivElement>(null);
  const [actualSize, setActualSize] = React.useState(false);
  const [fullscreen, setFullscreen] = React.useState(false);
  const [failed, setFailed] = React.useState(false);
  const canFullscreen = typeof document !== 'undefined' && document.fullscreenEnabled === true;

  React.useEffect(() => { setFailed(false); setActualSize(false); }, [src]);

  React.useEffect(() => {
    function sync() { setFullscreen(document.fullscreenElement === frameRef.current); }
    document.addEventListener('fullscreenchange', sync);
    return () => document.removeEventListener('fullscreenchange', sync);
  }, []);

  function toggleFullscreen() {
    if (document.fullscreenElement) void document.exitFullscreen();
    else void frameRef.current?.requestFullscreen?.().catch(() => undefined);
  }

  const imageSrc = failed && fallbackSrc ? fallbackSrc : src;

  return (
    <div ref={frameRef} className={cn('grid min-w-0 gap-2', fullscreen && 'bg-background h-dvh content-start p-2', className)}>
      <div className="flex flex-wrap items-center justify-end gap-2">
        {toolbar}
        {kind === 'image' ? (
          <Button type="button" size="sm" variant="outline" aria-pressed={actualSize} onClick={() => setActualSize((value) => !value)}
            aria-label={actualSize ? 'Fit image to screen' : 'Show image at actual size'}>
            {actualSize ? <Shrink className="size-4" /> : <Expand className="size-4" />}
            {actualSize ? 'Fit' : 'Actual size'}
          </Button>
        ) : null}
        {canFullscreen ? (
          <Button type="button" size="sm" variant="outline" onClick={toggleFullscreen}
            aria-label={fullscreen ? 'Exit fullscreen' : 'Enter fullscreen'}>
            {fullscreen ? <Minimize2 className="size-4" /> : <Maximize2 className="size-4" />}
          </Button>
        ) : null}
      </div>
      <div
        ref={contentRef}
        className={cn(
          'bg-muted flex items-center justify-center rounded-xl',
          kind === 'image' && actualSize ? 'overflow-auto' : 'overflow-hidden',
          fullscreen ? 'min-h-0 flex-1' : 'min-h-[20rem] sm:min-h-[28rem]',
        )}
        style={fullscreen ? { maxHeight: 'calc(100dvh - 4rem)' } : { maxHeight: 'calc(100dvh - 8rem)' }}
      >
        {kind === 'video' ? (
          <video
            src={resolveApiUrl(src)}
            poster={posterUrl ? resolveApiUrl(posterUrl) : undefined}
            controls
            playsInline
            preload="metadata"
            className={cn('max-w-full object-contain', fullscreen ? 'max-h-[calc(100dvh-4rem)]' : 'max-h-[calc(100dvh-8rem)]')}
          />
        ) : (
          <img
            src={resolveApiUrl(imageSrc)}
            alt={alt}
            onError={() => { if (fallbackSrc) setFailed(true); }}
            className={cn(
              actualSize
                ? 'max-h-none max-w-none'
                : cn('max-w-full object-contain', fullscreen ? 'max-h-[calc(100dvh-4rem)]' : 'max-h-[calc(100dvh-8rem)]'),
            )}
          />
        )}
      </div>
    </div>
  );
}
