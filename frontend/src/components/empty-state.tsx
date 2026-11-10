import * as React from 'react';

import { cn } from '@/lib/utils';

export function EmptyState({
  icon: Icon,
  title,
  action,
  className,
}: {
  icon?: React.ComponentType<{ className?: string }>;
  title: React.ReactNode;
  action?: React.ReactNode;
  className?: string;
}) {
  return (
    <div className={cn('flex flex-col items-center justify-center gap-4 px-4 py-16 text-center', className)}>
      {Icon ? (
        <span className="bg-muted text-muted-foreground flex size-12 items-center justify-center rounded-2xl border border-dashed">
          <Icon className="size-5" />
        </span>
      ) : null}
      <p className="text-base font-medium">{title}</p>
      {action}
    </div>
  );
}
