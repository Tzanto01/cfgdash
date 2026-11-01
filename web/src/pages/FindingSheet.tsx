import { useRef } from 'react';
import { Link } from 'react-router';
import { Button } from '../components/ui/button';
import { Sheet, SheetClose, SheetContent } from '../components/ui/sheet';
import { findingHref, findingLocation } from '../lib/findings';
import type { Finding } from '../lib/models';
import { useUrl } from '../lib/url';
import { navViewId, VIEW_DEFINITIONS } from '../lib/views';
import { useReturnFocus } from '../shell/surface';
import { useSnapshotStore } from '../state/snapshot';
import { SeverityLabel } from './SeverityBar';

/** Right-side detail of one finding: severity, rule, detail text, where, and links to the evidence. */
export function FindingSheet({ finding, onClose }: { finding: Finding; onClose(): void }) {
  const url = useUrl();
  const store = useSnapshotStore();
  const focus = useReturnFocus();
  const heading = useRef<HTMLHeadingElement>(null);
  const viewId = navViewId(finding.view);
  const viewLabel = VIEW_DEFINITIONS.find((v) => v.id === viewId)?.label ?? finding.view;
  const fileId = finding.evidence.fileId;

  return (
    <Sheet
      open
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
    >
      <SheetContent
        side="right"
        aria-labelledby="finding-title"
        aria-describedby={undefined}
        closeLabel="Close finding"
        onOpenAutoFocus={(event) => {
          event.preventDefault();
          heading.current?.focus();
        }}
        onCloseAutoFocus={focus.onCloseAutoFocus}
      >
        <div className="space-y-1 border-b border-border p-4 pr-12">
          <h2 id="finding-title" ref={heading} tabIndex={-1} className="text-sm font-semibold break-words">
            {finding.title}
          </h2>
          <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs">
            <SeverityLabel severity={finding.severity} />
            <span className="mono-value text-muted">{finding.rule}</span>
          </div>
        </div>
        <div className="min-h-0 flex-1 space-y-4 overflow-y-auto p-4">
          <p className="break-words whitespace-pre-wrap">{finding.detail}</p>
          <dl className="space-y-1">
            <dt className="text-xs font-medium text-muted">Where</dt>
            <dd className="mono-value break-all">{findingLocation(store.snapshot, finding)}</dd>
          </dl>
          <div className="flex flex-wrap gap-2">
            <Link
              to={findingHref(finding, url.dir)}
              onClick={() => {
                focus.handOff(false);
                onClose();
              }}
              className="touch-target inline-flex h-8 items-center rounded-md border border-accent bg-accent px-3 text-sm font-medium text-accent-fg hover:opacity-90"
            >
              Show in {viewLabel}
            </Link>
            {fileId && (
              <Button
                onClick={() => {
                  focus.handOff(true);
                  onClose();
                  url.openFile(fileId);
                }}
              >
                Open file
              </Button>
            )}
            <SheetClose asChild>
              <Button variant="ghost">Close</Button>
            </SheetClose>
          </div>
        </div>
      </SheetContent>
    </Sheet>
  );
}
