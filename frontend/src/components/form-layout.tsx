import * as React from 'react';
import { ChevronRight, Info } from 'lucide-react';

import { Tooltip } from '@/components/ui/tooltip';
import { cn } from '@/lib/utils';

/** Page wrapper for create/edit forms: a single readable column with a title. */
export function FormPage({
  title,
  children,
  className,
}: {
  title: React.ReactNode;
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <section className={cn('mx-auto flex w-full max-w-3xl flex-col gap-5 p-4 sm:p-6', className)}>
      <h1 className="text-2xl font-semibold">{title}</h1>
      {children}
    </section>
  );
}

/** A titled group of related fields. */
export function FormSection({
  title,
  action,
  hint,
  children,
  className,
}: {
  title?: React.ReactNode;
  action?: React.ReactNode;
  /** Explanation shown in a tooltip next to the title instead of a paragraph. */
  hint?: string;
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <div className={cn('bg-surface grid gap-4 rounded-2xl border p-4', className)}>
      {title || action ? (
        <div className="flex min-h-9 items-center justify-between gap-3">
          <h2 className="flex items-center gap-1.5 text-sm font-semibold">
            {title}
            {hint ? <InfoHint label={hint} /> : null}
          </h2>
          {action}
        </div>
      ) : null}
      {children}
    </div>
  );
}

export function InfoHint({ label }: { label: string }) {
  return (
    <Tooltip label={label} side="top">
      <button type="button" aria-label={label}
        className="text-muted-foreground hover:text-foreground inline-flex size-6 items-center justify-center rounded-full pointer-coarse:size-9">
        <Info className="size-4" />
      </button>
    </Tooltip>
  );
}

/** Collapsed-by-default section for rarely needed fields. */
export function Advanced({
  summary = 'Advanced',
  defaultOpen,
  children,
}: {
  summary?: React.ReactNode;
  defaultOpen?: boolean;
  children: React.ReactNode;
}) {
  return (
    <details className="bg-surface group rounded-2xl border" open={defaultOpen ? true : undefined}>
      <summary className="flex min-h-11 cursor-pointer list-none items-center gap-2 px-4 text-sm font-medium [&::-webkit-details-marker]:hidden">
        <ChevronRight className="text-muted-foreground size-4 transition-transform group-open:rotate-90" />
        {summary}
      </summary>
      <div className="grid gap-4 px-4 pb-4">{children}</div>
    </details>
  );
}

/** Sticky bottom bar for Save / Cancel; sits above the mobile tab bar. */
export function FormActions({
  error,
  children,
}: {
  error?: string | null;
  children: React.ReactNode;
}) {
  return (
    <div className="bg-background/90 sticky bottom-[calc(3.5rem+env(safe-area-inset-bottom))] z-30 -mx-4 grid gap-2 border-t px-4 py-3 backdrop-blur-md sm:mx-0 sm:rounded-2xl sm:border md:bottom-4">
      {error ? <p role="alert" className="text-destructive text-sm">{error}</p> : null}
      <div className="flex justify-end gap-2">{children}</div>
    </div>
  );
}

export function relationLabel(type: string) {
  return type.replace(/_/g, ' ');
}
