import type { ComponentProps } from 'react';
import { cn } from '../../lib/utils';

/** An on/off switch button (`role="switch"`); name it with `aria-label` or `aria-labelledby`. */
export function Switch({ on, className, ...props }: { on: boolean } & Omit<ComponentProps<'button'>, 'role' | 'type'>) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={on}
      {...props}
      className={cn(
        'touch-target relative inline-flex h-5 w-9 shrink-0 items-center rounded-full border transition-colors duration-100 disabled:opacity-50',
        on ? 'border-accent bg-accent' : 'border-border-strong bg-raised',
        className
      )}
    >
      <span
        aria-hidden="true"
        className={cn('size-3.5 rounded-full transition-transform duration-100', on ? 'translate-x-[18px] bg-accent-fg' : 'translate-x-[2px] bg-border-strong')}
      />
    </button>
  );
}
