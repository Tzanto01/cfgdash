import { Pencil } from 'lucide-react';
import { useLocation, useNavigate } from 'react-router';
import { Button } from '../components/ui/button';
import { mergeSearch, useUrl } from '../lib/url';
import { useUi } from '../state/ui';

/** The Settings view's "Edit" toggle (edits-v1 UI): switches `edit=1`; only when health says `edits: true`. */
export function EditToggle() {
  const ui = useUi();
  const url = useUrl();
  const location = useLocation();
  const navigate = useNavigate();
  if (!ui.edits) {
    return null;
  }
  return (
    <Button
      aria-pressed={url.edit}
      onClick={() => void navigate({ pathname: '/settings', search: mergeSearch(location.search, { edit: url.edit ? undefined : '1' }) })}
      className={url.edit ? 'tint-accent border-accent text-accent' : undefined}
    >
      <Pencil strokeWidth={1.75} aria-hidden="true" />
      Edit
    </Button>
  );
}
