import * as React from 'react';
import { Dialog as DialogPrimitive } from '@base-ui/react/dialog';
import { X } from 'lucide-react';

import { Button } from '@/components/ui/button';

export function Dialog({
  open,
  title,
  description,
  children,
  onOpenChange,
}: {
  open: boolean;
  title: string;
  description?: string;
  children: React.ReactNode;
  onOpenChange: (open: boolean) => void;
}) {
  return (
    <DialogPrimitive.Root open={open} onOpenChange={(next) => onOpenChange(next)}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Backdrop className="fixed inset-0 z-50 bg-black/40 backdrop-blur-[2px] transition-opacity duration-200 data-[ending-style]:opacity-0 data-[starting-style]:opacity-0" />
        <DialogPrimitive.Viewport className="fixed inset-0 z-50 grid place-items-center overflow-y-auto p-4">
          <DialogPrimitive.Popup className="bg-popover text-popover-foreground shadow-3 grid w-full max-w-md gap-4 rounded-[var(--radius-panel)] border p-5 outline-none transition-[opacity,transform] duration-200 ease-out data-[ending-style]:scale-95 data-[ending-style]:opacity-0 data-[starting-style]:scale-95 data-[starting-style]:opacity-0">
            <div className="flex items-start justify-between gap-4">
              <div className="grid gap-1">
                <DialogPrimitive.Title className="text-lg font-semibold">{title}</DialogPrimitive.Title>
                {description ? (
                  <DialogPrimitive.Description className="text-muted-foreground text-sm">
                    {description}
                  </DialogPrimitive.Description>
                ) : null}
              </div>
              <DialogPrimitive.Close
                render={<Button type="button" variant="ghost" size="icon" aria-label="Close" />}
              >
                <X className="size-4" />
              </DialogPrimitive.Close>
            </div>
            {children}
          </DialogPrimitive.Popup>
        </DialogPrimitive.Viewport>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  );
}
