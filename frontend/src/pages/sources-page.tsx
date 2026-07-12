import * as React from 'react';

import { TextCardIndexPage } from '@/pages/tags-page';

export function SourcesPage({
  onNavigate,
}: {
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
}) {
  return (
    <TextCardIndexPage
      title="Sources"
      cardType="source"
      emptyMessage="No sources"
      onNavigate={onNavigate}
    />
  );
}
