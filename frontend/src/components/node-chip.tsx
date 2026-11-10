import * as React from 'react';
import {
  BookOpen,
  Brush,
  FileVideo,
  Globe,
  Hash,
  Info,
  Layers,
  ListFilter,
  Sparkle,
  User,
  Wand2,
  X,
} from 'lucide-react';

import { cn } from '@/lib/utils';

export type NodeChipKind = 'tag' | 'source' | 'deck' | 'card' | 'filter' | 'file';

type Tone = { icon: React.ComponentType<{ className?: string }>; label: string; tone: string };

const categoryTones: Record<string, Tone> = {
  general: { icon: Hash, label: 'general', tone: 'bg-tag-general-soft text-tag-general border-tag-general/30' },
  artist: { icon: Brush, label: 'artist', tone: 'bg-tag-artist-soft text-tag-artist border-tag-artist/30' },
  character: { icon: User, label: 'character', tone: 'bg-tag-character-soft text-tag-character border-tag-character/30' },
  copyright: { icon: BookOpen, label: 'copyright', tone: 'bg-tag-copyright-soft text-tag-copyright border-tag-copyright/30' },
  meta: { icon: Info, label: 'meta', tone: 'bg-tag-meta-soft text-tag-meta border-tag-meta/30' },
  user: { icon: Sparkle, label: 'custom', tone: 'bg-tag-user-soft text-tag-user border-tag-user/30' },
};

const neutralTone = 'bg-muted text-foreground border-border';

function resolveTone(
  kind: NodeChipKind,
  category: string | undefined,
  origin: string | undefined
): Tone {
  switch (kind) {
    case 'tag':
      if (origin === 'user') return categoryTones.user;
      return categoryTones[category ?? 'general'] ?? categoryTones.general;
    case 'source':
      return { icon: Globe, label: 'source', tone: neutralTone };
    case 'deck':
      return { icon: Layers, label: 'deck', tone: neutralTone };
    case 'card':
      return { icon: Hash, label: 'card', tone: neutralTone };
    case 'file':
      return { icon: FileVideo, label: 'file', tone: categoryTones.meta.tone };
    default:
      return { icon: ListFilter, label: 'filter', tone: neutralTone };
  }
}

export function NodeChip({
  kind = 'tag',
  label,
  category,
  origin,
  suggested = false,
  href,
  onNavigate,
  onRemove,
  removeLabel,
  size = 'md',
  className,
}: {
  kind?: NodeChipKind;
  label: string;
  category?: string;
  origin?: string;
  /** Automatic suggestion: dashed outline and a wand glyph until accepted. */
  suggested?: boolean;
  href?: string;
  onNavigate?: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onRemove?: () => void;
  removeLabel?: string;
  size?: 'sm' | 'md';
  className?: string;
}) {
  const tone = resolveTone(kind, category, origin);
  const Glyph = suggested ? Wand2 : tone.icon;
  const typeLabel = suggested ? `suggested ${tone.label}` : tone.label;
  const ariaLabel = `${typeLabel}: ${label}`;
  const body = (
    <>
      <span
        aria-hidden="true"
        className={cn(
          'bg-surface/80 inline-flex shrink-0 items-center justify-center border border-current/30',
          kind === 'file' ? 'rounded-[3px]' : 'rounded-full',
          size === 'sm' ? 'size-3.5 [&_svg]:size-2' : 'size-4 [&_svg]:size-2.5'
        )}
      >
        <Glyph />
      </span>
      <span className="truncate">{label}</span>
    </>
  );
  const shell = cn(
    'inline-flex max-w-full min-w-0 items-center gap-1.5 rounded-full border font-medium',
    size === 'sm' ? 'h-6 px-1.5 text-xs' : 'h-7 px-2 text-[13px]',
    suggested ? 'border-dashed' : '',
    tone.tone,
    className
  );

  const content = href ? (
    <a
      href={href}
      onClick={onNavigate}
      aria-label={ariaLabel}
      title={ariaLabel}
      className="inline-flex min-w-0 items-center gap-1.5 rounded-full hover:underline"
    >
      {body}
    </a>
  ) : (
    <span aria-label={ariaLabel} title={ariaLabel} className="inline-flex min-w-0 items-center gap-1.5">
      {body}
    </span>
  );

  return (
    <span className={cn(shell, onRemove && 'pr-0.5')}>
      {content}
      {onRemove ? (
        <button
          type="button"
          aria-label={removeLabel ?? `Remove ${label}`}
          title={removeLabel ?? `Remove ${label}`}
          onClick={onRemove}
          className="hover:bg-foreground/10 inline-flex size-5 shrink-0 items-center justify-center rounded-full pointer-coarse:size-7"
        >
          <X className="size-3" />
        </button>
      ) : null}
    </span>
  );
}
