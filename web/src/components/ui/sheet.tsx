import * as DialogPrimitive from '@radix-ui/react-dialog';
import { X } from 'lucide-react';
import { m } from 'motion/react';
import type { ComponentProps } from 'react';
import { cn } from '../../lib/utils';

/** The enter spring for sheets and the command palette. */
export const SPRING = { type: 'spring', stiffness: 400, damping: 34 } as const;

export const Sheet = DialogPrimitive.Root;
export const SheetTrigger = DialogPrimitive.Trigger;
export const SheetClose = DialogPrimitive.Close;
export const SheetTitle = DialogPrimitive.Title;
export const SheetDescription = DialogPrimitive.Description;

const SIDES = {
  left: 'inset-y-0 left-0 h-full w-[min(20rem,100%)] border-r',
  right: 'inset-y-0 right-0 h-full w-full border-l md:w-[560px]'
} as const;

/** shadcn/ui sheet adapted to the cfgdash tokens: a side panel with a scrim and a Close button. */
export function SheetContent({
  side = 'right',
  className,
  children,
  closeLabel = 'Close',
  ...props
}: ComponentProps<typeof DialogPrimitive.Content> & { side?: keyof typeof SIDES; closeLabel?: string }) {
  return (
    <DialogPrimitive.Portal>
      <DialogPrimitive.Overlay className="fixed inset-0 z-40 bg-black/40" />
      <DialogPrimitive.Content
        className={cn('fixed z-50 flex flex-col border-border bg-raised text-text shadow-overlay', SIDES[side], className)}
        {...props}
        asChild
      >
        <m.div initial={{ x: side === 'right' ? 24 : -24, opacity: 0 }} animate={{ x: 0, opacity: 1 }} transition={SPRING}>
          {children}
          <DialogPrimitive.Close
            className="touch-target absolute top-2 right-2 inline-flex size-8 items-center justify-center rounded-md text-muted hover:bg-hover hover:text-text"
            aria-label={closeLabel}
          >
            <X className="size-4" strokeWidth={1.75} aria-hidden="true" />
          </DialogPrimitive.Close>
        </m.div>
      </DialogPrimitive.Content>
    </DialogPrimitive.Portal>
  );
}
