import { useQuery, useQueryClient } from '@tanstack/react-query';
import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { hydrateRoot } from 'react-dom/client';
import { renderToString } from 'react-dom/server';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { Link } from '../src/link.tsx';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload } from '../src/payload.ts';
import { createQueryShell } from '../src/query.tsx';
import { RouterProvider, withClockOffset } from '../src/router.tsx';

const origin = window.location.origin;
const server = setupServer();
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));
afterEach(() => {
  server.resetHandlers();
  vi.useRealTimers();
});
afterAll(() => server.close());

function Likes() {
  const client = useQueryClient();
  const likes = useQuery({ queryKey: ['likes'], queryFn: (): Promise<number> => new Promise(() => {}) });
  return (
    <div>
      <p>{`${likes.data ?? '?'} likes${likes.isFetching ? ' (refreshing)' : ''}`}</p>
      <button type="button" onClick={() => client.setQueryData(['likes'], 4)}>
        like
      </button>
      <Link href="/b">B</Link>
    </div>
  );
}

const modules = { '/app/a/page.tsx': async () => ({ default: Likes }), '/app/b/page.tsx': async () => ({ default: Likes }) };
const QueryShell = createQueryShell();

function payload(page: string, renderedAt: number, likes: number, updatedAt = renderedAt): PagePayload {
  return {
    kind: 'page',
    route: `/${page}`,
    page: `app/${page}/page.tsx`,
    segments: [],
    props: {},
    params: {},
    shared: null,
    status: 200,
    url: `/${page}`,
    version: 'v1',
    renderedAt,
    queries: [{ key: ['likes'], data: likes, updatedAt }],
  };
}

describe('server timestamps on the client clock', () => {
  it('trusts the timestamps as they are when a data response has no server time header', async () => {
    const clientNow = Date.now();
    server.use(
      // e.g. UseOutputCache placed before UseNetjes: an hour-old cached payload without X-Netjes-Now.
      http.get(`${origin}/b`, () => HttpResponse.json(payload('b', clientNow - 3_600_000, 3))),
    );
    const initial = payload('a', clientNow, 3);
    window.history.replaceState(null, '', '/a');
    const loader = createModuleLoader(modules);
    render(
      <RouterProvider
        initial={withClockOffset(initial, initial.renderedAt)}
        initialModules={await loader.loadRoute(initial)}
        loader={loader}
        errorPage={null}
        shell={QueryShell}
      />,
    );
    const user = userEvent.setup();

    await user.click(screen.getByRole('button', { name: 'like' }));
    await user.click(screen.getByRole('link', { name: 'B' }));
    await screen.findByRole('link', { name: 'B', current: 'page' });

    expect(screen.getByText(/^4 likes/)).toBeDefined();
  });

  it('hydrates without a mismatch when the browser hydrates long after the server rendered', async () => {
    const renderedAt = Date.UTC(2026, 9, 28, 12);
    const initial = payload('a', renderedAt, 3);
    const loader = createModuleLoader(modules);
    const routeModules = await loader.loadRoute(initial);
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(renderedAt);
    const html = renderToString(
      <RouterProvider initial={initial} initialModules={routeModules} loader={loader} errorPage={null} shell={QueryShell} />,
    );
    expect(html).toContain('3 likes</p>');

    // A cached page, or a browser clock ahead of the server: well past the one-minute staleTime.
    vi.setSystemTime(renderedAt + 61_000);
    window.history.replaceState(null, '', '/a');
    const container = document.createElement('div');
    container.innerHTML = html;
    document.body.append(container);
    const recoverable = vi.fn();
    await act(async () => {
      hydrateRoot(
        container,
        <RouterProvider
          initial={withClockOffset(initial, initial.renderedAt)}
          initialModules={routeModules}
          loader={loader}
          errorPage={null}
          shell={QueryShell}
        />,
        { onRecoverableError: recoverable },
      );
    });

    expect(recoverable).not.toHaveBeenCalled();
    container.remove();
  });

  it('keeps newer client data over a seeded copy stamped by a server clock that runs ahead', async () => {
    const clientNow = Date.now();
    const serverNow = clientNow + 120_000; // the server clock is two minutes ahead
    server.use(
      http.get(`${origin}/b`, () =>
        // Rendered (and cached) ten seconds before the user's like.
        HttpResponse.json(payload('b', serverNow - 10_000, 3), { headers: { 'X-Netjes-Now': String(serverNow) } }),
      ),
    );
    const initial = payload('a', clientNow - 30_000, 3);
    window.history.replaceState(null, '', '/a');
    const loader = createModuleLoader(modules);
    render(
      <RouterProvider
        initial={withClockOffset(initial, initial.renderedAt)}
        initialModules={await loader.loadRoute(initial)}
        loader={loader}
        errorPage={null}
        shell={QueryShell}
      />,
    );
    const user = userEvent.setup();

    await user.click(screen.getByRole('button', { name: 'like' }));
    await user.click(screen.getByRole('link', { name: 'B' }));
    await screen.findByRole('link', { name: 'B', current: 'page' });

    expect(screen.getByText(/^4 likes/)).toBeDefined();
  });
});
