import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useEffect, useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import '../src/testing-vitest.ts';

// A real timer, taken before any test fakes them: it ends the wait when Testing Library hangs on a fake one.
const realSetTimeout = globalThis.setTimeout;

/** Whether `promise` settles within `ms` of real time. */
function settlesWithin(ms: number, promise: Promise<unknown>): Promise<'settled' | 'still waiting'> {
  return Promise.race([
    promise.then(() => 'settled' as const),
    new Promise<'still waiting'>((resolve) => realSetTimeout(() => resolve('still waiting'), ms)),
  ]);
}

const jestGlobal = () => (globalThis as { jest?: { advanceTimersByTime?: unknown } }).jest;

afterEach(() => {
  vi.useRealTimers();
});

describe('Testing Library under Vitest fake timers', () => {
  it('lets user-event click under vi.useFakeTimers()', async () => {
    vi.useFakeTimers();
    function Later() {
      const [done, setDone] = useState(false);
      return (
        <button type="button" onClick={() => setTimeout(() => setDone(true), 1400)}>
          {done ? 'Done' : 'Start'}
        </button>
      );
    }
    render(<Later />);
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });

    expect(await settlesWithin(1000, user.click(screen.getByRole('button', { name: 'Start' })))).toBe('settled');
    expect(await screen.findByText('Done', undefined, { timeout: 2000 })).toBeDefined();
  });

  it('lets waitFor and findBy return under fake timers, advancing them as they poll', async () => {
    vi.useFakeTimers();
    function Soon() {
      const [later, setLater] = useState(false);
      useEffect(() => {
        const timer = setTimeout(() => setLater(true), 300);
        return () => clearTimeout(timer);
      }, []);
      return (
        <>
          <p>Now</p>
          {later && <p>Later</p>}
        </>
      );
    }
    render(<Soon />);

    expect(await settlesWithin(1000, waitFor(() => screen.getByText('Now')))).toBe('settled');
    expect(await settlesWithin(1000, screen.findByText('Later'))).toBe('settled');
  });

  it("defines jest only while Vitest's timers are fake", () => {
    expect(typeof jestGlobal()).toBe('undefined');

    vi.useFakeTimers();
    let fired = false;
    setTimeout(() => {
      fired = true;
    }, 100);

    expect(typeof jestGlobal()).toBe('object');
    expect(typeof jestGlobal()?.advanceTimersByTime).toBe('function');
    (jestGlobal()?.advanceTimersByTime as (ms: number) => void)(100);
    expect(fired).toBe(true);

    vi.useRealTimers();
    expect(typeof jestGlobal()).toBe('undefined');
  });
});
