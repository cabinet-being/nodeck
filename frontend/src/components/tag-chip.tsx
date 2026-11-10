import { NodeChip } from '@/components/node-chip';

export function TagChip({
  name,
  category,
  origin,
  className,
}: {
  name: string;
  category: string;
  origin: string;
  className?: string;
}) {
  return <NodeChip kind="tag" label={name} category={category} origin={origin} className={className} />;
}
