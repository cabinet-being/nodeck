import {
  Archive,
  GalleryVerticalEnd,
  Heart,
  Layers3,
  Plus,
  Tag,
} from 'lucide-react';

export type RouteItem = {
  path: string;
  label: string;
  icon: React.ComponentType<{ className?: string }>;
  group: 'core' | 'create';
};

export type RouteDetail = {
  eyebrow: string;
  title: string;
  summary: string;
  items: string[];
};

export const routes: RouteItem[] = [
  { path: '/gallery', label: 'Gallery', icon: GalleryVerticalEnd, group: 'core' },
  { path: '/favorites', label: 'Favorites', icon: Heart, group: 'core' },
  { path: '/cards', label: 'Cards', icon: Archive, group: 'core' },
  { path: '/decks', label: 'Decks', icon: Layers3, group: 'core' },
  { path: '/tags', label: 'Tags', icon: Tag, group: 'core' },
  { path: '/cards/new', label: 'Create Card', icon: Plus, group: 'create' },
  { path: '/decks/new', label: 'Create Deck', icon: Plus, group: 'create' },
];

export const routeTitles = new Map<string, string>([
  ['/', 'Home'],
  ...routes.map((route) => [route.path, route.label] as const),
]);

export const routeDetails = new Map<string, RouteDetail>([
  [
    '/',
    {
      eyebrow: 'Home',
      title: 'Nodeck',
      summary: 'Card-centered gallery navigation for visual objects, tags, and decks.',
      items: ['Browse saved cards', 'Open deck collections', 'Prepare new gallery entries'],
    },
  ],
  [
    '/gallery',
    {
      eyebrow: 'Gallery',
      title: 'Gallery',
      summary: 'The future browsing surface for saved visual objects.',
      items: ['Media grid placeholder', 'Card previews', 'Filter-ready layout'],
    },
  ],
  [
    '/favorites',
    {
      eyebrow: 'Favorites',
      title: 'Favorites',
      summary: 'A focused area for saved cards and decks.',
      items: ['Pinned cards', 'Saved decks', 'Quick return paths'],
    },
  ],
  [
    '/cards',
    {
      eyebrow: 'Cards',
      title: 'Cards',
      summary: 'Semi-technical card management placeholder.',
      items: ['Card records', 'Object metadata', 'Future import state'],
    },
  ],
  [
    '/decks',
    {
      eyebrow: 'Decks',
      title: 'Decks',
      summary: 'Semi-technical deck management placeholder.',
      items: ['Deck records', 'Card membership', 'Collection structure'],
    },
  ],
  [
    '/tags',
    {
      eyebrow: 'Tags',
      title: 'Tags',
      summary: 'The future index for visual grouping and discovery.',
      items: ['Tag cards', 'Related objects', 'Filter entry points'],
    },
  ],
  [
    '/cards/new',
    {
      eyebrow: 'Create',
      title: 'Create Card',
      summary: 'Reserved route for adding a new visual object card.',
      items: ['Card draft', 'Source details', 'Tag assignment'],
    },
  ],
  [
    '/decks/new',
    {
      eyebrow: 'Create',
      title: 'Create Deck',
      summary: 'Reserved route for assembling a new card collection.',
      items: ['Deck draft', 'Card selection', 'Collection metadata'],
    },
  ],
]);
