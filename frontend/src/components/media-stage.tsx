import * as React from 'react';

import { cn } from '@/lib/utils';

/**
 * Dark neutral surround for the card's media. Full-bleed on narrow screens, rounded panel from `sm` up.
 * Children (MediaViewer, SetViewer, ComicReader) draw their own overlay toolbars on top of it.
 */
export function MediaStage({
  children,
  className,
  ...props
}: React.ComponentProps<'div'>) {
  return (
    <div
      className={cn(
        'bg-stage text-stage-foreground min-w-0 overflow-hidden sm:rounded-[var(--radius-panel)]',
        className
      )}
      {...props}
    >
      {children}
    </div>
  );
}

/** Round overlay button style shared by stage toolbars. */
export const stageButtonClass =
  'bg-stage/70 text-stage-foreground hover:bg-stage-foreground/20 focus-visible:ring-ring inline-flex size-9 items-center justify-center rounded-full backdrop-blur outline-none transition-colors duration-[var(--dur-fast)] focus-visible:ring-2 pointer-coarse:size-11 disabled:opacity-40 [&_svg]:size-4';
