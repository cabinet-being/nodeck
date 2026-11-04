import { cn } from '@/lib/utils';

const categoryClasses: Record<string, string> = {
  general: 'border-slate-500/40 bg-slate-500/10 text-slate-700 dark:text-slate-200',
  artist: 'border-red-500/40 bg-red-500/10 text-red-700 dark:text-red-300',
  character: 'border-green-500/40 bg-green-500/10 text-green-700 dark:text-green-300',
  copyright: 'border-violet-500/40 bg-violet-500/10 text-violet-700 dark:text-violet-300',
  meta: 'border-orange-500/40 bg-orange-500/10 text-orange-700 dark:text-orange-300',
};

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
  const tone = origin === 'user'
    ? 'border-teal-500/40 bg-teal-500/10 text-teal-700 dark:text-teal-300'
    : categoryClasses[category] ?? categoryClasses.general;
  return (
    <span className={cn('inline-flex items-center rounded-full border px-2.5 py-1 text-xs font-medium', tone, className)}>
      {name}
    </span>
  );
}
