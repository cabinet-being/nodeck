import * as React from 'react';
import { Dialog as DialogPrimitive } from '@base-ui/react/dialog';
import { X } from 'lucide-react';

import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';

export function Sheet({
  open,
  onOpenChange,
  title,
  side = 'bottom',
  children,
  className,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  side?: 'bottom' | 'right';
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <DialogPrimitive.Root open={open} onOpenChange={(next) => onOpenChange(next)}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Backdrop className="fixed inset-0 z-50 bg-black/40 transition-opacity duration-[var(--dur-slow)] data-[ending-style]:opacity-0 data-[starting-style]:opacity-0" />
        <DialogPrimitive.Popup
          className={cn(
            'bg-popover text-popover-foreground shadow-3 fixed z-50 grid gap-4 overflow-y-auto border p-5 outline-none transition-transform duration-[var(--dur-slow)] ease-out',
            side === 'bottom'
              ? 'inset-x-0 bottom-0 max-h-[85vh] rounded-t-[var(--radius-panel)] border-b-0 pb-[calc(1.25rem+env(safe-area-inset-bottom))] data-[ending-style]:translate-y-full data-[starting-style]:translate-y-full'
              : 'inset-y-0 right-0 w-full max-w-md rounded-l-[var(--radius-panel)] border-r-0 data-[ending-style]:translate-x-full data-[starting-style]:translate-x-full',
            className
          )}
        >
          <div className="flex items-center justify-between gap-4">
            <DialogPrimitive.Title className="text-lg font-semibold">{title}</DialogPrimitive.Title>
            <DialogPrimitive.Close render={<Button type="button" variant="ghost" size="icon" aria-label="Close" />}>
              <X />
            </DialogPrimitive.Close>
          </div>
          {children}
        </DialogPrimitive.Popup>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  );
}
