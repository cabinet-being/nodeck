import * as React from 'react';
import { ArrowDown, ArrowUp, Plus, Trash2 } from 'lucide-react';

import { listCards, resolveApiUrl, type Card } from '@/api/cards';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';

export function ExistingMediaPicker({
  selected,
  onChange,
  disabled = false,
}: {
  selected: Card[];
  onChange: (cards: Card[]) => void;
  disabled?: boolean;
}) {
  const [search, setSearch] = React.useState('');
  const [results, setResults] = React.useState<Card[]>([]);
  const [error, setError] = React.useState<string | null>(null);

  React.useEffect(() => {
    let active = true;
    const timer = window.setTimeout(() => {
      listCards({ type: 'media', excludeContainedMedia: true, search: search.trim() || undefined })
        .then((cards) => { if (active) { setResults(cards); setError(null); } })
        .catch((cause) => { if (active) setError(cause.message); });
    }, 250);
    return () => { active = false; window.clearTimeout(timer); };
  }, [search]);

  const chosen = new Set(selected.map((card) => card.id));
  const available = results.filter((card) => !chosen.has(card.id));

  return (
    <div className="grid gap-4">
      <div className="grid gap-2">
        <label htmlFor="existing-media-search" className="text-sm font-medium">Find existing media</label>
        <Input id="existing-media-search" type="search" value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Search media" disabled={disabled} />
        {error ? <p className="text-destructive text-sm">{error}</p> : null}
        <div className="grid max-h-56 gap-2 overflow-auto">
          {available.map((card) => (
            <div key={card.id} className="bg-surface flex items-center gap-3 rounded-xl border p-2">
              <Thumb card={card} />
              <span className="min-w-0 flex-1 truncate text-sm">{card.title || `Untitled media`}</span>
              <Button type="button" variant="outline" size="sm" onClick={() => onChange([...selected, card])} disabled={disabled} aria-label={`Add ${card.title || `media`}`}><Plus className="size-4" /> Add</Button>
            </div>
          ))}
          {available.length === 0 && !error ? <p className="text-muted-foreground text-sm">No available media found.</p> : null}
        </div>
      </div>
      <div className="grid gap-2">
        <div className="text-sm font-medium">Selected media ({selected.length})</div>
        {selected.map((card, index) => (
          <div key={card.id} className="bg-surface flex items-center gap-2 rounded-xl border p-2">
            <span className="text-muted-foreground w-6 text-sm">{index + 1}</span>
            <Thumb card={card} />
            <span className="min-w-0 flex-1 truncate text-sm">{card.title || `Untitled media`}</span>
            <Button type="button" variant="ghost" size="icon" disabled={disabled || index === 0} aria-label={`Move ${card.title || 'media'} up`} onClick={() => move(index, index - 1)}><ArrowUp className="size-4" /></Button>
            <Button type="button" variant="ghost" size="icon" disabled={disabled || index === selected.length - 1} aria-label={`Move ${card.title || 'media'} down`} onClick={() => move(index, index + 1)}><ArrowDown className="size-4" /></Button>
            <Button type="button" variant="ghost" size="icon" disabled={disabled} aria-label={`Remove ${card.title || 'media'}`} onClick={() => onChange(selected.filter((item) => item.id !== card.id))}><Trash2 className="size-4" /></Button>
          </div>
        ))}
      </div>
    </div>
  );

  function move(from: number, to: number) {
    const next = [...selected];
    const [item] = next.splice(from, 1);
    next.splice(to, 0, item);
    onChange(next);
  }
}

function Thumb({ card }: { card: Card }) {
  return card.previewUrl
    ? <img src={resolveApiUrl(card.previewUrl)} alt="" className="size-12 shrink-0 rounded-lg object-cover" />
    : <div className="bg-muted size-12 shrink-0 rounded-lg" />;
}
