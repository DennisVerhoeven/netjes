import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import type { ReactNode } from 'react';
import { type Root, hydrateRoot } from 'react-dom/client';
import { renderToString } from 'react-dom/server';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { Link } from '../src/link.tsx';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload } from '../src/payload.ts';
import { RouterProvider, useRouter } from '../src/router.tsx';

// What a screen reader and a keyboard user get from a client navigation: the router names the new page in a polite
// live region, as a screen reader names the page a document load opens.

const origin = window.location.origin;
const server = setupServer();
/** A page hydrated from the server's HTML, which Testing Library's cleanup does not know about. */
let hydrated: { root: Root; container: HTMLElement } | undefined;
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));
afterEach(() => {
  if (hydrated) {
    const { root, container } = hydrated;
    act(() => root.unmount());
    container.remove();
    hydrated = undefined;
  }
  server.resetHandlers();
  vi.restoreAllMocks();
});
afterAll(() => server.close());

/** The router as the layout sees it. */
let router: ReturnType<typeof useRouter> | undefined;

function Layout({ children }: { children: ReactNode }) {
  router = useRouter();
  return (
    <div>
      <nav aria-label="Main">
        <Link href="/">Home</Link>
        <Link href="/about">About</Link>
        <Link href="/contact">Contact</Link>
      </nav>
      {children}
    </div>
  );
}

const Home = () => <h1>Home</h1>;
const About = () => <h1>About</h1>;
const Contact = () => <h1>Contact</h1>;
const Plain = () => <p>A page without a heading.</p>;
function Article() {
  return (
    <article>
      <h1>Article</h1>
      <Link href="#end">To the end</Link>
      <h2 id="end">End</h2>
    </article>
  );
}
const Guestbook = (props: Record<string, unknown>) => <h1>{`Guestbook ${String(props.entries)}`}</h1>;

const modules = {
  '/app/layout.tsx': async () => ({ default: Layout }),
  '/app/page.tsx': async () => ({ default: Home }),
  '/app/about/page.tsx': async () => ({ default: About }),
  '/app/contact/page.tsx': async () => ({ default: Contact }),
  '/app/plain/page.tsx': async () => ({ default: Plain }),
  '/app/article/page.tsx': async () => ({ default: Article }),
  '/app/guestbook/page.tsx': async () => ({ default: Guestbook }),
};

function payload(page: string, url: string, overrides: Partial<PagePayload> = {}): PagePayload {
  return {
    kind: 'page',
    route: url.split('?')[0] ?? url,
    page: page === 'index' ? 'app/page.tsx' : `app/${page}/page.tsx`,
    segments: [{ id: 'app', layout: 'app/layout.tsx' }],
    props: {},
    params: {},
    shared: null,
    status: 200,
    url,
    version: 'v1',
    ...overrides,
  };
}

function pageHandler(path: string, body: PagePayload) {
  return http.get(`${origin}${path}`, () => HttpResponse.json(body));
}

async function renderApp(initial: PagePayload = payload('index', '/')) {
  window.history.replaceState(null, '', origin + initial.url);
  const loader = createModuleLoader(modules);
  const initialModules = await loader.loadRoute(initial);
  render(<RouterProvider initial={initial} initialModules={initialModules} loader={loader} errorPage={null} />);
  return { user: userEvent.setup() };
}

/** The router's live region. */
function announcer(): HTMLElement {
  const region = document.querySelector<HTMLElement>('[data-netjes-route-announcer]');
  if (!region) throw new Error('the router renders no live region');
  return region;
}

describe('the route announcer', () => {
  it('renders an empty, polite, atomic live region without a role once the page has hydrated', async () => {
    const initial = payload('index', '/');
    const loader = createModuleLoader(modules);
    const app = <RouterProvider initial={initial} initialModules={await loader.loadRoute(initial)} loader={loader} errorPage={null} />;
    const browserDocument = globalThis.document;
    vi.stubGlobal('document', undefined);
    let html: string;
    try {
      html = renderToString(app);
    } finally {
      vi.stubGlobal('document', browserDocument);
      vi.unstubAllGlobals();
    }
    const errors = vi.spyOn(console, 'error');
    const container = document.createElement('div');
    container.innerHTML = html;
    document.body.append(container);
    window.history.replaceState(null, '', initial.url);

    await act(async () => {
      hydrated = { root: hydrateRoot(container, app), container };
    });

    // Not in the server's HTML: documents, hydration and a Content-Security-Policy are as they were.
    expect(html).not.toContain('aria-live');
    const region = announcer();
    expect(region.getAttribute('aria-live')).toBe('polite');
    expect(region.getAttribute('aria-atomic')).toBe('true');
    expect(region.hasAttribute('role')).toBe(false);
    expect(region.textContent).toBe('');
    // Visually hidden, still read.
    expect(region.style.position).toBe('absolute');
    expect(region.style.width).toBe('1px');
    expect(region.style.overflow).toBe('hidden');
    expect(errors).not.toHaveBeenCalled();
  });

  it("announces the new page's title after a client navigation", async () => {
    server.use(pageHandler('/about', payload('about', '/about', { metadata: { title: 'About · Demo' } })));
    const { user } = await renderApp(payload('index', '/', { metadata: { title: 'Home · Demo' } }));
    expect(announcer().textContent).toBe('');

    await user.click(screen.getByRole('link', { name: 'About' }));

    await waitFor(() => expect(announcer().textContent).toBe('About · Demo'));
    expect(document.title).toBe('About · Demo');
  });

  it('announces the first h1, then the path, for a page without a title', async () => {
    server.use(pageHandler('/contact', payload('contact', '/contact')), pageHandler('/plain', payload('plain', '/plain')));
    const { user } = await renderApp();

    await user.click(screen.getByRole('link', { name: 'Contact' }));
    await waitFor(() => expect(announcer().textContent).toBe('Contact'));

    await act(() => router!.push('/plain'));
    await waitFor(() => expect(announcer().textContent).toBe('/plain'));
  });

  it('announces nothing on a fragment link or a refresh, with or without a title', async () => {
    let entries = 3;
    server.use(
      pageHandler('/article', payload('article', '/article', { metadata: { title: 'Article · Demo' } })),
      http.get(`${origin}/guestbook`, () => HttpResponse.json(payload('guestbook', '/guestbook', { props: { entries: ++entries } }))),
    );
    const { user } = await renderApp(payload('article', '/article', { metadata: { title: 'Article · Demo' } }));

    await user.click(screen.getByRole('link', { name: 'To the end' }));
    await waitFor(() => expect(window.location.hash).toBe('#end'));
    await act(() => router!.refresh());
    expect(announcer().textContent).toBe('');

    // A page without a title is named by its h1, which a refresh can change: that is the same page, not a new one.
    cleanup();
    await renderApp(payload('guestbook', '/guestbook', { props: { entries } }));
    await act(() => router!.refresh());
    expect(await screen.findByRole('heading', { name: 'Guestbook 4' })).toBeDefined();
    expect(announcer().textContent).toBe('');
  });
});
