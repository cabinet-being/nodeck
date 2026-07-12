import * as React from 'react';
import { ExternalLink, Plus, Trash2, Pencil } from 'lucide-react';

import {
  createCardRelation,
  deleteCardRelation,
  listCards,
  resolveApiUrl,
  updateCardRelation,
  type Card,
  type CardRelationEntry,
  type CardRelations,
} from '@/api/cards';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Dialog } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { cn } from '@/lib/utils';

const editableRelationTypes = new Set(['tagged_with', 'sourced_from', 'related_to']);

type PickerMode = 'tag' | 'source' | 'related';

export function RelationDashboard({
  card,
  relations,
  onRelationsChange,
  onNavigate,
}: {
  card: Card;
  relations: CardRelations;
  onRelationsChange: (relations: CardRelations) => void;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const [pickerMode, setPickerMode] = React.useState<PickerMode | null>(null);
  const [pickerSearch, setPickerSearch] = React.useState('');
  const [pickerResults, setPickerResults] = React.useState<Card[]>([]);
  const [pickerError, setPickerError] = React.useState<string | null>(null);
  const [isPickerLoading, setIsPickerLoading] = React.useState(false);
  const [isMutating, setIsMutating] = React.useState(false);
  const [sourceDrafts, setSourceDrafts] = React.useState<Record<number, string>>({});
  const [sourceDraftUrl, setSourceDraftUrl] = React.useState('');
  const [localError, setLocalError] = React.useState<string | null>(null);

  React.useEffect(() => {
    const nextDrafts: Record<number, string> = {};

    relations.outgoingRelations
      .filter((relation) => relation.relationType === 'sourced_from')
      .forEach((relation) => {
        const originalUrl = relation.properties?.original_url;
        nextDrafts[relation.id] = typeof originalUrl === 'string' ? originalUrl : '';
      });

    setSourceDrafts((current) => ({ ...nextDrafts, ...current }));
  }, [relations]);

  React.useEffect(() => {
    if (!pickerMode) {
      return;
    }

    let active = true;
    setPickerError(null);
    setIsPickerLoading(true);

    listCards({
      search: pickerSearch || undefined,
      types: pickerMode === 'tag' ? ['tag'] : pickerMode === 'source' ? ['source'] : undefined,
      sort: 'created_at',
      order: 'desc',
    })
      .then((results) => {
        if (!active) {
          return;
        }

        setPickerResults(results.filter((result) => result.id !== card.id));
      })
      .catch((requestError) => {
        if (!active) {
          return;
        }

        setPickerError(requestError.message);
      })
      .finally(() => {
        if (active) {
          setIsPickerLoading(false);
        }
      });

    return () => {
      active = false;
    };
  }, [card.id, pickerMode, pickerSearch]);

  const tags = relations.outgoingRelations.filter((relation) => relation.relationType === 'tagged_with');
  const sources = relations.outgoingRelations.filter((relation) => relation.relationType === 'sourced_from');
  const relatedCards = dedupeRelations([
    ...relations.outgoingRelations.filter((relation) => relation.relationType === 'related_to'),
    ...relations.incomingRelations.filter((relation) => relation.relationType === 'related_to'),
  ]);
  const structuralRelations = [
    ...relations.outgoingRelations.filter((relation) => relation.relationType === 'contains' || relation.relationType === 'next_in_sequence'),
    ...relations.incomingRelations.filter((relation) => relation.relationType === 'contains' || relation.relationType === 'next_in_sequence'),
  ];
  const backlinks = relations.incomingRelations.filter((relation) =>
    editableRelationTypes.has(relation.relationType)
  );

  return (
    <section className="grid gap-4">
      {localError ? <div className="text-destructive text-sm">{localError}</div> : null}

      <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_20rem]">
        <div className="grid gap-4">
          <RelationSection
            title="Tags"
            emptyMessage="No tags"
            actionLabel="Add tag"
            onAction={() => setPickerMode('tag')}
          >
            {tags.map((relation) => (
              <RelationCardRow
                key={relation.id}
                relation={relation}
                onNavigate={onNavigate}
                onRemove={() => handleRemove(relation.id)}
              />
            ))}
          </RelationSection>

          <RelationSection
            title="Sources"
            emptyMessage="No sources"
            actionLabel="Add source"
            onAction={() => setPickerMode('source')}
          >
            {sources.map((relation) => (
              <SourceRelationRow
                key={relation.id}
                relation={relation}
                draft={sourceDrafts[relation.id] ?? ''}
                isSaving={isMutating}
                onNavigate={onNavigate}
                onDraftChange={(value) =>
                  setSourceDrafts((current) => ({ ...current, [relation.id]: value }))
                }
                onSave={() => handleSaveSource(relation)}
                onRemove={() => handleRemove(relation.id)}
              />
            ))}
          </RelationSection>

          <RelationSection
            title="Related cards"
            emptyMessage="No related cards"
            actionLabel="Add related card"
            onAction={() => setPickerMode('related')}
          >
            {relatedCards.map((relation) => (
              <RelationCardRow
                key={relation.id}
                relation={relation}
                onNavigate={onNavigate}
                onRemove={() => handleRemove(relation.id)}
              />
            ))}
          </RelationSection>

          <RelationSection title="Structural relations" emptyMessage="No structural relations">
            {structuralRelations.map((relation) => (
              <RelationCardRow
                key={relation.id}
                relation={relation}
                onNavigate={onNavigate}
                readOnly
              />
            ))}
          </RelationSection>

          <RelationSection title="Incoming backlinks" emptyMessage="No incoming backlinks">
            {backlinks.map((relation) => (
              <RelationCardRow key={relation.id} relation={relation} onNavigate={onNavigate} readOnly />
            ))}
          </RelationSection>
        </div>

        <div className="grid gap-4 content-start">
          <RelationGraph card={card} relations={relations} onNavigate={onNavigate} />
          <RelationLegend />
        </div>
      </div>

      <Dialog
        open={pickerMode !== null}
        title={
          pickerMode === 'tag'
            ? 'Add tag'
            : pickerMode === 'source'
              ? 'Add source'
              : 'Add related card'
        }
        description="Search by card ID or title, then select a card."
        onOpenChange={(open) => {
          if (!open) {
            setPickerMode(null);
            setPickerSearch('');
            setPickerResults([]);
            setPickerError(null);
            setSourceDraftUrl('');
          }
        }}
      >
        <div className="grid gap-3">
          {pickerMode === 'source' ? (
            <label className="grid gap-2 text-sm font-medium">
              Original URL
              <Input
                value={sourceDraftUrl}
                onChange={(event) => setSourceDraftUrl(event.target.value)}
                placeholder="https://..."
              />
            </label>
          ) : null}
          <Input
            value={pickerSearch}
            placeholder="Search by ID or title"
            onChange={(event) => setPickerSearch(event.target.value)}
          />
          {pickerError ? <div className="text-destructive text-sm">{pickerError}</div> : null}
          <div className="grid max-h-80 gap-2 overflow-auto">
            {isPickerLoading ? (
              <div className="text-muted-foreground text-sm">Loading...</div>
            ) : null}
            {pickerResults.map((result) => (
              <button
                key={result.id}
                type="button"
                className="grid grid-cols-[auto_1fr_auto] items-center gap-3 rounded-lg border p-3 text-left hover:bg-muted/50"
                onClick={() => handlePick(result)}
              >
                {result.previewUrl ? (
                  <img
                    src={resolveApiUrl(result.previewUrl)}
                    alt=""
                    className="bg-muted size-12 rounded border object-cover"
                  />
                ) : (
                  <div className="bg-muted text-muted-foreground grid size-12 place-items-center rounded border text-[10px] uppercase">
                    {result.type}
                  </div>
                )}
                <div className="min-w-0">
                  <div className="truncate font-medium">{result.title ?? result.type}</div>
                  <div className="text-muted-foreground text-sm">#{result.id}</div>
                </div>
                <Badge variant="outline">{result.type}</Badge>
              </button>
            ))}
            {!isPickerLoading && pickerResults.length === 0 ? (
              <div className="text-muted-foreground text-sm">No results</div>
            ) : null}
          </div>
        </div>
      </Dialog>
    </section>
  );

  async function handlePick(selectedCard: Card) {
    if (!pickerMode) {
      return;
    }

    setIsMutating(true);
    setLocalError(null);

    try {
      const relationType =
        pickerMode === 'tag' ? 'tagged_with' : pickerMode === 'source' ? 'sourced_from' : 'related_to';
      const properties =
        pickerMode === 'source'
          ? normalizeOriginalUrl(sourceDraftUrl)
          : null;

      const created = await createCardRelation(card.id, {
        toCardId: selectedCard.id,
        relationType,
        properties,
      });

      onRelationsChange(appendRelation(relations, created));
      setPickerMode(null);
      setPickerSearch('');
      setPickerResults([]);
      setSourceDraftUrl('');
    } catch (requestError) {
      setLocalError(requestError instanceof Error ? requestError.message : 'Unable to add relation.');
    } finally {
      setIsMutating(false);
    }
  }

  async function handleRemove(relationId: number) {
    setIsMutating(true);
    setLocalError(null);

    try {
      await deleteCardRelation(relationId);
      onRelationsChange(removeRelation(relations, relationId));
    } catch (requestError) {
      setLocalError(requestError instanceof Error ? requestError.message : 'Unable to remove relation.');
    } finally {
      setIsMutating(false);
    }
  }

  async function handleSaveSource(relation: CardRelationEntry) {
    setIsMutating(true);
    setLocalError(null);

    try {
      const nextUrl = normalizeOriginalUrl(sourceDrafts[relation.id] ?? '');
      const updated = await updateCardRelation(relation.id, {
        properties: nextUrl,
      });

      onRelationsChange(replaceRelation(relations, updated));
    } catch (requestError) {
      setLocalError(requestError instanceof Error ? requestError.message : 'Unable to update relation.');
    } finally {
      setIsMutating(false);
    }
  }
}

