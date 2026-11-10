import * as React from 'react';

import { CatalogPage } from '@/pages/catalog-page';

export function SourcesPage({ onNavigate }: { onNavigate?: (event: React.MouseEvent<HTMLAnchorElement>) => void }) {
  return <CatalogPage kind="source" onNavigate={onNavigate} />;
}
