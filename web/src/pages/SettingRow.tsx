import { useId, useState } from 'react';
import { JsonValue } from '../blocks/JsonValue';
import { Button } from '../components/ui/button';
import type { EditScope, JsonValue as Value, SettingChange, SettingRow as Row } from '../lib/models';
import { cn } from '../lib/utils';

/** Display labels of the edit scopes. */
export const SCOPE_LABEL: Record<EditScope, string> = { user: 'User', project: 'Project', local: 'Local', machine: 'Machine' };

const PRECEDENCE: EditScope[] = ['local', 'project', 'user'];
const EDITABLE_TYPES = ['bool', 'enum', 'number', 'string', 'stringList'];
const REDACTED = '[redacted]';
const READ_ONLY: Record<string, string> = {
  secret: 'Secret: shown redacted, never editable',
  'unsafe-env': 'Can run code when set: not editable here',
  'not-in-catalog': 'Not in the settings catalog: read-only',
  'scope-unavailable': 'This scope is not available for the selected folder',
  'unsafe-target': 'The settings file is a link outside its folder: not editable'
};
const FIELD = 'touch-target w-full rounded-md border border-border-strong bg-raised px-2 text-sm placeholder:text-muted';

function hasRedacted(value: Value | null): boolean {
  return value !== null && JSON.stringify(value).includes(REDACTED);
}

function fits(type: string, value: Value | null): boolean {
  if (!EDITABLE_TYPES.includes(type)) {
    return false;
  }
  if (value === null) {
    return true;
  }
  switch (type) {
    case 'bool':
      return typeof value === 'boolean';
    case 'number':
      return typeof value === 'number';
    case 'stringList':
      return Array.isArray(value) && value.every((item) => typeof item === 'string');
    default:
      return typeof value === 'string';
  }
}

function sourceLabel(source: string): string {
  return source in SCOPE_LABEL ? SCOPE_LABEL[source as EditScope] : source;
}

interface SettingRowProps {
  row: Row;
  scope: EditScope;
  staged?: SettingChange;
  /** Validation error of the last review, already in words. */
  error?: string;
  onStage(change: SettingChange): void;
  onUnstage(): void;
}

