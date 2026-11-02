import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api } from '../lib/api';
import { setTheme } from './theme';

/** Open flags of the app-level surfaces (folder picker, command palette) and whether this listener allows edits. */
export interface UiState {
  /** `edits` from `GET /api/v1/health`: true only on the localhost UI listener (edits-v1). */
  edits: boolean;
  pickerOpen: boolean;
  setPickerOpen(open: boolean): void;
  paletteOpen: boolean;
  setPaletteOpen(open: boolean): void;
}

const UiContext = createContext<UiState | null>(null);

export function UiProvider({ children }: { children: ReactNode }) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [edits, setEdits] = useState(false);
  useEffect(() => {
    const controller = new AbortController();
    api
      .health(controller.signal)
      .then((health) => {
        setEdits(health.edits === true);
        return health.edits === true ? api.cfgdashSettings(controller.signal) : null;
      })
      .then(
        (settings) => {
          if (settings && !settings.configError) {
            setTheme(settings.values.theme);
          }
        },
        () => undefined
      );
    return () => controller.abort();
  }, []);
  const value = useMemo(() => ({ edits, pickerOpen, setPickerOpen, paletteOpen, setPaletteOpen }), [edits, pickerOpen, paletteOpen]);
  return <UiContext.Provider value={value}>{children}</UiContext.Provider>;
}

export function useUi(): UiState {
  const ui = useContext(UiContext);
  if (!ui) {
    throw new Error('useUi needs a UiProvider');
  }
  return ui;
}