function RelationSection({
  title,
  children,
  emptyMessage,
  actionLabel,
  onAction,
}: {
  title: string;
  children: React.ReactNode;
  emptyMessage: string;
  actionLabel?: string;
  onAction?: () => void;
}) {
  return (
    <section className="grid gap-3 rounded-lg border p-4">
      <div className="flex items-center justify-between gap-3">
        <h2 className="text-base font-semibold">{title}</h2>
        {onAction ? (
          <Button type="button" variant="outline" size="sm" onClick={onAction}>
            <Plus className="size-4" />
            {actionLabel}
          </Button>
        ) : null}
      </div>
      <div className="grid gap-2">{children}</div>
      {React.Children.count(children) === 0 ? (
        <div className="text-muted-foreground text-sm">{emptyMessage}</div>
      ) : null}
    </section>
  );
}

function RelationCardRow({
  relation,
  onNavigate,
  onRemove,
  readOnly = false,
}: {
  relation: CardRelationEntry;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onRemove?: () => void;
  readOnly?: boolean;
}) {
  return (
    <div className="grid gap-2 rounded-lg border p-3 md:grid-cols-[auto_1fr_auto]">
      {relation.relatedCard.previewUrl ? (
        <a href={`/cards/${relation.relatedCard.id}`} onClick={onNavigate}>
          <img
            src={resolveApiUrl(relation.relatedCard.previewUrl)}
            alt={relation.relatedCard.title ?? ''}
            className="bg-muted size-16 rounded border object-cover"
            loading="lazy"
          />
        </a>
      ) : (
        <div className="bg-muted text-muted-foreground grid size-16 place-items-center rounded border text-[10px] uppercase">
          {relation.relatedCard.type}
        </div>
      )}

      <a href={`/cards/${relation.relatedCard.id}`} onClick={onNavigate} className="min-w-0">
        <div className="flex flex-wrap items-center gap-2">
          <Badge variant="outline">{relation.relationType}</Badge>
          <Badge variant="secondary">{relation.direction}</Badge>
        </div>
        <div className="truncate font-medium">{relation.relatedCard.title ?? relation.relatedCard.type}</div>
        <div className="text-muted-foreground text-sm">#{relation.relatedCard.id}</div>
        {relation.properties ? (
          <pre className="bg-muted/50 mt-2 max-h-32 overflow-auto rounded p-2 text-xs">
            {JSON.stringify(relation.properties, null, 2)}
          </pre>
        ) : null}
      </a>

      {!readOnly && onRemove ? (
        <Button type="button" variant="ghost" size="icon" onClick={onRemove}>
          <Trash2 className="size-4" />
        </Button>
      ) : (
        <a
          href={`/cards/${relation.relatedCard.id}`}
          onClick={onNavigate}
          className="text-muted-foreground inline-flex size-8 items-center justify-center rounded-lg border hover:bg-muted"
          aria-label={`Open card ${relation.relatedCard.id}`}
        >
          <ExternalLink className="size-4" />
        </a>
      )}
    </div>
  );
}

