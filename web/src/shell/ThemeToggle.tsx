import { Moon, Sun, SunMoon } from 'lucide-react';
import { Button } from '../components/ui/button';
import { nextTheme, setTheme, useTheme } from '../state/theme';

const ICONS = { system: SunMoon, light: Sun, dark: Moon } as const;

/** Cycles the theme system, light, dark; the name states the current mode and the next one. */
export function ThemeToggle() {
  const mode = useTheme();
  const next = nextTheme(mode);
  const Icon = ICONS[mode];
  return (
    <Button variant="ghost" size="icon" aria-label={`Theme: ${mode}. Switch to ${next}`} onClick={() => setTheme(next)}>
      <Icon strokeWidth={1.75} aria-hidden="true" />
    </Button>
  );
}
