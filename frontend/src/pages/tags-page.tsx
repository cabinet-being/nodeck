import * as React from 'react';

import { CatalogPage } from '@/pages/catalog-page';

export function TagsPage({ onNavigate }: { onNavigate?: (event: React.MouseEvent<HTMLAnchorElement>) => void }) {
  return <CatalogPage kind="tag" onNavigate={onNavigate} />;
}
