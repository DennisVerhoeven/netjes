import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import type { ReactNode } from 'react';
import { type Root, hydrateRoot } from 'react-dom/client';
import { renderToString } from 'react-dom/server';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { Form } from '../src/form.tsx';
import { Link } from '../src/link.tsx';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload } from '../src/payload.ts';
import { RouterProvider, useRouter, useSearchParams } from '../src/router.tsx';

// What a screen reader and a keyboard user get from a client navigation: the router names the new page in a polite
// live region, as a screen reader names the page a document load opens, and the focus starts over at the top of the
// document when the page is replaced, as after a page load, or goes to the element a fragment names.

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
  document.body.removeAttribute('tabindex');
});
afterAll(() => server.close());

/** The router as the layout sees it. */
let router: ReturnType<typeof useRouter> | undefined;

function Layout({ children }: { children: ReactNode }) {
  const layoutRouter = useRouter();
  router = layoutRouter;
  return (
    <div>
      <nav aria-label="Main">
        <Link href="/">Home</Link>
        <Link href="/about">About</Link>
        <Link href="/contact">Contact</Link>
        <Link href="/welcome">Welcome</Link>
        <Link href="/tabs/two" scroll={false}>
          Tab two
        </Link>
      </nav>
      {/* Navigates as the user types, from any page. */}
      <input type="search" aria-label="Search" onChange={(event) => void layoutRouter.replace(`/search?q=${encodeURIComponent(event.currentTarget.value)}`)} />
      <input aria-label="Note" />
      <main>{children}</main>
    </div>
  );
}

const Home = () => <h1>Home</h1>;
function About() {
  return (
    <>
      <h1>About</h1>
      <Link href="/contact">Contact us</Link>
      <h2 id="team">Team</h2>
      <button id="closed" type="button" disabled>
        Closed
      </button>
    </>
  );
}
const Contact = () => <h1>Contact</h1>;
const Plain = () => <p>A page without a heading.</p>;
function Article() {
  return (
    <article>
      <h1>Article</h1>
      <Link href="#end">To the end</Link>
      <Link href="#more">To more</Link>
      <h2 id="end">End</h2>
      <button id="more" type="button">
        More
      </button>
    </article>
  );
}
const Guestbook = (props: Record<string, unknown>) => <h1>{`Guestbook ${String(props.entries)}`}</h1>;
function Results() {
  const search = useSearchParams();
  return (
    <>
      <h1>Results</h1>
      <p>{`page ${search.get('page') ?? '1'}`}</p>
      <Link href="/results?page=2">Next page</Link>
    </>
  );
}
function Search() {
  const search = useSearchParams();
  return <h1>{`Results for ${search.get('q') ?? ''}`}</h1>;
}
function Welcome() {
  return (
    <>
      <h1>Welcome</h1>
      <input aria-label="Your name" autoFocus />
    </>
  );
}
const TabOne = () => <h1>Tab one</h1>;
function TabTwo(props: Record<string, unknown>) {
  const pageRouter = useRouter();
  return (
    <>
      <h1>{`Tab two ${String(props.version)}`}</h1>
      <button type="button" onClick={() => void pageRouter.refresh()}>
        Refresh
      </button>
      <Form method="post">
        <button type="submit">Save</button>
      </Form>
    </>
  );
}

const modules = {
  '/app/layout.tsx': async () => ({ default: Layout }),
  '/app/page.tsx': async () => ({ default: Home }),
  '/app/about/page.tsx': async () => ({ default: About }),
  '/app/contact/page.tsx': async () => ({ default: Contact }),
  '/app/plain/page.tsx': async () => ({ default: Plain }),
  '/app/article/page.tsx': async () => ({ default: Article }),
  '/app/guestbook/page.tsx': async () => ({ default: Guestbook }),
  '/app/results/page.tsx': async () => ({ default: Results }),
  '/app/search/page.tsx': async () => ({ default: Search }),
  '/app/welcome/page.tsx': async () => ({ default: Welcome }),
  '/app/tabs/one/page.tsx': async () => ({ default: TabOne }),
  '/app/tabs/two/page.tsx': async () => ({ default: TabTwo }),
};

const antiforgery = { formFieldName: '__RequestVerificationToken', headerName: 'RequestVerificationToken', requestToken: 'tok' };

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