function SourceRelationRow({
  relation,
  draft,
  isSaving,
  onNavigate,
  onDraftChange,
  onSave,
  onRemove,
}: {
  relation: CardRelationEntry;
  draft: string;
  isSaving: boolean;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onDraftChange: (value: string) => void;
  onSave: () => void;
  onRemove: () => void;
}) {
  return (
    <div className="grid gap-3 rounded-lg border p-3">
      <RelationCardRow relation={relation} onNavigate={onNavigate} onRemove={onRemove} />
      <div className="grid gap-2 md:grid-cols-[1fr_auto_auto]">
        <Input
          value={draft}
          onChange={(event) => onDraftChange(event.target.value)}
          placeholder="Original URL"
        />
        <Button type="button" variant="outline" disabled={isSaving} onClick={onSave}>
          <Pencil className="size-4" />
          Save
        </Button>
      </div>
    </div>
  );
}

function RelationLegend() {
  return (
    <section className="grid gap-3 rounded-lg border p-4">
      <h2 className="text-base font-semibold">Legend</h2>
      <div className="grid gap-3 text-sm">
        <div className="grid gap-2">
          <div className="text-muted-foreground text-xs uppercase tracking-wide">Card types</div>
          <div className="flex flex-wrap gap-2">
            <LegendPill label="current" tone="slate" />
            <LegendPill label="media" tone="slate" />
            <LegendPill label="comic" tone="orange" />
            <LegendPill label="set" tone="violet" />
            <LegendPill label="tag" tone="green" />
            <LegendPill label="source" tone="blue" />
          </div>
        </div>

        <div className="grid gap-2">
          <div className="text-muted-foreground text-xs uppercase tracking-wide">Relations</div>
          <div className="flex flex-wrap gap-2">
            <LegendPill label="tagged_with" tone="green" />
            <LegendPill label="sourced_from" tone="blue" />
            <LegendPill label="related_to" tone="violet" />
            <LegendPill label="contains" tone="orange" />
            <LegendPill label="next_in_sequence" tone="slate" />
          </div>
        </div>

        <div className="flex flex-wrap gap-2">
          <Badge variant="secondary">outgoing</Badge>
          <span className="text-muted-foreground">relation from the current card</span>
        </div>
        <div className="flex flex-wrap gap-2">
          <Badge variant="outline">incoming</Badge>
          <span className="text-muted-foreground">relation pointing to the current card</span>
        </div>
      </div>
    </section>
  );
}

