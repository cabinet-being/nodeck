import * as React from 'react';
import { MoreHorizontal, Pencil, Plus, SearchX, Trash2 } from 'lucide-react';

import {
  createSource, createTag, deleteSource, deleteTag, listSources, listTags,
  updateSource, updateTag, type Source, type Tag, type TagCategoryFilter,
} from '@/api/catalog';
import { EmptyState } from '@/components/empty-state';
import { NodeChip } from '@/components/node-chip';
import { Button } from '@/components/ui/button';
import { Dialog } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Menu, MenuContent, MenuItem, MenuTrigger } from '@/components/ui/menu';
import { Segmented } from '@/components/ui/segmented';
import { Skeleton } from '@/components/ui/skeleton';
import { useUrlState } from '@/hooks/use-url-state';

type Item = Tag | Source;
type Category = 'all' | 'artist' | 'character' | 'copyright' | 'general' | 'meta' | 'custom';

const categoryChoices: { value: TagCategoryFilter; label: string }[] = [
  { value: 'general', label: 'General' },
  { value: 'artist', label: 'Artist' },
  { value: 'character', label: 'Character' },
  { value: 'copyright', label: 'Copyright' },
  { value: 'meta', label: 'Meta' },
  { value: 'custom', label: 'Custom' },
];

// The API returns at most 200 entries per request; tag tabs ask the API for their
// category (most used first), and the client filter below keeps the list consistent.
const LOAD_LIMIT = 200;

function matchesCategory(item: Item, category: Category) {
  if (category === 'all' || !('origin' in item)) return true;
  return item.category === category;
}

