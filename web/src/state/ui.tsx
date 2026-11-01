import { createContext, useContext, useMemo, useState, type ReactNode } from 'react';

/** Open flags of the app-level surfaces (folder picker, command palette). */
export interface UiState {
  pickerOpen: boolean;
  setPickerOpen(open: boolean): void;
  paletteOpen: boolean;
  setPaletteOpen(open: boolean): void;
}

const UiContext = createContext<UiState | null>(null);

export function UiProvider({ children }: { children: ReactNode }) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const value = useMemo(() => ({ pickerOpen, setPickerOpen, paletteOpen, setPaletteOpen }), [pickerOpen, paletteOpen]);
  return <UiContext.Provider value={value}>{children}</UiContext.Provider>;
}

export function useUi(): UiState {
  const ui = useContext(UiContext);
  if (!ui) {
    throw new Error('useUi needs a UiProvider');
  }
  return ui;
}
