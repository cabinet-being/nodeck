import * as React from 'react';

import { cn } from '@/lib/utils';

export type SegmentedOption<T extends string> = {
  value: T;
  label: React.ReactNode;
  icon?: React.ReactNode;
};

export function Segmented<T extends string>({
  value,
  onValueChange,
  options,
  ariaLabel,
  className,
}: {
  value: T;
  onValueChange: (value: T) => void;
  options: SegmentedOption<T>[];
  ariaLabel: string;
  className?: string;
}) {
  return (
    <div
      role="radiogroup"
      aria-label={ariaLabel}
      className={cn('bg-muted inline-flex items-center gap-0.5 rounded-[var(--radius-button)] p-0.5', className)}
    >
      {options.map((option) => {
        const active = option.value === value;
        return (
          <button
            key={option.value}
            type="button"
            role="radio"
            aria-checked={active}
            onClick={() => onValueChange(option.value)}
            className={cn(
              'inline-flex h-8 items-center gap-1.5 rounded-lg px-3 text-sm font-medium transition-[background-color,color,box-shadow] duration-[var(--dur-fast)] [&_svg]:size-4',
              active ? 'bg-surface text-foreground shadow-1' : 'text-muted-foreground hover:text-foreground'
            )}
          >
            {option.icon}
            {option.label}
          </button>
        );
      })}
    </div>
  );
}