export function CatalogPage({
  kind,
  onNavigate,
}: {
  kind: 'tag' | 'source';
  onNavigate?: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  const [category, setCategory] = useUrlState<Category>('tab', 'all', {
    parse: (raw) => (categoryChoices.some((choice) => choice.value === raw) ? raw as Category : undefined),
  });
  const [items, setItems] = React.useState<Item[]>([]);
  const [search, setSearch] = useUrlState('q', '', { debounce: 300 });
  const [revision, setRevision] = React.useState(0);
  const [loading, setLoading] = React.useState(true);
  const [error, setError] = React.useState<string | null>(null);
  const [editing, setEditing] = React.useState<Item | 'new' | null>(null);
  const [deleting, setDeleting] = React.useState<Item | null>(null);
  const [name, setName] = React.useState('');
  const [aliases, setAliases] = React.useState('');
  const [tagCategory, setTagCategory] = React.useState<TagCategoryFilter>('custom');
  const [formError, setFormError] = React.useState<string | null>(null);
  const [saving, setSaving] = React.useState(false);

  React.useEffect(() => {
    let active = true;
    setLoading(true);
    setError(null);
    const load = kind === 'tag'
      ? listTags(search, 'usage', category === 'all' ? undefined : category)
      : listSources(search);
    load.then((result) => {
      if (active) setItems(result);
    }).catch((cause) => {
      if (active) setError(cause instanceof Error ? cause.message : 'Unable to load catalog.');
    }).finally(() => {
      if (active) setLoading(false);
    });
    return () => { active = false; };
  }, [kind, search, revision, category]);

  function openEditor(item: Item | 'new') {
    setEditing(item);
    setName(item === 'new' ? '' : item.name);
    setAliases(item !== 'new' && 'aliases' in item ? item.aliases.join(', ') : '');
    setTagCategory(item !== 'new' && 'origin' in item
      ? (categoryChoices.some((choice) => choice.value === item.category) ? item.category as TagCategoryFilter : 'custom')
      : category === 'all' ? 'custom' : category);
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
        if (editing === 'new') await createTag(trimmed, parsedAliases, tagCategory);
        else await updateTag(editing.id, trimmed, parsedAliases, tagCategory);
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
  const importedEditing = editing !== null && editing !== 'new' && 'origin' in editing && editing.origin !== 'user';
  const visible = items.filter((item) => matchesCategory(item, category));
  return (
    <section className="mx-auto flex w-full max-w-4xl min-w-0 flex-col gap-5 p-4 sm:p-6">
      <div className="flex items-center justify-between gap-3">
        <h1 className="text-2xl font-semibold">{label}</h1>
        <Button type="button" onClick={() => openEditor('new')}><Plus />New {kind}</Button>
      </div>
      <Input type="search" aria-label={`Search ${label.toLowerCase()}`}
        placeholder={kind === 'tag' ? 'Search tags and aliases' : 'Search sources'}
        value={search} onChange={(event) => setSearch(event.target.value)} />
      {kind === 'tag' ? (
        <div className="-mx-4 overflow-x-auto px-4 sm:mx-0 sm:px-0">
          <Segmented ariaLabel="Tag category" value={category} onValueChange={setCategory}
            options={[
              { value: 'all', label: 'All' },
              { value: 'artist', label: 'Artist' },
              { value: 'character', label: 'Character' },
              { value: 'copyright', label: 'Copyright' },
              { value: 'general', label: 'General' },
              { value: 'meta', label: 'Meta' },
              { value: 'custom', label: 'Custom' },
            ]} />
        </div>
      ) : null}
      {error ? <p role="alert" className="text-destructive text-sm">{error}</p> : null}
      {loading && items.length === 0 ? (
        <div role="status" aria-label="Loading" className="grid gap-2">
          {Array.from({ length: 6 }, (_, index) => <Skeleton key={index} className="h-14 rounded-xl" />)}
        </div>
      ) : null}
      <ul className="grid gap-2">
        {visible.map((item) => {
          const editable = !item.nameConflict &&
            (kind === 'tag' ? 'origin' in item && item.origin !== 'system'
              : 'isLegacy' in item && !item.isLegacy);
          const deletable = kind !== 'tag' || ('origin' in item && item.origin === 'user');
          const href = kind === 'tag' ? `/gallery?catalog_tags=${item.id}` : `/gallery?catalog_source=${item.id}`;
          const aliasText = 'aliases' in item && item.aliases.length > 0 ? item.aliases.join(', ')
            : item.nameConflict ? 'Name conflict' : '';
          return (
            <li key={item.id} className="bg-surface flex min-h-14 items-center gap-3 rounded-xl border px-3 py-2">
              <div className="flex min-w-0 flex-1 flex-wrap items-center gap-x-3 gap-y-1">
                {'origin' in item
                  ? <NodeChip kind="tag" label={item.name} category={item.category} origin={item.origin}
                      href={href} onNavigate={onNavigate} />
                  : <NodeChip kind="source" label={item.name} href={href} onNavigate={onNavigate} />}
                {aliasText ? <span className="text-muted-foreground min-w-0 truncate text-xs">{aliasText}</span> : null}
              </div>
              <span className="text-muted-foreground shrink-0 text-xs tabular-nums">
                {item.referenceCount} {item.referenceCount === 1 ? 'card' : 'cards'}
              </span>
              {editable ? (
                <Menu>
                  <MenuTrigger render={<Button type="button" variant="ghost" size="icon"
                    aria-label={`Actions for ${item.name}`} />}>
                    <MoreHorizontal />
                  </MenuTrigger>
                  <MenuContent>
                    <MenuItem onClick={() => openEditor(item)}><Pencil /> Edit</MenuItem>
                    {deletable ? <MenuItem disabled={item.referenceCount > 0}
                      onClick={() => { setDeleting(item); setFormError(null); }}
                      className="text-destructive [&_svg]:text-destructive">
                      <Trash2 /> {item.referenceCount > 0 ? 'Delete (in use)' : 'Delete'}
                    </MenuItem> : null}
                  </MenuContent>
                </Menu>
              ) : <span className="size-9 shrink-0 pointer-coarse:size-11" aria-hidden="true" />}
            </li>
          );
        })}
      </ul>
      {!loading && visible.length === 0 && !error ? (
        <EmptyState icon={SearchX} title={`No ${label.toLowerCase()} found`} />
      ) : null}
      {!loading && items.length >= LOAD_LIMIT ? (
        <p className="text-muted-foreground text-center text-xs">
          {kind === 'tag' ? `Showing the ${LOAD_LIMIT} most used tags. Search to find others.` : `Showing the first ${LOAD_LIMIT}. Search to narrow the list.`}
        </p>
      ) : null}

      <Dialog open={editing !== null} title={editing === 'new' ? `New ${kind}` : `Edit ${kind}`}
        onOpenChange={(open) => { if (!open) setEditing(null); }}>
        <form onSubmit={save} className="grid gap-3">
          <label className="grid gap-1 text-sm font-medium">Name
            <Input autoFocus={!importedEditing} value={name} readOnly={importedEditing}
              onChange={(event) => setName(event.target.value)} />
          </label>
          {kind === 'tag' ? <label className="grid gap-1 text-sm font-medium">Aliases
            <Input value={aliases} readOnly={importedEditing} onChange={(event) => setAliases(event.target.value)}
              placeholder="Comma separated" />
          </label> : null}
          {kind === 'tag' ? <div className="grid gap-1 text-sm font-medium">
            <span>Category</span>
            <Segmented ariaLabel="Category" value={tagCategory} onValueChange={setTagCategory}
              className="flex-wrap" options={categoryChoices} />
            {importedEditing ? <p className="text-muted-foreground text-xs font-normal">
              Imported tags keep their name and aliases; only the category can change.</p> : null}
          </div> : null}
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
