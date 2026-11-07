// A file of its own: the setup module must find a jest global set before it was imported, which a static import of
// it, as in testing-vitest.test.tsx, would not allow.
import { afterEach, describe, expect, it, vi } from 'vitest';

const global = globalThis as { jest?: unknown };

afterEach(() => {
  vi.useRealTimers();
  delete global.jest;
  vi.resetModules();
});

describe("a jest global that is not the setup file's", () => {
  it('keeps one that was there before', async () => {
    const theirs = { advanceTimersByTime: vi.fn() };
    global.jest = theirs;

    await import('../src/testing-vitest.ts');

    expect(global.jest).toBe(theirs);
    vi.useFakeTimers();
    expect(global.jest).toBe(theirs);
  });

  it('keeps one the test sets itself', async () => {
    await import('../src/testing-vitest.ts');
    const mine = { advanceTimersByTime: vi.fn() };

    global.jest = mine;

    expect(global.jest).toBe(mine);
    vi.useFakeTimers();
    expect(global.jest).toBe(mine);
    vi.useRealTimers();
    expect(global.jest).toBe(mine);
  });
});