function LegendPill({
  label,
  tone,
}: {
  label: string;
  tone: 'slate' | 'orange' | 'violet' | 'green' | 'blue';
}) {
  const toneClasses: Record<typeof tone, string> = {
    slate: 'border-slate-500/40 bg-slate-500/15 text-slate-700 dark:text-slate-300',
    orange: 'border-orange-500/40 bg-orange-500/15 text-orange-700 dark:text-orange-300',
    violet: 'border-violet-500/40 bg-violet-500/15 text-violet-700 dark:text-violet-300',
    green: 'border-green-500/40 bg-green-500/15 text-green-700 dark:text-green-300',
    blue: 'border-blue-500/40 bg-blue-500/15 text-blue-700 dark:text-blue-300',
  };

  return <span className={cn('rounded-full border px-2 py-0.5 text-xs font-medium', toneClasses[tone])}>{label}</span>;
}

function dedupeRelations(relations: CardRelationEntry[]) {
  const seen = new Set<number>();
  const next: CardRelationEntry[] = [];

  for (const relation of relations) {
    if (seen.has(relation.relatedCard.id)) {
      continue;
    }

    seen.add(relation.relatedCard.id);
    next.push(relation);
  }

  return next;
}

function appendRelation(relations: CardRelations, relation: CardRelationEntry): CardRelations {
  if (relation.direction === 'incoming') {
    return {
      outgoingRelations: relations.outgoingRelations,
      incomingRelations: [...relations.incomingRelations, relation],
    };
  }

  return {
    outgoingRelations: [...relations.outgoingRelations, relation],
    incomingRelations: relations.incomingRelations,
  };
}

function replaceRelation(relations: CardRelations, relation: CardRelationEntry): CardRelations {
  const update = (items: CardRelationEntry[]) =>
    items.map((item) => (item.id === relation.id ? relation : item));

  return {
    outgoingRelations: update(relations.outgoingRelations),
    incomingRelations: update(relations.incomingRelations),
  };
}

function removeRelation(relations: CardRelations, relationId: number): CardRelations {
  return {
    outgoingRelations: relations.outgoingRelations.filter((item) => item.id !== relationId),
    incomingRelations: relations.incomingRelations.filter((item) => item.id !== relationId),
  };
}

