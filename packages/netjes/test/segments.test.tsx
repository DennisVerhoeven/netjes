import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { type ReactNode, use, useState } from 'react';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { Link } from '../src/link.tsx';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload, SegmentRef } from '../src/payload.ts';
import type { ErrorPageProps } from '../src/route-tree.tsx';
import { RouterProvider } from '../src/router.tsx';

const origin = window.location.origin;
const server = setupServer();
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

let templateMounts = 0;
let release: (value: string) => void = () => {};
let slow = new Promise<string>((resolve) => {
  release = resolve;
});

function RootLayout({ children }: { children: ReactNode }) {
  const [clicks, setClicks] = useState(0);
  return (
    <div>
      <button type="button" onClick={() => setClicks((c) => c + 1)}>{`layout clicks ${clicks}`}</button>
      <Link href="/blog/a">A</Link>
      <Link href="/blog/b">B</Link>
      <Link href="/blog/broken">Broken</Link>
      {children}
    </div>
  );
}

function BlogTemplate({ children }: { children: ReactNode }) {
  const [id] = useState(() => ++templateMounts);
  return <section aria-label={`template ${id}`}>{children}</section>;
}

const BlogLoading = () => <p>loading blog…</p>;

function BlogError({ error, reset }: ErrorPageProps) {
  return (
    <div role="alert">
      <p>{`blog failed: ${error.message}`}</p>
      <button type="button" onClick={reset}>
        try again
      </button>
    </div>
  );
}

function Post(props: Record<string, unknown>) {
  return <h1>{`post ${String(props.slug)}`}</h1>;
}

function SlowPost() {
  return <h1>{use(slow)}</h1>;
}

let broken = false;
function Broken(): ReactNode {
  if (broken) throw new Error('render exploded');
  return <h1>recovered</h1>;
}

const modules = {
  '/app/layout.tsx': async () => ({ default: RootLayout }),
  '/app/blog/template.tsx': async () => ({ default: BlogTemplate }),
  '/app/blog/loading.tsx': async () => ({ default: BlogLoading }),
  '/app/blog/error.tsx': async () => ({ default: BlogError }),
  '/app/blog/[slug]/page.tsx': async () => ({ default: Post }),
  '/app/blog/slow/page.tsx': async () => ({ default: SlowPost }),
  '/app/blog/broken/page.tsx': async () => ({ default: Broken }),
};

const blogSegments: SegmentRef[] = [
  { id: 'app', layout: 'app/layout.tsx' },
  { id: 'app/blog', template: 'app/blog/template.tsx', loading: 'app/blog/loading.tsx', error: 'app/blog/error.tsx' },
];

function payload(page: string, url: string, overrides: Partial<PagePayload> = {}): PagePayload {
  return {
    kind: 'page',
    route: url,
    page,
    segments: blogSegments,
    props: {},
    params: {},
    shared: null,
    status: 200,
    url,
    version: 'v1',
    ...overrides,
  };
}

async function renderApp(initial: PagePayload) {
  window.history.replaceState(null, '', initial.url);
  const loader = createModuleLoader(modules);
  const initialModules = await loader.loadRoute(initial);
  // Awaited, so React retries pages that suspend once their data arrives.
  await act(async () => {
    render(<RouterProvider initial={initial} initialModules={initialModules} loader={loader} errorPage={null} />);
  });
  return userEvent.setup();
}

describe('segments', () => {
  it('wraps the page in layouts and templates; templates remount on navigation, layouts keep state', async () => {
    server.use(http.get(`${origin}/blog/b`, () => HttpResponse.json(payload('app/blog/[slug]/page.tsx', '/blog/b', { props: { slug: 'b' } }))));
    templateMounts = 0;
    const user = await renderApp(payload('app/blog/[slug]/page.tsx', '/blog/a', { props: { slug: 'a' } }));

    expect(screen.getByRole('region', { name: 'template 1' }).textContent).toBe('post a');
    await user.click(screen.getByRole('button', { name: 'layout clicks 0' }));
    await user.click(screen.getByRole('link', { name: 'B' }));

    expect(await screen.findByRole('heading', { name: 'post b' })).toBeDefined();
    expect(screen.getByRole('region', { name: 'template 2' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'layout clicks 1' })).toBeDefined();
  });

  it('shows the segment loading fallback while the page suspends', async () => {
    slow = new Promise<string>((resolve) => {
      release = resolve;
    });
    await renderApp(payload('app/blog/slow/page.tsx', '/blog/slow'));

    expect(screen.getByText('loading blog…')).toBeDefined();
    await act(async () => release('slow post'));

    expect(await screen.findByRole('heading', { name: 'slow post' })).toBeDefined();
  });

  it('catches render errors in the nearest error boundary, keeps the layout, and resets', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    broken = true;
    const user = await renderApp(payload('app/blog/broken/page.tsx', '/blog/broken'));

    expect(screen.getByRole('alert').textContent).toContain('blog failed: render exploded');
    expect(screen.getByRole('button', { name: 'layout clicks 0' })).toBeDefined();
    broken = false;
    await user.click(screen.getByRole('button', { name: 'try again' }));

    expect(screen.getByRole('heading', { name: 'recovered' })).toBeDefined();
    vi.restoreAllMocks();
  });

  it('renders an error boundary the server chose, with the error and a reset that refreshes', async () => {
    const requests: string[] = [];
    server.use(
      http.get(`${origin}/blog/a`, ({ request }) => {
        requests.push(request.url);
        return HttpResponse.json(payload('app/blog/[slug]/page.tsx', '/blog/a', { props: { slug: 'a' } }));
      }),
    );
    const user = await renderApp(
      payload('app/blog/error.tsx', '/blog/a', {
        pageKind: 'error',
        status: 500,
        props: { status: 500, message: 'db down', stack: null },
        segments: [blogSegments[0]!, { ...blogSegments[1]!, error: undefined }],
      }),
    );

    expect(screen.getByRole('alert').textContent).toContain('blog failed: db down');
    await user.click(screen.getByRole('button', { name: 'try again' }));

    expect(await screen.findByRole('heading', { name: 'post a' })).toBeDefined();
    expect(requests).toHaveLength(1);
  });
});