/** One setting: label, key, description, the control for `scope` (or the read-only reason) and the effective value. */
export function SettingRow({ row, scope, staged, error, onStage, onUnstage }: SettingRowProps) {
  const id = useId();
  const [draft, setDraft] = useState<string | null>(null);
  const labelId = `${id}-label`;
  const descId = `${id}-desc`;
  const errorId = `${id}-error`;
  const scopeLabel = SCOPE_LABEL[scope];
  const scopeValue = row.values[scope] ?? null;
  const stagedUnset = staged !== undefined && 'unset' in staged;
  const current: Value | null = staged === undefined ? scopeValue : 'unset' in staged ? null : staged.value;

  const readOnly = !row.scopes.includes(scope)
    ? `Not settable in ${scopeLabel}`
    : !row.editable
      ? (READ_ONLY[row.reason ?? ''] ?? 'Read-only')
      : hasRedacted(scopeValue)
        ? READ_ONLY['secret']
        : !fits(row.type, scopeValue)
          ? 'Read-only'
          : null;

  const source = row.effectiveSource;
  const overriddenBy =
    source !== null && PRECEDENCE.includes(source as EditScope) && PRECEDENCE.indexOf(source as EditScope) < PRECEDENCE.indexOf(scope)
      ? SCOPE_LABEL[source as EditScope]
      : null;

  const set = (value: Value | null) =>
    onStage(value === null ? { key: row.key, scope, unset: true } : { key: row.key, scope, value });

  const field = {
    'aria-labelledby': labelId,
    'aria-describedby': error ? `${descId} ${errorId}` : descId,
    'aria-invalid': error ? true : undefined
  };

  const control = () => {
    switch (row.type) {
      case 'bool': {
        const on = current === true;
        return (
          <div className="flex items-center gap-2">
            <button
              type="button"
              role="switch"
              aria-checked={on}
              {...field}
              onClick={() => set(!on)}
              className={cn(
                'touch-target relative inline-flex h-5 w-9 shrink-0 items-center rounded-full border transition-colors duration-100',
                on ? 'border-accent bg-accent' : 'border-border-strong bg-raised'
              )}
            >
              <span
                aria-hidden="true"
                className={cn(
                  'size-3.5 rounded-full transition-transform duration-100',
                  on ? 'translate-x-[18px] bg-accent-fg' : 'translate-x-[2px] bg-border-strong'
                )}
              />
            </button>
            <span className="text-sm text-muted">{current === null ? `Not set in ${scopeLabel}` : on ? 'On' : 'Off'}</span>
          </div>
        );
      }
      case 'enum': {
        const options = row.options ?? [];
        const value = typeof current === 'string' ? current : '';
        return (
          <select {...field} value={value} onChange={(event) => set(event.target.value === '' ? null : event.target.value)} className={cn(FIELD, 'h-8')}>
            <option value="">Not set</option>
            {value !== '' && !options.includes(value) && <option value={value}>{value}</option>}
            {options.map((option) => (
              <option key={option} value={option}>
                {option}
              </option>
            ))}
          </select>
        );
      }
      case 'number':
        return (
          <input
            type="number"
            inputMode="decimal"
            {...field}
            placeholder={`Not set in ${scopeLabel}`}
            value={draft ?? (typeof current === 'number' ? String(current) : '')}
            onChange={(event) => {
              const text = event.target.value;
              setDraft(text);
              if (text.trim() === '') {
                set(null);
              } else if (Number.isFinite(Number(text))) {
                set(Number(text));
              }
            }}
            onBlur={() => setDraft(null)}
            className={cn(FIELD, 'h-8')}
          />
        );
      case 'stringList': {
        const items = Array.isArray(current) ? current.filter((item): item is string => typeof item === 'string') : [];
        return (
          <textarea
            {...field}
            rows={Math.min(Math.max(items.length + 1, 3), 10)}
            spellCheck={false}
            placeholder={`Not set in ${scopeLabel}. One item per line`}
            value={draft ?? items.join('\n')}
            onChange={(event) => {
              const text = event.target.value;
              setDraft(text);
              const next = text
                .split('\n')
                .map((item) => item.trim())
                .filter((item) => item !== '');
              set(next.length ? next : null);
            }}
            onBlur={() => setDraft(null)}
            className={cn(FIELD, 'mono-value py-1.5')}
          />
        );
      }
      default:
        return (
          <input
            type="text"
            {...field}
            autoComplete="off"
            spellCheck={false}
            placeholder={`Not set in ${scopeLabel}`}
            value={typeof current === 'string' ? current : ''}
            onChange={(event) => set(event.target.value === '' ? null : event.target.value)}
            className={cn(FIELD, 'h-8')}
          />
        );
    }
  };

  return (
    <div className="grid gap-x-6 gap-y-2 border-b border-border px-4 py-3 last:border-b-0 md:grid-cols-[minmax(0,1fr)_minmax(0,320px)]">
      <div className="min-w-0">
        <div className="flex flex-wrap items-center gap-2">
          <span id={labelId} className="font-medium">
            {row.label}
          </span>
          {staged && (
            <span className="tint-accent rounded-full border border-accent px-2 text-xs leading-5 font-medium text-accent">Changed</span>
          )}
        </div>
        <div className="mono-value wrap-anywhere text-muted">{row.key}</div>
        <p id={descId} className="mt-1 text-muted">
          {row.description}
        </p>
      </div>

      <div className="min-w-0 space-y-1.5">
        {readOnly ? (
          <>
            <div className="mono-value wrap-anywhere">
              <JsonValue value={scopeValue} redacted={scopeValue === REDACTED} />
            </div>
            <p className="text-xs text-muted">{readOnly}</p>
          </>
        ) : (
          control()
        )}
        {error && (
          <p id={errorId} className="text-xs font-medium text-danger">
            {error}
          </p>
        )}
        <div className="flex flex-wrap items-baseline gap-x-1 text-xs text-muted">
          <span>Effective:</span>
          {row.effective === null ? (
            <span>not set</span>
          ) : (
            <>
              <div className="mono-value min-w-0 wrap-anywhere text-text">
                <JsonValue value={row.effective} redacted={row.effective === REDACTED} />
              </div>
              <span>from {source === null ? 'not set' : sourceLabel(source)}</span>
            </>
          )}
        </div>
        {overriddenBy && <p className="text-xs text-warn">Overridden by {overriddenBy}: a change here will not take effect.</p>}
        {!readOnly && (scopeValue !== null || staged !== undefined) && (
          <div className="flex flex-wrap gap-1">
            {(scopeValue !== null || !stagedUnset) && (
              <Button size="sm" variant="ghost" aria-pressed={stagedUnset} onClick={() => (stagedUnset ? onUnstage() : set(null))}>
                Restore default<span className="sr-only"> for {row.label}</span>
              </Button>
            )}
            {staged && (
              <Button size="sm" variant="ghost" onClick={onUnstage}>
                Undo<span className="sr-only"> change to {row.label}</span>
              </Button>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
