import * as React from 'react';
import { Download, Info, MoreHorizontal, Pencil, Trash2 } from 'lucide-react';

import { resolveApiUrl } from '@/api/cards';
import { DailyVote } from '@/components/daily-vote';
import { DeckMembershipControl } from '@/components/deck-membership-control';
import { FavoriteToggle } from '@/components/favorite-toggle';
import { Button } from '@/components/ui/button';
import { Menu, MenuContent, MenuItem, MenuLinkItem, MenuSeparator, MenuTrigger } from '@/components/ui/menu';

/** Favorite, daily vote, deck popover and the overflow menu (edit, download, details, delete) of a card. */
export function ActionBar({
  cardId,
  isFavorite,
  onFavoriteChange,
  editHref,
  onNavigate,
  onDelete,
  originalUrl,
  onShowDetails,
  deckOpen,
  onDeckOpenChange,
  onDecksChange,
}: {
  cardId: number;
  isFavorite: boolean;
  onFavoriteChange: (isFavorite: boolean) => void;
  editHref: string;
  onNavigate: (event: React.MouseEvent<HTMLAnchorElement>) => void;
  onDelete: () => void;
  /** Original file; the Download item is hidden when absent. */
  originalUrl?: string | null;
  /** Details sheet opener; the item is hidden when absent. */
  onShowDetails?: () => void;
  deckOpen?: boolean;
  onDeckOpenChange?: (open: boolean) => void;
  onDecksChange?: () => void;
}) {
  return (
    <div className="flex flex-wrap items-center gap-1" role="toolbar" aria-label="Card actions">
      <FavoriteToggle cardId={cardId} isFavorite={isFavorite} onChange={onFavoriteChange} />
      <DailyVote key={`vote-${cardId}`} cardId={cardId} />
      <DeckMembershipControl key={`decks-${cardId}`} cardId={cardId} open={deckOpen} onOpenChange={onDeckOpenChange}
        onMembershipChange={onDecksChange} />
      <Menu>
        <MenuTrigger render={<Button type="button" variant="ghost" size="icon" aria-label="More actions" className="ml-auto" />}>
          <MoreHorizontal />
        </MenuTrigger>
        <MenuContent>
          <MenuLinkItem href={editHref} onClick={onNavigate}><Pencil /> Edit</MenuLinkItem>
          {originalUrl ? (
            <MenuLinkItem href={resolveApiUrl(originalUrl)} download><Download /> Download original</MenuLinkItem>
          ) : null}
          {onShowDetails ? <MenuItem onClick={onShowDetails}><Info /> Details</MenuItem> : null}
          <MenuSeparator />
          <MenuItem onClick={onDelete} className="text-destructive [&_svg]:text-destructive"><Trash2 /> Delete</MenuItem>
        </MenuContent>
      </Menu>
    </div>
  );
}
