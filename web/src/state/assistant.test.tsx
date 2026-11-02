import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { AssistantProvider, MAX_CONTEXT, useAssistant } from './assistant';

describe('assistant context', () => {
  it('holds at most 8 references and ignores a duplicate', () => {
    const { result } = renderHook(() => useAssistant(), { wrapper: ({ children }: { children: ReactNode }) => <AssistantProvider>{children}</AssistantProvider> });

    for (let i = 0; i < MAX_CONTEXT; i++) {
      act(() => {
        expect(result.current.add({ ref: { kind: 'file', fileId: `f${i}` }, label: `f${i}` })).toBe('added');
      });
    }
    act(() => {
      expect(result.current.add({ ref: { kind: 'file', fileId: 'f0' }, label: 'f0' })).toBe('present');
      expect(result.current.add({ ref: { kind: 'finding', findingId: 'x' }, label: 'x' })).toBe('full');
    });

    expect(result.current.items).toHaveLength(MAX_CONTEXT);
  });
});
