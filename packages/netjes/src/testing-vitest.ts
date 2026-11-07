// Registered by netjes() as a Vitest setup file in every app, after the app's own: what the test harness needs from the
// runner. Named by path, not exported.
import type { DefaultOptions } from '@tanstack/react-query';
import { inject, vi } from 'vitest';

declare module 'vitest' {
  interface ProvidedContext {
    /** The app's `netjes({ query: { defaultOptions } })`, which netjes() provides to its tests. */
    'netjes:query': DefaultOptions;
  }
}

// The app's query defaults, for createTestApp. They wait under a global symbol rather than in a module: the app's
// imports of netjes/testing need not share module instances with this file, which Vitest loads by its path.
const appQueryDefaults: unknown = inject('netjes:query');
if (typeof appQueryDefaults === 'object' && appQueryDefaults !== null) {
  (globalThis as Record<symbol, unknown>)[Symbol.for('netjes.testing.queryDefaults')] = appQueryDefaults;
}

// Testing Library advances fake timers only through Jest's global: without one, waitFor, findBy and every user-event
// call wait on a faked setTimeout that nothing advances, so they never return. While Vitest's timers are fake, `jest`
// is a bridge to them; otherwise it is undefined, so code that looks for Jest finds none. A jest global that was
// already there is left alone, and one a test assigns replaces the bridge.
if (!('jest' in globalThis)) {
  const bridge = {
    advanceTimersByTime(ms: number) {
      vi.advanceTimersByTime(ms);
    },
  };
  Object.defineProperty(globalThis, 'jest', {
    configurable: true,
    get: () => (vi.isFakeTimers() ? bridge : undefined),
    set(value: unknown) {
      Object.defineProperty(globalThis, 'jest', { configurable: true, enumerable: true, writable: true, value });
    },
  });
}
