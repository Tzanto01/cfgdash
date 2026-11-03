import { Check, ChevronDown, Search } from 'lucide-react';
import { useEffect, useId, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import type { AssistantEffort, AssistantProvider, AssistantStatus } from '../lib/models';
import { cn } from '../lib/utils';

/** Every effort the assistant accepts (assistant-v1), in order. */
export const EFFORTS: AssistantEffort[] = ['low', 'medium', 'high', 'xhigh', 'max'];
const EFFORT_LABEL: Record<AssistantEffort, string> = { low: 'Low', medium: 'Medium', high: 'High', xhigh: 'Extra high', max: 'Max' };
const MODEL_ID = /^[A-Za-z0-9][A-Za-z0-9.\-[\]]{0,99}$/;

/** The providers to offer: the status `providers`, or one Claude entry built from the older `models` list. */
export function pickerProviders(status: AssistantStatus | undefined): AssistantProvider[] {
  if (status?.providers?.length) {
    return status.providers;
  }
  return [
    {
      id: 'claude',
      label: 'Claude',
      available: status?.available ?? false,
      reason: status?.reason,
      models: (status?.models ?? []).map((id) => ({ id, label: id })),
      efforts: EFFORTS
    }
  ];
}

export interface PickerValue {
  provider: string;
  model: string;
  effort: AssistantEffort;
}

interface ModelPickerProps {
  providers: AssistantProvider[];
  value: PickerValue;
  /** The effort labelled "Default". */
  defaultEffort: AssistantEffort;
  onChange(value: PickerValue): void;
  /** The id of the visible label that names the trigger. */
  labelledBy: string;
  describedBy?: string;
  disabled?: boolean;
  /** Where the popover opens relative to the trigger. */
  placement?: 'top' | 'bottom';
}

const OPTION =
  'flex min-h-8 cursor-pointer items-center gap-2 rounded-md px-2 text-sm hover:bg-hover has-[:checked]:font-medium has-[:focus-visible]:outline-2 has-[:focus-visible]:-outline-offset-2 has-[:focus-visible]:outline-focus';

function Option({ name, checked, onSelect, children }: { name: string; checked: boolean; onSelect(): void; children: ReactNode }) {
  return (
    <label className={cn(OPTION, 'touch-target')}>
      <input type="radio" name={name} checked={checked} onChange={onSelect} className="sr-only" />
      <span className="flex min-w-0 flex-1 items-center gap-2">{children}</span>
      <Check className={cn('size-4 shrink-0 text-accent', !checked && 'invisible')} strokeWidth={1.75} aria-hidden="true" />
    </label>
  );
}

/**
 * The assistant model picker: a trigger showing the model and effort, opening a popover with providers on the left, a
 * searchable model list in the middle and the reasoning effort on the right. Every choice calls `onChange` at once.
 */
export function ModelPicker({ providers, value, defaultEffort, onChange, labelledBy, describedBy, disabled, placement = 'bottom' }: ModelPickerProps) {
  const id = useId();
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const root = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const search = useRef<HTMLInputElement>(null);

  const provider = providers.find((p) => p.id === value.provider) ?? providers[0];
  const listed = provider?.models ?? [];
  const models = value.model && !listed.some((m) => m.id === value.model) ? [{ id: value.model, label: value.model }, ...listed] : listed;
  const q = query.trim();
  const shown = q ? models.filter((m) => `${m.id} ${m.label}`.toLowerCase().includes(q.toLowerCase())) : models;
  const custom = q && MODEL_ID.test(q) && !models.some((m) => m.id === q) ? q : null;
  const efforts = provider?.efforts.length ? provider.efforts : EFFORTS;
  const modelLabel = models.find((m) => m.id === value.model)?.label ?? value.model;

  useEffect(() => {
    if (!open) {
      return;
    }
    search.current?.focus();
    const onPointerDown = (event: PointerEvent) => {
      if (!root.current?.contains(event.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener('pointerdown', onPointerDown);
    return () => document.removeEventListener('pointerdown', onPointerDown);
  }, [open]);

  const close = () => {
    setOpen(false);
    setQuery('');
    trigger.current?.focus();
  };

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key === 'Escape') {
      event.stopPropagation();
      close();
    }
  };

  const pick = (patch: Partial<PickerValue>) => onChange({ ...value, ...patch });

  return (
    <div
      ref={root}
      className="relative"
      onKeyDown={onKeyDown}
      onBlur={(event) => {
        if (event.relatedTarget && !root.current?.contains(event.relatedTarget)) {
          setOpen(false);
          setQuery('');
        }
      }}
    >
      <button
        ref={trigger}
        type="button"
        aria-haspopup="dialog"
        aria-expanded={open}
        aria-labelledby={`${labelledBy} ${id}-value`}
        aria-describedby={describedBy}
        disabled={disabled}
        onClick={() => (open ? close() : setOpen(true))}
        className="touch-target flex h-8 w-full items-center gap-2 rounded-md border border-border-strong bg-raised px-2 text-left text-sm hover:bg-hover disabled:opacity-50"
      >
        <span id={`${id}-value`} className="min-w-0 flex-1 truncate">
          <span className="mono-value">{modelLabel || 'No model'}</span>
          <span className="text-muted"> · {EFFORT_LABEL[value.effort]}</span>
        </span>
        <ChevronDown className="size-4 shrink-0 text-muted" strokeWidth={1.75} aria-hidden="true" />
      </button>
      {open && (
        <div
          role="dialog"
          aria-label="Model and effort"
          tabIndex={-1}
          className={cn(
            'absolute right-0 z-50 outline-none w-[min(560px,calc(100vw-32px))] overflow-hidden rounded-xl border border-border bg-raised text-text shadow-overlay',
            placement === 'top' ? 'bottom-full mb-1' : 'top-full mt-1'
          )}
        >
          <div className="grid max-h-[min(440px,70vh)] grid-cols-1 overflow-y-auto sm:grid-cols-[minmax(0,120px)_minmax(0,1fr)_minmax(0,170px)] sm:overflow-hidden">
            <fieldset className="border-b border-border p-2 sm:border-r sm:border-b-0">
              <legend className="sr-only">Provider</legend>
              <div className="flex gap-1 sm:flex-col">
                {providers.map((p) => (
                  <Option key={p.id} name={`${id}-provider`} checked={p.id === provider?.id} onSelect={() => pick({ provider: p.id, model: p.models[0]?.id ?? '' })}>
                    <span className="min-w-0">
                      <span className="block truncate">{p.label}</span>
                      {!p.available && <span className="block truncate text-xs font-normal text-muted">Not available</span>}
                    </span>
                  </Option>
                ))}
              </div>
            </fieldset>
            <div className="flex min-h-0 flex-col border-b border-border sm:border-r sm:border-b-0">
              <div className="flex items-center gap-2 border-b border-border px-3 focus-within:shadow-[inset_0_-2px_var(--focus)]">
                <Search className="size-4 shrink-0 text-muted" strokeWidth={1.75} aria-hidden="true" />
                <input
                  ref={search}
                  type="search"
                  aria-label="Search models"
                  placeholder="Search models..."
                  autoComplete="off"
                  spellCheck={false}
                  value={query}
                  onChange={(event) => setQuery(event.target.value)}
                  className="h-10 min-w-0 flex-1 bg-transparent text-sm outline-none placeholder:text-muted"
                />
              </div>
              <fieldset className="min-h-0 flex-1 space-y-0.5 overflow-y-auto p-2">
                <legend className="sr-only">Model</legend>
                {shown.map((m) => (
                  <Option key={m.id} name={`${id}-model`} checked={m.id === value.model} onSelect={() => pick({ model: m.id })}>
                    <span className="truncate">{m.label}</span>
                    {m.label !== m.id && <span className="mono-value truncate text-xs text-muted">{m.id}</span>}
                  </Option>
                ))}
                {!shown.length && !custom && <p className="px-2 py-1.5 text-sm text-muted">No models match</p>}
                {custom && (
                  <button
                    type="button"
                    onClick={() => {
                      pick({ model: custom });
                      setQuery('');
                    }}
                    className="touch-target flex min-h-8 w-full items-center rounded-md px-2 text-left text-sm hover:bg-hover"
                  >
                    Use model id <span className="mono-value ml-1 truncate">{custom}</span>
                  </button>
                )}
              </fieldset>
            </div>
            <fieldset className="space-y-0.5 p-2">
              <legend className="flex w-full items-center gap-2 px-2 pb-1 text-xs font-medium text-muted">
                <span className="flex-1">Reasoning</span>
                <span className="mono-value truncate">{modelLabel}</span>
              </legend>
              {efforts.map((effort) => (
                <Option key={effort} name={`${id}-effort`} checked={effort === value.effort} onSelect={() => pick({ effort })}>
                  <span className="flex-1">{EFFORT_LABEL[effort]}</span>
                  {effort === defaultEffort && <span className="text-xs font-normal text-muted">Default</span>}
                </Option>
              ))}
            </fieldset>
          </div>
        </div>
      )}
    </div>
  );
}
