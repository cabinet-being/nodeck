import * as React from 'react';
import { Checkbox as CheckboxPrimitive } from '@base-ui/react/checkbox';
import { Check } from 'lucide-react';

import { cn } from '@/lib/utils';

export function Checkbox({ className, ...props }: CheckboxPrimitive.Root.Props) {
  return (
    <CheckboxPrimitive.Root
      className={cn(
        'border-input bg-surface focus-visible:ring-ring/50 data-[checked]:bg-primary data-[checked]:border-primary data-[checked]:text-primary-foreground inline-flex size-5 shrink-0 items-center justify-center rounded-md border outline-none focus-visible:ring-3 data-[disabled]:opacity-50',
        className
      )}
      {...props}
    >
      <CheckboxPrimitive.Indicator className="inline-flex">
        <Check className="size-3.5" />
      </CheckboxPrimitive.Indicator>
    </CheckboxPrimitive.Root>
  );
}
