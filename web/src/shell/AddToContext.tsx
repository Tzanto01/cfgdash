import { MessageSquarePlus } from 'lucide-react';
import { Button } from '../components/ui/button';
import { useAnnounce } from '../state/announcer';
import { MAX_CONTEXT, useAssistant, type ContextItem } from '../state/assistant';
import { useUi } from '../state/ui';

/**
 * "Add to context" for the assistant (assistant-v1): only where edits are available. `row` renders the compact icon button,
 * and only while the panel is open, so dense rows stay clean.
 */
export function AddToContext({ item, row = false }: { item: ContextItem; row?: boolean }) {
  const ui = useUi();
  const assistant = useAssistant();
  const announce = useAnnounce();
  if (!ui.edits || (row && !assistant.open)) {
    return null;
  }
  const onClick = () => {
    const result = assistant.add(item);
    announce(
      result === 'full'
        ? `The assistant context is full (${MAX_CONTEXT} items)`
        : result === 'present'
          ? 'Already in the assistant context'
          : `Added to the assistant context (${assistant.items.length + 1} of ${MAX_CONTEXT})`
    );
  };
  return row ? (
    <Button variant="ghost" size="icon" className="size-7" aria-label={`Add ${item.label} to assistant context`} onClick={onClick}>
      <MessageSquarePlus strokeWidth={1.75} aria-hidden="true" />
    </Button>
  ) : (
    <Button variant="ghost" size="sm" onClick={onClick}>
      <MessageSquarePlus strokeWidth={1.75} aria-hidden="true" />
      Add to context
    </Button>
  );
}
