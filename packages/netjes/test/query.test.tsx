import { useQuery, useQueryClient } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import type { ReactNode } from 'react';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { z } from 'zod';
import { Link } from '../src/link.tsx';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload, SeededQuery } from '../src/payload.ts';
import { createQueryShell } from '../src/query.tsx';
import { RouterProvider } from '../src/router.tsx';

const origin = window.location.origin;
const server = setupServer();
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

const likes = z.object({ likes: z.number() });
let clients: unknown[] = [];
let fetches = 0;

function Layout({ children }: { children: ReactNode }) {
  clients.push(useQueryClient());
  return (
    <div>
      <Link href="/a">A</Link>
      <Link href="/b">B</Link>
      {children}
    </div>
  );
}

function Likes() {
  const queryClient = useQueryClient();
  const query = useQuery({
    queryKey: ['likes', 'post'],
    queryFn: async () => {
      fetches++;
      return likes.parse(await (await fetch(`${origin}/api/likes`)).json());
    },
  });
  return (
    <div>
      <p>{query.data ? `${query.data.likes} likes` : 'loading'}</p>
      <button type="button" onClick={() => queryClient.setQueryData(['likes', 'post'], { likes: 99 })}>
        like
      </button>
    </div>
  );
}

const modules = {
  '/app/layout.tsx': async () => ({ default: Layout }),
  '/app/a/page.tsx': async () => ({ default: Likes }),
  '/app/b/page.tsx': async () => ({ default: Likes }),
};

function payload(page: string, queries?: SeededQuery[]): PagePayload {
  return {
    kind: 'page',
    route: `/${page}`,
    page: `app/${page}/page.tsx`,
    segments: [{ id: 'app', layout: 'app/layout.tsx' }],
    props: {},
    params: {},
    shared: null,
    status: 200,
    url: `/${page}`,
    version: 'v1',
    ...(queries ? { queries } : {}),
  };
}

const seeded = (count: number, updatedAt = Date.now()): SeededQuery[] => [{ key: ['likes', 'post'], data: { likes: count }, updatedAt }];

async function renderApp(initial: PagePayload) {
  clients = [];
  fetches = 0;
  window.history.replaceState(null, '', initial.url);
  const loader = createModuleLoader(modules);
  render(
    <RouterProvider
      initial={initial}
      initialModules={await loader.loadRoute(initial)}
      loader={loader}
      errorPage={null}
      shell={createQueryShell()}
    />,
  );
  return userEvent.setup();
}

describe('the built-in QueryClient', () => {
  it('renders queries seeded by the server without fetching them', async () => {
    await renderApp(payload('a', seeded(3)));

    expect(screen.getByText('3 likes')).toBeDefined();
    expect(fetches).toBe(0);
  });

  it('fetches queries the server did not seed', async () => {
    server.use(http.get(`${origin}/api/likes`, () => HttpResponse.json({ likes: 7 })));
    await renderApp(payload('a'));

    expect(screen.getByText('loading')).toBeDefined();
    expect(await screen.findByText('7 likes')).toBeDefined();
  });

  it('applies newer data seeded by the next page', async () => {
    server.use(http.get(`${origin}/b`, () => HttpResponse.json(payload('b', seeded(5)))));
    const user = await renderApp(payload('a', seeded(3, Date.now() - 1_000)));

    await user.click(screen.getByRole('link', { name: 'B' }));

    expect(await screen.findByText('5 likes')).toBeDefined();
  });

  it('keeps newer client data over an older seeded copy', async () => {
    server.use(http.get(`${origin}/b`, () => HttpResponse.json(payload('b', seeded(5, Date.now() - 60_000)))));
    const user = await renderApp(payload('a', seeded(3, Date.now() - 120_000)));

    await user.click(screen.getByRole('button', { name: 'like' }));
    await user.click(screen.getByRole('link', { name: 'B' }));
    await screen.findByRole('link', { name: 'B', current: 'page' });

    expect(screen.getByText('99 likes')).toBeDefined();
  });

  it('keeps one QueryClient across navigations', async () => {
    server.use(http.get(`${origin}/b`, () => HttpResponse.json(payload('b', seeded(5)))));
    const user = await renderApp(payload('a', seeded(3)));

    await user.click(screen.getByRole('link', { name: 'B' }));
    await screen.findByText('5 likes');

    expect(new Set(clients).size).toBe(1);
  });
});
