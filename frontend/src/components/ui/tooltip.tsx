import * as React from 'react';
import { Tooltip as TooltipPrimitive } from '@base-ui/react/tooltip';

import { cn } from '@/lib/utils';

export const TooltipProvider = TooltipPrimitive.Provider;

export function Tooltip({
  label,
  children,
  side = 'bottom',
  className,
}: {
  label: React.ReactNode;
  children: React.ReactElement<Record<string, unknown>>;
  side?: 'top' | 'bottom' | 'left' | 'right';
  className?: string;
}) {
  return (
    <TooltipPrimitive.Root>
      <TooltipPrimitive.Trigger render={children} />
      <TooltipPrimitive.Portal>
        <TooltipPrimitive.Positioner side={side} sideOffset={6} className="z-[60]">
          <TooltipPrimitive.Popup
            className={cn(
              'bg-foreground text-background shadow-2 rounded-md px-2 py-1 text-xs transition-opacity duration-150 data-[ending-style]:opacity-0 data-[starting-style]:opacity-0',
              className
            )}
          >
            {label}
          </TooltipPrimitive.Popup>
        </TooltipPrimitive.Positioner>
      </TooltipPrimitive.Portal>
    </TooltipPrimitive.Root>
  );
}
