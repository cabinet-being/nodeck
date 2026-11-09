import * as React from 'react';

import { resolveApiUrl } from '@/api/cards';
import { cn } from '@/lib/utils';

export function CardPreview({
  url,
  title,
  type,
  imageClassName,
  fallbackClassName,
}: {
  url: string | null;
  title: string | null;
  type: string;
  imageClassName: string;
  fallbackClassName: string;
}) {
  const [failedUrl, setFailedUrl] = React.useState<string | null>(null);

  if (!url || failedUrl === url) {
    return (
      <div className={cn('flex items-center justify-center p-3 text-center text-sm text-muted-foreground', fallbackClassName)}>
        {type}
      </div>
    );
  }

  return <img src={resolveApiUrl(url)} alt={title ?? ''} loading="lazy"
    className={imageClassName} onError={() => setFailedUrl(url)} />;
}
