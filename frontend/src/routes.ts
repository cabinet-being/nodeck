import {
  Archive,
  CopyCheck,
  FilePlus2,
  GalleryVerticalEnd,
  Globe,
  Heart,
  House,
  Layers3,
  Plus,
  Puzzle,
  Tag,
  Upload,
} from 'lucide-react';

export type RouteItem = {
  path: string;
  label: string;
  icon: React.ComponentType<{ className?: string }>;
};

export const routes: { primary: RouteItem[]; library: RouteItem[]; create: RouteItem[] } = {
  primary: [
    { path: '/', label: 'Home', icon: House },
    { path: '/gallery', label: 'Gallery', icon: GalleryVerticalEnd },
    { path: '/decks', label: 'Decks', icon: Layers3 },
    { path: '/favorites', label: 'Favorites', icon: Heart },
  ],
  library: [
    { path: '/tags', label: 'Tags', icon: Tag },
    { path: '/sources', label: 'Sources', icon: Globe },
    { path: '/cards', label: 'Manage cards', icon: Archive },
    { path: '/duplicates', label: 'Duplicates', icon: CopyCheck },
    { path: '/extension', label: 'Browser extension', icon: Puzzle },
  ],
  create: [
    { path: '/cards/import', label: 'Import files', icon: Upload },
    { path: '/decks/new', label: 'New deck', icon: Plus },
    { path: '/cards/new', label: 'New card', icon: FilePlus2 },
  ],
};

export function isRouteActive(currentPath: string, path: string) {
  if (path === '/') return currentPath === '/';
  if (path === '/cards') return currentPath === '/cards';
  if (path === '/decks') return currentPath === '/decks' || /^\/decks\/\d+(\/edit)?$/.test(currentPath);
  return currentPath === path || currentPath.startsWith(`${path}/`);
}
