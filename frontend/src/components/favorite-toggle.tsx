import * as React from 'react';
import { Heart } from 'lucide-react';

import { addFavorite, removeFavorite } from '@/api/cards';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';

export function FavoriteToggle({
  cardId,
  isFavorite,
  onChange,
  className,
}: {
  cardId: number;
  isFavorite: boolean;
  onChange: (isFavorite: boolean) => void;
  className?: string;
}) {
  const [isUpdating, setIsUpdating] = React.useState(false);

  async function handleClick(event: React.MouseEvent<HTMLButtonElement>) {
    event.preventDefault();
    event.stopPropagation();

    if (isUpdating) {
      return;
    }

    setIsUpdating(true);

    try {
      if (isFavorite) {
        await removeFavorite(cardId);
        onChange(false);
      } else {
        await addFavorite(cardId);
        onChange(true);
      }
    } finally {
      setIsUpdating(false);
    }
  }

  return (
    <Button
      type="button"
      variant={isFavorite ? 'secondary' : 'ghost'}
      size="icon"
      aria-label={isFavorite ? 'Remove card from Favorites' : 'Add card to Favorites'}
      disabled={isUpdating}
      className={className}
      onClick={handleClick}
    >
      <Heart className={cn('size-4', isFavorite ? 'fill-current' : '')} />
    </Button>
  );
}
