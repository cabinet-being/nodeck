import * as React from 'react';

import { CardBrowser } from '@/components/card-browser';

export function GalleryPage({
  currentSearch,
  onNavigate,
  onNavigateTo,
}: {
  currentSearch: string;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onNavigateTo: (path: string) => void;
}) {
  return (
    <section className="flex flex-col gap-5 px-4 py-6 sm:px-6 sm:py-8">
      <CardBrowser
        scope={{ kind: 'all' }}
        basePath="/gallery"
        currentSearch={currentSearch}
        onNavigate={onNavigate}
        onNavigateTo={onNavigateTo}
        viewStorageKey="nodeck.galleryView"
        title={<h1 className="text-2xl font-semibold tracking-tight">Gallery</h1>}
        emptyMessage="No cards yet. Import some media to get started."
      />
    </section>
  );
}
