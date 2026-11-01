import type { ComponentProps } from 'react';
import { cn } from '../../lib/utils';

const VARIANTS = {
  default: 'border border-border-strong bg-raised text-text hover:bg-hover',
  primary: 'border border-accent bg-accent text-accent-fg hover:opacity-90',
  ghost: 'text-text hover:bg-hover',
  link: 'h-auto px-0 text-accent underline-offset-2 hover:underline'
} as const;

const SIZES = {
  default: 'h-8 px-3',
  sm: 'h-7 px-2 text-xs',
  icon: 'size-8 justify-center'
} as const;

export interface ButtonProps extends ComponentProps<'button'> {
  variant?: keyof typeof VARIANTS;
  size?: keyof typeof SIZES;
}

/** shadcn/ui button adapted to the cfgdash tokens; `type="button"` by default. */
export function Button({ className, variant = 'default', size = 'default', type = 'button', ...props }: ButtonProps) {
  return (
    <button
      type={type}
      className={cn(
        'touch-target inline-flex shrink-0 items-center gap-1.5 rounded-md text-sm font-medium whitespace-nowrap transition-colors disabled:pointer-events-none disabled:opacity-50 [&_svg]:size-4 [&_svg]:shrink-0',
        SIZES[size],
        VARIANTS[variant],
        className
      )}
      {...props}
    />
  );
}
