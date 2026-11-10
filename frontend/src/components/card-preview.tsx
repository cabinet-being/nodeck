import * as React from 'react';

import { resolveApiUrl } from '@/api/cards';
import { cn } from '@/lib/utils';

export function CardPreview({
  url,
  title,
  type,
  imageClassName,
  fallbackClassName,
  onLoad,
}: {
  url: string | null;
  title: string | null;
  type: string;
  imageClassName: string;
  fallbackClassName: string;
  /** Called once the image has loaded (the image fades in at that moment). */
  onLoad?: () => void;
}) {
  const [failedUrl, setFailedUrl] = React.useState<string | null>(null);
  const [loadedUrl, setLoadedUrl] = React.useState<string | null>(null);

  if (!url || failedUrl === url) {
    return (
      <div className={cn('flex items-center justify-center p-3 text-center text-sm text-muted-foreground', fallbackClassName)}>
        {type}
      </div>
    );
  }

  return <img src={resolveApiUrl(url)} alt={title ?? ''} loading="lazy"
    className={cn(imageClassName, 'transition-opacity duration-[var(--dur-base)] motion-reduce:transition-none',
      loadedUrl === url ? 'opacity-100' : 'opacity-0')}
    ref={(node) => { if (node?.complete && node.naturalWidth > 0 && loadedUrl !== url) queueMicrotask(() => { setLoadedUrl(url); onLoad?.(); }); }}
    onLoad={() => { setLoadedUrl(url); onLoad?.(); }} onError={() => setFailedUrl(url)} />;
}
