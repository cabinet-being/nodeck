import * as React from 'react';
import { Upload } from 'lucide-react';

import { Button } from '@/components/ui/button';

/** Hidden native input driven by a regular Button, with the chosen file name beside it. */
export function FilePicker({
  label = 'Choose file',
  accept,
  multiple = false,
  fileName,
  onFiles,
  disabled = false,
  className,
}: {
  label?: string;
  accept?: string;
  multiple?: boolean;
  /** Name(s) to show; falls back to "No file chosen". */
  fileName?: string;
  onFiles: (files: File[]) => void;
  disabled?: boolean;
  className?: string;
}) {
  const inputRef = React.useRef<HTMLInputElement>(null);

  return (
    <div className={['flex flex-wrap items-center gap-3', className].filter(Boolean).join(' ')}>
      <input
        ref={inputRef}
        type="file"
        className="sr-only"
        tabIndex={-1}
        aria-hidden="true"
        accept={accept}
        multiple={multiple}
        disabled={disabled}
        onChange={(event) => {
          onFiles(Array.from(event.target.files ?? []));
          if (multiple) event.target.value = '';
        }}
      />
      <Button type="button" variant="outline" disabled={disabled} onClick={() => inputRef.current?.click()}>
        <Upload className="size-4" />
        {label}
      </Button>
      <span className="text-muted-foreground min-w-0 truncate text-sm" role="status">
        {fileName || 'No file chosen'}
      </span>
    </div>
  );
}
