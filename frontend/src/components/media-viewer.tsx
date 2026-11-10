import * as React from 'react';
import { Download, Expand, Maximize2, Minimize2, Shrink } from 'lucide-react';

import { resolveApiUrl } from '@/api/cards';
import { stageButtonClass } from '@/components/media-stage';
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
  /** Extra controls rendered in the overlay toolbar. */
  toolbar?: React.ReactNode;
  /** When set, the overlay toolbar offers a download link for the original file. */
  originalUrl?: string | null;
  /** Wraps the image (for example in a link); receives the current fit/actual-size state. */
  wrapImage?: (image: React.ReactNode, state: { actualSize: boolean }) => React.ReactNode;
  /** Extra overlay content positioned against the viewer frame (stays visible in fullscreen). */
  overlay?: React.ReactNode | ((state: { actualSize: boolean }) => React.ReactNode);
};

export function MediaViewer({ kind, src, alt, posterUrl, fallbackSrc, contentRef, className, toolbar, originalUrl, wrapImage, overlay }: MediaViewerProps) {
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
  const image = (
    <img
            src={resolveApiUrl(imageSrc)}
            alt={alt}
            onError={() => { if (fallbackSrc) setFailed(true); }}
            className={cn(
              actualSize
                ? 'max-h-none max-w-none'
                : cn('max-w-full object-contain', fullscreen ? 'max-h-dvh' : 'max-h-[calc(100dvh-7rem)]'),
            )}
          />
  );

  return (
    <div ref={frameRef} className={cn('bg-stage text-stage-foreground relative min-w-0', fullscreen && 'h-dvh', className)}>
      <div className="absolute top-3 right-3 z-10 flex items-center gap-2">
        {toolbar}
        {kind === 'image' ? (
          <button type="button" className={stageButtonClass} aria-pressed={actualSize} onClick={() => setActualSize((value) => !value)}
            aria-label={actualSize ? 'Fit image to screen' : 'Show image at actual size'}
            title={actualSize ? 'Fit to screen' : 'Actual size'}>
            {actualSize ? <Shrink /> : <Expand />}
          </button>
        ) : null}
        {canFullscreen ? (
          <button type="button" className={stageButtonClass} onClick={toggleFullscreen}
            aria-label={fullscreen ? 'Exit fullscreen' : 'Enter fullscreen'}
            title={fullscreen ? 'Exit fullscreen' : 'Fullscreen'}>
            {fullscreen ? <Minimize2 /> : <Maximize2 />}
          </button>
        ) : null}
        {originalUrl ? (
          <a href={resolveApiUrl(originalUrl)} download className={stageButtonClass}
            aria-label="Download original" title="Download original">
            <Download />
          </a>
        ) : null}
      </div>
      <div
        ref={contentRef}
        className={cn(
          'flex items-center justify-center',
          kind === 'image' && actualSize ? 'overflow-auto' : 'overflow-hidden',
          fullscreen ? 'h-full' : 'min-h-[16rem] sm:min-h-[28rem]',
        )}
        style={fullscreen ? undefined : { maxHeight: 'calc(100dvh - 7rem)' }}
      >
        {kind === 'video' ? (
          <video
            src={resolveApiUrl(src)}
            poster={posterUrl ? resolveApiUrl(posterUrl) : undefined}
            controls
            playsInline
            preload="metadata"
            className={cn('max-w-full object-contain', fullscreen ? 'max-h-dvh' : 'max-h-[calc(100dvh-7rem)]')}
          />
        ) : (
          wrapImage ? wrapImage(image, { actualSize }) : image
        )}
      </div>
      {typeof overlay === 'function' ? overlay({ actualSize }) : overlay}
    </div>
  );
}
