import * as React from 'react';
import { X } from 'lucide-react';

import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';

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
  if (!open) {
    return null;
  }

  return (
    <div
      className="fixed inset-0 z-50 grid place-items-center bg-black/40 p-4"
      role="presentation"
      onMouseDown={() => onOpenChange(false)}
    >
      <div
        role="dialog"
        aria-modal="true"
        aria-labelledby="dialog-title"
        aria-describedby={description ? 'dialog-description' : undefined}
        className={cn(
          'bg-background text-foreground grid w-full max-w-md gap-4 rounded-lg border p-5 shadow-lg'
        )}
        onMouseDown={(event) => event.stopPropagation()}
      >
        <div className="flex items-start justify-between gap-4">
          <div className="grid gap-1">
            <h2 id="dialog-title" className="text-lg font-semibold">
              {title}
            </h2>
            {description ? (
              <p id="dialog-description" className="text-muted-foreground text-sm">
                {description}
              </p>
            ) : null}
          </div>
          <Button type="button" variant="ghost" size="icon" onClick={() => onOpenChange(false)}>
            <X className="size-4" />
          </Button>
        </div>
        {children}
      </div>
    </div>
  );
}
