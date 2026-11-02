import { Moon, Sun, SunMoon } from 'lucide-react';
import { Button } from '../components/ui/button';
import { api } from '../lib/api';
import { nextTheme, setTheme, useTheme } from '../state/theme';
import { useUi } from '../state/ui';

const ICONS = { system: SunMoon, light: Sun, dark: Moon } as const;

/**
 * Cycles the theme system, light, dark; the name states the current mode and the next one. Where edits are available the
 * choice is also saved to cfgdash's `config.json` (cfgdash-config-v1); elsewhere it stays in this browser.
 */
export function ThemeToggle() {
  const mode = useTheme();
  const ui = useUi();
  const next = nextTheme(mode);
  const Icon = ICONS[mode];
  const toggle = () => {
    setTheme(next);
    if (ui.edits) {
      api.putCfgdashSettings({ theme: next }).catch(() => undefined);
    }
  };
  return (
    <Button variant="ghost" size="icon" aria-label={`Theme: ${mode}. Switch to ${next}`} onClick={toggle}>
      <Icon strokeWidth={1.75} aria-hidden="true" />
    </Button>
  );
}
