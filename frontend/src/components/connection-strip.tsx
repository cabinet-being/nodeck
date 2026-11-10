import * as React from 'react';
import { Plus } from 'lucide-react';

import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';

export type ConnectionGroup = {
  key: string;
  label: string;
  icon: React.ComponentType<{ className?: string }>;
  nodes: React.ReactNode[];
  /** Total shown in the header when only part of the nodes are rendered. */
  count?: number;
  /** Shows a "+" in the group header. */
  onAdd?: () => void;
  addLabel?: string;
  /** Extra header controls (menus, select toggles). */
  actions?: React.ReactNode;
  /** Short text shown when the group has no nodes. */
  empty?: string;
  /** Rendered after the nodes (for example "show all"). */
  footer?: React.ReactNode;
  /** Laid out as one node per row (used for review lists on touch screens). */
  stacked?: boolean;
  /** Hide the group when it has no nodes and cannot be added to. */
  hideWhenEmpty?: boolean;
};

/** Vertical spine with labelled port groups; each group lists the card's connected nodes. */
export function ConnectionStrip({
  groups,
  footer,
  className,
}: {
  groups: ConnectionGroup[];
  footer?: React.ReactNode;
  className?: string;
}) {
  const visible = groups.filter((group) => !(group.hideWhenEmpty && group.nodes.length === 0 && !group.onAdd));
  return (
    <div className={cn('relative grid gap-5', className)}>
      <span aria-hidden="true" className="bg-border absolute top-3 bottom-3 left-3 w-px" />
      {visible.map((group) => {
        const Icon = group.icon;
        return (
          <section key={group.key} aria-label={group.label} className="relative grid gap-2 pl-9">
            <span aria-hidden="true"
              className="bg-surface text-muted-foreground absolute top-0 left-0 inline-flex size-6 items-center justify-center rounded-full border pointer-coarse:top-2.5 [&_svg]:size-3.5">
              <Icon />
            </span>
            <div className="flex min-h-6 items-center justify-between gap-2 pointer-coarse:min-h-11">
              <h3 className="text-sm font-semibold">
                {group.label}
                {(group.count ?? group.nodes.length) > 0 ? (
                  <span className="text-muted-foreground ml-1.5 text-xs font-normal tabular-nums">{group.count ?? group.nodes.length}</span>
                ) : null}
              </h3>
              <div className="flex items-center gap-0.5">
                {group.actions}
                {group.onAdd ? (
                  <Button type="button" size="icon-sm" variant="ghost" aria-label={group.addLabel ?? `Add ${group.label.toLowerCase()}`}
                    className="pointer-coarse:size-11" onClick={group.onAdd}>
                    <Plus className="size-4" />
                  </Button>
                ) : null}
              </div>
            </div>
            {group.nodes.length > 0 ? (
              <div className={cn('flex flex-wrap items-center gap-2', group.stacked && 'pointer-coarse:grid pointer-coarse:gap-1')}>
                {group.nodes.map((node, index) => <React.Fragment key={index}>{node}</React.Fragment>)}
              </div>
            ) : group.empty ? (
              <p className="text-muted-foreground text-xs">{group.empty}</p>
            ) : null}
            {group.footer}
          </section>
        );
      })}
      {footer ? <div className="relative pl-9">{footer}</div> : null}
    </div>
  );
}
