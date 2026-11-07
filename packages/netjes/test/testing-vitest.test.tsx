import { useMutation, useQuery } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useEffect, useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import '../src/testing-vitest.ts';
import { createTestApp } from '../src/testing.tsx';

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
  vi.unstubAllGlobals();
});

describe("the app's query defaults, which vitest.config.ts provides as netjes() does", () => {
  /** Fetches its own data, as a component with a query that no loader seeds does. */
  function Likes() {
    const likes = useQuery({
      queryKey: ['likes', 'hello'],
      queryFn: async () => {
        const response = await fetch('/api/posts/hello/likes');
        if (!response.ok) throw new Error(`Likes request failed with ${response.status}`);
        return (await response.json()) as { likes: number };
      },
    });
    return <p>{likes.isError ? 'Failed' : likes.data ? `Loaded ${likes.data.likes} likes` : 'Loading'}</p>;
  }

  it("hands createTestApp the app's query defaults the plugin provides, without their waits", () => {
    const defaults = createTestApp().queryClient.getDefaultOptions();

    expect(defaults.queries).toEqual({ staleTime: Number.POSITIVE_INFINITY, retry: 1, retryDelay: 0, gcTime: Number.POSITIVE_INFINITY });
    expect(defaults.mutations).toEqual({ retry: 1, retryDelay: 0 });
  });

  it("brings a failed query to its error after the app's retries, with no wait, under fake timers", async () => {
    vi.useFakeTimers();
    const fetch = vi.fn(async () => new Response(null, { status: 500 }));
    vi.stubGlobal('fetch', fetch);

    render(<Likes />, { wrapper: createTestApp().Wrapper });

    expect(await screen.findByText('Failed', undefined, { timeout: 500 })).toBeDefined();
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it("settles a failed mutation after the app's retries, with no wait, under fake timers", async () => {
    vi.useFakeTimers();
    let calls = 0;
    function Save() {
      const save = useMutation({
        mutationFn: async () => {
          calls += 1;
          throw new Error('Saving failed');
        },
      });
      return (
        <button type="button" onClick={() => save.mutate()}>
          {save.isError ? 'Not saved' : 'Save'}
        </button>
      );
    }
    render(<Save />, { wrapper: createTestApp().Wrapper });
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });

    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(calls).toBe(2), { timeout: 500 });
    expect(await screen.findByRole('button', { name: 'Not saved' }, { timeout: 500 })).toBeDefined();
    expect(calls).toBe(2);
  });
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
