import { useSyncExternalStore } from 'react';

export type ThemeMode = 'system' | 'light' | 'dark';

const STORAGE_KEY = 'cfgdash-theme';
const ORDER: readonly ThemeMode[] = ['system', 'light', 'dark'];
const listeners = new Set<() => void>();

function readTheme(): ThemeMode {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    return stored === 'light' || stored === 'dark' ? stored : 'system';
  } catch {
    return 'system';
  }
}

/** Applies `mode` to `<html>`: `data-theme` and `color-scheme`; system clears both so CSS follows the OS. */
export function applyTheme(mode: ThemeMode): void {
  const root = document.documentElement;
  if (mode === 'system') {
    root.removeAttribute('data-theme');
    root.style.colorScheme = '';
  } else {
    root.setAttribute('data-theme', mode);
    root.style.colorScheme = mode;
  }
}

export function nextTheme(mode: ThemeMode): ThemeMode {
  return ORDER[(ORDER.indexOf(mode) + 1) % ORDER.length];
}

/** Stores, applies and broadcasts the theme choice. */
export function setTheme(mode: ThemeMode): void {
  try {
    if (mode === 'system') {
      localStorage.removeItem(STORAGE_KEY);
    } else {
      localStorage.setItem(STORAGE_KEY, mode);
    }
  } catch {
    return;
  }
  applyTheme(mode);
  listeners.forEach((listener) => listener());
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** The stored theme mode (system, light or dark). */
export function useTheme(): ThemeMode {
  return useSyncExternalStore(subscribe, readTheme, () => 'system');
}