function normalizeOriginalUrl(value: string) {
  const normalized = value.trim();

  return normalized ? { original_url: normalized } : null;
}

function RelationGraph({
  card,
  relations,
  onNavigate,
}: {
  card: Card;
  relations: CardRelations;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const nodes = [
    { id: card.id, title: card.title ?? card.type, type: card.type, current: true },
    ...dedupeRelations([
      ...relations.outgoingRelations,
      ...relations.incomingRelations,
    ]).map((relation) => ({
      id: relation.relatedCard.id,
      title: relation.relatedCard.title ?? relation.relatedCard.type,
      type: relation.relatedCard.type,
      current: false,
    })),
  ];

  if (nodes.length <= 1) {
    return (
      <section className="grid gap-3 rounded-lg border p-4">
        <h2 className="text-base font-semibold">Local relation graph</h2>
        <div className="text-muted-foreground text-sm">No relations to visualize.</div>
      </section>
    );
  }

  const radius = 130;
  const placed = nodes.map((node, index) => {
    if (node.current) {
      return { ...node, x: 0, y: 0 };
    }

    const angle = (index / (nodes.length - 1)) * Math.PI * 2;
    return { ...node, x: Math.cos(angle) * radius, y: Math.sin(angle) * radius };
  });

  const edges = [
    ...relations.outgoingRelations.map((relation) => ({
      from: card.id,
      to: relation.relatedCard.id,
      relationType: relation.relationType,
      direction: relation.direction,
    })),
    ...relations.incomingRelations.map((relation) => ({
      from: relation.relatedCard.id,
      to: card.id,
      relationType: relation.relationType,
      direction: relation.direction,
    })),
  ];

  const nodeById = new Map(placed.map((node) => [node.id, node]));

  return (
    <section className="grid gap-3 rounded-lg border p-4">
      <h2 className="text-base font-semibold">Local relation graph</h2>
      <svg viewBox="-240 -180 480 360" className="bg-muted/20 h-80 w-full rounded-lg border">
        {edges.map((edge, index) => {
          const from = nodeById.get(edge.from);
          const to = nodeById.get(edge.to);

          if (!from || !to) {
            return null;
          }

          return (
            <line
              key={`${edge.relationType}-${index}`}
              x1={from.x}
              y1={from.y}
              x2={to.x}
              y2={to.y}
              stroke={relationStroke(edge.relationType)}
              strokeWidth="2"
              strokeDasharray={edge.relationType === 'related_to' ? '6 4' : undefined}
              markerEnd={edge.relationType === 'related_to' ? undefined : 'url(#graph-arrow)'}
            />
          );
        })}
        <defs>
          <marker id="graph-arrow" markerWidth="8" markerHeight="8" refX="6" refY="4" orient="auto">
            <path d="M0,0 L8,4 L0,8 z" fill="currentColor" />
          </marker>
        </defs>
        {placed.map((node) => (
          <g key={node.id}>
            <a href={`/cards/${node.id}`} onClick={node.current ? undefined : onNavigate}>
              <title>
                {node.title} #{node.id} {node.type}
              </title>
              <circle
                cx={node.x}
                cy={node.y}
                r={node.current ? 26 : 20}
                fill={nodeFill(node.type, node.current)}
                stroke={node.current ? 'currentColor' : 'transparent'}
                strokeWidth={node.current ? 3 : 0}
              />
              <text
                x={node.x}
                y={node.y + 32}
                textAnchor="middle"
                className="fill-foreground text-[10px]"
              >
                {node.title.slice(0, 14)}
              </text>
            </a>
          </g>
        ))}
      </svg>
    </section>
  );
}

function relationStroke(relationType: string) {
  switch (relationType) {
    case 'tagged_with':
      return 'rgb(34 197 94)';
    case 'sourced_from':
      return 'rgb(59 130 246)';
    case 'related_to':
      return 'rgb(168 85 247)';
    case 'contains':
      return 'rgb(249 115 22)';
    case 'next_in_sequence':
      return 'rgb(107 114 128)';
    default:
      return 'rgb(148 163 184)';
  }
}

function nodeFill(type: string, current: boolean) {
  if (current) {
    return 'rgb(100 116 139)';
  }

  switch (type) {
    case 'comic':
      return 'rgb(251 146 60)';
    case 'set':
      return 'rgb(168 85 247)';
    case 'tag':
      return 'rgb(34 197 94)';
    case 'source':
      return 'rgb(59 130 246)';
    default:
      return 'rgb(148 163 184)';
  }
}