describe('the focus after a navigation', () => {
  it('starts the focus over at the top of the document when a navigation replaces the page', async () => {
    server.use(pageHandler('/about', payload('about', '/about')), pageHandler('/contact', payload('contact', '/contact')));
    const { user } = await renderApp();
    // An app's own tabindex on body is put back.
    document.body.setAttribute('tabindex', '0');
    const focus = vi.spyOn(document.body, 'focus');

    // From a link in the layout, which stays mounted with the focus unless the router moves it.
    await user.click(screen.getByRole('link', { name: 'About' }));
    await screen.findByRole('heading', { name: 'About' });
    expect(focus).toHaveBeenCalledExactlyOnceWith({ preventScroll: true, focusVisible: false });
    expect(document.activeElement).toBe(document.body);
    expect(document.body.getAttribute('tabindex')).toBe('0');

    // From a link in the page, which goes with it.
    document.body.removeAttribute('tabindex');
    focus.mockClear();
    await user.click(screen.getByRole('link', { name: 'Contact us' }));
    await screen.findByRole('heading', { name: 'Contact' });
    expect(focus).toHaveBeenCalledExactlyOnceWith({ preventScroll: true, focusVisible: false });
    expect(document.activeElement).toBe(document.body);
    expect(document.body.hasAttribute('tabindex')).toBe(false);
  });

  it('moves the focus to the target of a fragment link on the same page', async () => {
    // Hovering a link prefetches its page, this one included.
    server.use(pageHandler('/article', payload('article', '/article')));
    const { user } = await renderApp(payload('article', '/article'));
    const end = screen.getByRole('heading', { name: 'End' });
    const more = screen.getByRole('button', { name: 'More' });
    const focusEnd = vi.spyOn(end, 'focus');
    const focusMore = vi.spyOn(more, 'focus');

    // A heading takes the focus only with a tabindex, which the router gives it until it loses the focus.
    await user.click(screen.getByRole('link', { name: 'To the end' }));
    await waitFor(() => expect(document.activeElement).toBe(end));
    expect(end.getAttribute('tabindex')).toBe('-1');
    expect(focusEnd).toHaveBeenCalled();
    for (const [options] of focusEnd.mock.calls) expect(options).toEqual({ preventScroll: true });
    expect(end.scrollIntoView).toHaveBeenCalled();

    // A button takes it as it is.
    await user.click(screen.getByRole('link', { name: 'To more' }));
    await waitFor(() => expect(document.activeElement).toBe(more));
    expect(end.hasAttribute('tabindex')).toBe(false);
    expect(more.hasAttribute('tabindex')).toBe(false);
    expect(focusMore).toHaveBeenCalledExactlyOnceWith({ preventScroll: true });
  });

  it("moves the focus to the fragment's target on the page it navigates to", async () => {
    server.use(pageHandler('/about', payload('about', '/about')));
    await renderApp();
    const focus = vi.spyOn(HTMLElement.prototype, 'focus');

    await act(() => router!.push('/about#team'));

    const team = await screen.findByRole('heading', { name: 'Team' });
    expect(document.activeElement).toBe(team);
    expect(team.getAttribute('tabindex')).toBe('-1');
    expect(focus).toHaveBeenCalled();
    for (const [options] of focus.mock.calls) expect(options).toEqual({ preventScroll: true });
  });

  it("leaves the focus to the other rules when the fragment's element cannot take it", async () => {
    server.use(pageHandler('/about', payload('about', '/about')));
    await renderApp();
    const focusBody = vi.spyOn(document.body, 'focus');

    await act(() => router!.push('/about#closed'));

    // A disabled button takes no focus, not even with a tabindex, which the router takes off again. On another page the
    // focus then starts over at the top.
    const closed = await screen.findByRole('button', { name: 'Closed' });
    expect(closed.hasAttribute('tabindex')).toBe(false);
    expect(focusBody).toHaveBeenCalledExactlyOnceWith({ preventScroll: true, focusVisible: false });
    expect(document.activeElement).toBe(document.body);
  });

  it('leaves the focus where it is when only the query changes', async () => {
    server.use(http.get(`${origin}/results`, ({ request }) => HttpResponse.json(payload('results', `/results${new URL(request.url).search}`))));
    const { user } = await renderApp(payload('results', '/results'));
    const next = screen.getByRole('link', { name: 'Next page' });

    await user.click(next);

    expect(await screen.findByText('page 2')).toBeDefined();
    expect(document.activeElement).toBe(next);
  });

  it('keeps the focus in a field of the layout that navigates to another page as you type', async () => {
    server.use(http.get(`${origin}/search`, ({ request }) => HttpResponse.json(payload('search', `/search${new URL(request.url).search}`))));
    const { user } = await renderApp();
    const field = screen.getByRole('searchbox', { name: 'Search' });

    await user.type(field, 'ab');

    expect(await screen.findByRole('heading', { name: 'Results for ab' })).toBeDefined();
    expect(window.location.pathname).toBe('/search');
    expect(document.activeElement).toBe(field);
  });

  it('leaves the focus alone with scroll={false} while its link still has it, on refresh, and after a post that stays on its page', async () => {
    let version = 0;
    const tabTwo = () => HttpResponse.json(payload('tabs/two', '/tabs/two', { antiforgery, props: { version: ++version } }));
    server.use(http.get(`${origin}/tabs/two`, tabTwo), http.post(`${origin}/tabs/two`, tabTwo));
    const { user } = await renderApp(payload('tabs/one', '/tabs/one', { antiforgery }));
    const tab = screen.getByRole('link', { name: 'Tab two' });

    await user.click(tab);
    expect(await screen.findByRole('heading', { name: 'Tab two 1' })).toBeDefined();
    expect(document.activeElement).toBe(tab);

    const refresh = screen.getByRole('button', { name: 'Refresh' });
    await user.click(refresh);
    expect(await screen.findByRole('heading', { name: 'Tab two 2' })).toBeDefined();
    expect(document.activeElement).toBe(refresh);

    const save = screen.getByRole('button', { name: 'Save' });
    await user.click(save);
    expect(await screen.findByRole('heading', { name: 'Tab two 3' })).toBeDefined();
    expect(window.location.pathname).toBe('/tabs/two');
    expect(document.activeElement).toBe(save);
  });

  it('leaves the focus where the new page or the user put it', async () => {
    let release = () => {};
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      pageHandler('/welcome', payload('welcome', '/welcome')),
      http.get(`${origin}/contact`, async () => {
        await held;
        return HttpResponse.json(payload('contact', '/contact'));
      }),
    );
    const { user } = await renderApp();

    // The new page's autoFocus.
    await user.click(screen.getByRole('link', { name: 'Welcome' }));
    const name = await screen.findByRole('textbox', { name: 'Your name' });
    expect(document.activeElement).toBe(name);

    // A field the user went to while the next page was on its way.
    await user.click(screen.getByRole('link', { name: 'Contact' }));
    const note = screen.getByRole('textbox', { name: 'Note' });
    await user.click(note);
    await act(async () => release());
    expect(await screen.findByRole('heading', { name: 'Contact' })).toBeDefined();
    expect(document.activeElement).toBe(note);
  });
});
