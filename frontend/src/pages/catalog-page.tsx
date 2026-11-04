import * as React from 'react';
import { Pencil, Plus, Trash2 } from 'lucide-react';

import {
  createSource, createTag, deleteSource, deleteTag, listSources, listTags,
  updateSource, updateTag, type Source, type Tag,
} from '@/api/catalog';
import { TagChip } from '@/components/tag-chip';
import { Button } from '@/components/ui/button';
import { Dialog } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';

type Item = Tag | Source;

export function CatalogPage({ kind }: { kind: 'tag' | 'source' }) {
  const [items, setItems] = React.useState<Item[]>([]);
  const [search, setSearch] = React.useState('');
  const [revision, setRevision] = React.useState(0);
  const [loading, setLoading] = React.useState(true);
  const [error, setError] = React.useState<string | null>(null);
  const [editing, setEditing] = React.useState<Item | 'new' | null>(null);
  const [deleting, setDeleting] = React.useState<Item | null>(null);
  const [name, setName] = React.useState('');
  const [aliases, setAliases] = React.useState('');
  const [formError, setFormError] = React.useState<string | null>(null);
  const [saving, setSaving] = React.useState(false);

  React.useEffect(() => {
    let active = true;
    setLoading(true);
    setError(null);
    const load = kind === 'tag' ? listTags(search) : listSources(search);
    load.then((result) => {
      if (active) setItems(result);
    }).catch((cause) => {
      if (active) setError(cause instanceof Error ? cause.message : 'Unable to load catalog.');
    }).finally(() => {
      if (active) setLoading(false);
    });
    return () => { active = false; };
  }, [kind, search, revision]);

  function openEditor(item: Item | 'new') {
    setEditing(item);
    setName(item === 'new' ? '' : item.name);
    setAliases(item !== 'new' && 'aliases' in item ? item.aliases.join(', ') : '');
    setFormError(null);
  }

  async function save(event: React.FormEvent) {
    event.preventDefault();
    if (!editing) return;
    setSaving(true);
    setFormError(null);
    try {
      const trimmed = name.trim();
      if (kind === 'tag') {
        const parsedAliases = aliases.split(',').map((alias) => alias.trim()).filter(Boolean);
        if (editing === 'new') await createTag(trimmed, parsedAliases);
        else await updateTag(editing.id, trimmed, parsedAliases);
      } else {
        if (editing === 'new') await createSource(trimmed);
        else await updateSource(editing.id, trimmed);
      }
      setEditing(null);
      setRevision((value) => value + 1);
    } catch (cause) {
      setFormError(cause instanceof Error ? cause.message : 'Unable to save.');
    } finally {
      setSaving(false);
    }
  }

  async function remove() {
    if (!deleting) return;
    setSaving(true);
    setFormError(null);
    try {
      if (kind === 'tag') await deleteTag(deleting.id);
      else await deleteSource(deleting.id);
      setDeleting(null);
      setRevision((value) => value + 1);
    } catch (cause) {
      setFormError(cause instanceof Error ? cause.message : 'Unable to delete.');
    } finally {
      setSaving(false);
    }
  }

  const label = kind === 'tag' ? 'Tags' : 'Sources';
  return (
    <section className="mx-auto flex w-full max-w-4xl flex-col gap-5 p-6">
      <div className="flex items-center justify-between gap-3">
        <h1 className="text-2xl font-semibold">{label}</h1>
        <Button type="button" onClick={() => openEditor('new')}><Plus className="size-4" />New {kind}</Button>
      </div>
      <Input type="search" aria-label={`Search ${label.toLowerCase()}`}
        placeholder={kind === 'tag' ? 'Search tags and aliases' : 'Search sources'}
        value={search} onChange={(event) => setSearch(event.target.value)} />
      {error ? <p className="text-destructive text-sm">{error}</p> : null}
      <div className="grid gap-2">
        {items.map((item) => {
          const editable = !item.nameConflict &&
            (kind === 'tag' ? 'origin' in item && item.origin === 'user' && item.category === 'custom'
              : 'isLegacy' in item && !item.isLegacy);
          return (
            <div key={item.id} className="flex flex-wrap items-center gap-3 rounded-lg border p-3">
              {'origin' in item ? <TagChip name={item.name} category={item.category} origin={item.origin} />
                : <span className="font-medium">{item.name}</span>}
              <span className="text-muted-foreground min-w-0 flex-1 text-xs">
                {'aliases' in item && item.aliases.length > 0 ? item.aliases.join(', ')
                  : item.nameConflict ? 'Legacy name conflict' : ''}
              </span>
              <span className="text-muted-foreground text-xs" aria-label={`${item.referenceCount} cards`}>
                {item.referenceCount}
              </span>
              {editable ? (
                <div className="flex gap-1">
                  <Button type="button" size="icon" variant="ghost" aria-label={`Edit ${item.name}`}
                    onClick={() => openEditor(item)}><Pencil className="size-4" /></Button>
                  <Button type="button" size="icon" variant="ghost" aria-label={`Delete ${item.name}`}
                    disabled={item.referenceCount > 0} title={item.referenceCount > 0 ? 'Remove from cards first' : undefined}
                    onClick={() => { setDeleting(item); setFormError(null); }}><Trash2 className="size-4" /></Button>
                </div>
              ) : null}
            </div>
          );
        })}
        {!loading && items.length === 0 && !error ? (
          <p className="text-muted-foreground rounded-lg border p-6 text-center text-sm">No {label.toLowerCase()} found</p>
        ) : null}
      </div>

      <Dialog open={editing !== null} title={editing === 'new' ? `New ${kind}` : `Edit ${kind}`}
        onOpenChange={(open) => { if (!open) setEditing(null); }}>
        <form onSubmit={save} className="grid gap-3">
          <label className="grid gap-1 text-sm font-medium">Name
            <Input autoFocus value={name} onChange={(event) => setName(event.target.value)} />
          </label>
          {kind === 'tag' ? <label className="grid gap-1 text-sm font-medium">Aliases
            <Input value={aliases} onChange={(event) => setAliases(event.target.value)} placeholder="Comma separated" />
          </label> : null}
          {formError ? <p className="text-destructive text-sm">{formError}</p> : null}
          <Button type="submit" disabled={saving || !name.trim()}>{saving ? 'Saving...' : 'Save'}</Button>
        </form>
      </Dialog>

      <Dialog open={deleting !== null} title={`Delete ${kind}`}
        onOpenChange={(open) => { if (!open) setDeleting(null); }}>
        <div className="grid gap-3">
          <p className="text-sm">Delete “{deleting?.name}”?</p>
          {formError ? <p className="text-destructive text-sm">{formError}</p> : null}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => setDeleting(null)}>Cancel</Button>
            <Button type="button" variant="destructive" disabled={saving} onClick={remove}>Delete</Button>
          </div>
        </div>
      </Dialog>
    </section>
  );
}
