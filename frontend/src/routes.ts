import {
  Archive,
  GalleryVerticalEnd,
  Heart,
  Layers3,
  Plus,
  Settings,
  Tag,
  Wrench,
} from 'lucide-react';

export type RouteItem = {
  path: string;
  label: string;
  icon: React.ComponentType<{ className?: string }>;
  group: 'core' | 'create' | 'service';
};

export const routes: RouteItem[] = [
  { path: '/gallery', label: 'Gallery', icon: GalleryVerticalEnd, group: 'core' },
  { path: '/favorites', label: 'Favorites', icon: Heart, group: 'core' },
  { path: '/cards', label: 'Cards', icon: Archive, group: 'core' },
  { path: '/decks', label: 'Decks', icon: Layers3, group: 'core' },
  { path: '/tags', label: 'Tags', icon: Tag, group: 'core' },
  { path: '/cards/new', label: 'Create Card', icon: Plus, group: 'create' },
  { path: '/decks/new', label: 'Create Deck', icon: Plus, group: 'create' },
  { path: '/settings', label: 'Settings', icon: Settings, group: 'service' },
  { path: '/service', label: 'Service', icon: Wrench, group: 'service' },
];
