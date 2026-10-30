import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, delay, http } from 'msw';
import { setupServer } from 'msw/node';
import { type ReactNode, useState } from 'react';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { z } from 'zod';
import { Form } from '../src/form.tsx';
import { Link } from '../src/link.tsx';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload } from '../src/payload.ts';
import { type InternalRouter, RouterProvider, useNavigation, useParams, usePathname, useRouter, useSearchParams, useSharedProps } from '../src/router.tsx';

const sharedSchema = z.object({ siteName: z.string() });

const origin = window.location.origin;
const server = setupServer();
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

function Layout({ children }: { children: ReactNode }) {
  const [count, setCount] = useState(0);
  const navigation = useNavigation();
  const shared = useSharedProps(sharedSchema);
  return (
    <div>
      <nav>
        <span>{shared.siteName}</span>
        <Link href="/">Home</Link>
        <Link href="/about">About</Link>
        <Link href="/blog/hello?tab=comments">Post</Link>
        <Link href="https://example.com/">External</Link>
        <button type="button" onClick={() => setCount((c) => c + 1)}>
          clicks {count}
        </button>
      </nav>
      <p role="status">{navigation.state}</p>
      {children}
    </div>
  );
}

const Home = () => <h1>Home</h1>;
const About = (props: Record<string, unknown>) => <h1>About {String(props.team)}</h1>;
function Post() {
  const params = useParams();
  const search = useSearchParams();
  const pathname = usePathname();
  return (
    <h1>
      Post {String(params.slug)} {search.get('tab')} {pathname}
    </h1>
  );
}
function Contact(props: Record<string, unknown>) {
  return (
    <Form method="post">
      <label>
        Name <input name="name" defaultValue="" />
      </label>
      <button type="submit">Send</button>
      {typeof props.error === 'string' && <p role="alert">{props.error}</p>}
    </Form>
  );
}
function Search() {
  return (
    <Form action="/search">
      <label>
        Query <input name="q" defaultValue="react" />
      </label>
      <button type="submit">Search</button>
    </Form>
  );
}
function Refresher(props: Record<string, unknown>) {
  const router = useRouter();
  pageRouter = router;
  return (
    <button type="button" onClick={() => router.refresh()}>
      refresh {String(props.n)}
    </button>
  );
}

const modules = {
  '/app/layout.tsx': async () => ({ default: Layout }),
  '/app/page.tsx': async () => ({ default: Home }),
  '/app/about/page.tsx': async () => ({ default: About }),
  '/app/blog/[slug]/page.tsx': async () => ({ default: Post }),
  '/app/contact/page.tsx': async () => ({ default: Contact }),
  '/app/search/page.tsx': async () => ({ default: Search }),
  '/app/refresh/page.tsx': async () => ({ default: Refresher }),
  '/app/thanks/page.tsx': async () => ({ default: () => <h1>Thanks</h1> }),
  // A chunk the browser cannot fetch: a dropped request, or a deploy that removed it.
  '/app/unreachable/page.tsx': async () => {
    throw new TypeError('Failed to fetch dynamically imported module: /assets/page-abc.js');
  },
};

function payload(page: string, url: string, overrides: Partial<PagePayload> = {}): PagePayload {
  return {
    kind: 'page',
    route: url.split('?')[0] ?? url,
    page: `app/${page}/page.tsx`,
    segments: [{ id: 'app', layout: 'app/layout.tsx' }],
    props: {},
    params: {},
    shared: { siteName: 'Demo' },
    status: 200,
    url,
    version: 'v1',
    ...overrides,
  };
}

function pageHandler(path: string, body: PagePayload | Record<string, unknown>, requests?: Request[]) {
  return http.get(`${origin}${path}`, ({ request }) => {
    requests?.push(request);
    return HttpResponse.json(body);
  });
}

async function renderApp(
  initial: PagePayload = payload('index', '/', { page: 'app/page.tsx' }),
  hardNavigate = vi.fn(),
  showDocument = vi.fn(),
  download = vi.fn(),
  // Where the browser is: the page's own path unless a test needs them to differ.
  location = origin + initial.url,
) {
  window.history.replaceState(null, '', location);
  // As the client entry creates it.
  const loader = createModuleLoader(modules, { nameFailedImports: true });
  const initialModules = await loader.loadRoute(initial);
  render(
    <>
      <RouterProvider
        initial={initial}
        initialModules={initialModules}
        loader={loader}
        errorPage={null}
        hardNavigate={hardNavigate}
        showDocument={showDocument}
        download={download}
      />
    </>,
  );
  return { user: userEvent.setup(), hardNavigate, showDocument };
}

/** The router as pages see it, captured by the Refresher test page. */
let pageRouter: ReturnType<typeof useRouter> | undefined;

describe('client navigation', () => {
  it('renders the initial page inside its layouts', async () => {
    await renderApp();

    expect(screen.getByRole('heading', { name: 'Home' })).toBeDefined();
    expect(screen.getByText('Demo')).toBeDefined();
  });

  it('fetches the page payload and renders it without a full page load', async () => {
    const requests: Request[] = [];
    server.use(pageHandler('/about', payload('about', '/about', { props: { team: 'core' } }), requests));
    const { user, hardNavigate } = await renderApp();

    await user.click(screen.getByRole('link', { name: 'About' }));

    expect(await screen.findByRole('heading', { name: 'About core' })).toBeDefined();
    expect(window.location.pathname).toBe('/about');
    expect(requests[0]?.headers.get('X-Netjes-Data')).toBe('1');
    expect(hardNavigate).not.toHaveBeenCalled();
  });

  it('keeps layout state across navigations', async () => {
    server.use(pageHandler('/about', payload('about', '/about')));
    const { user } = await renderApp();

    await user.click(screen.getByRole('button', { name: 'clicks 0' }));
    await user.click(screen.getByRole('link', { name: 'About' }));
    await screen.findByRole('heading', { name: /About/ });

    expect(screen.getByRole('button', { name: 'clicks 1' })).toBeDefined();
  });

  it('keeps layout state between a page with metadata and one without', async () => {
    const home = payload('index', '/', { page: 'app/page.tsx', metadata: { title: 'Home' } });
    server.use(pageHandler('/about', payload('about', '/about')), pageHandler('/', home));
    const { user } = await renderApp(home);

    await user.click(screen.getByRole('button', { name: 'clicks 0' }));
    await user.click(screen.getByRole('link', { name: 'About' }));
    await screen.findByRole('heading', { name: /About/ });
    expect(screen.getByRole('button', { name: 'clicks 1' })).toBeDefined();

    await user.click(screen.getByRole('button', { name: 'clicks 1' }));
    await user.click(screen.getByRole('link', { name: 'Home' }));
    await screen.findByRole('heading', { name: 'Home' });
    expect(screen.getByRole('button', { name: 'clicks 2' })).toBeDefined();
  });

  it('marks links to the current page with aria-current', async () => {
    server.use(pageHandler('/about', payload('about', '/about')));
    const { user } = await renderApp();

    expect(screen.getByRole('link', { name: 'Home' }).getAttribute('aria-current')).toBe('page');
    await user.click(screen.getByRole('link', { name: 'About' }));
    await screen.findByRole('heading', { name: /About/ });

    expect(screen.getByRole('link', { name: 'About' }).getAttribute('aria-current')).toBe('page');
    expect(screen.getByRole('link', { name: 'Home' }).getAttribute('aria-current')).toBeNull();
  });

  it('exposes params, search params and pathname to pages', async () => {
    server.use(pageHandler('/blog/hello', payload('blog/[slug]', '/blog/hello?tab=comments', { params: { slug: 'hello' } })));
    const { user } = await renderApp();

    await user.click(screen.getByRole('link', { name: 'Post' }));

    expect(await screen.findByRole('heading', { name: 'Post hello comments /blog/hello' })).toBeDefined();
  });

  it('leaves modified clicks and external links to the browser', async () => {
    const requests: Request[] = [];
    server.use(pageHandler('/about', payload('about', '/about'), requests));
    await renderApp();

    // fireEvent returns false when a handler called preventDefault(), i.e. when the router took over the click.
    const ctrlClickReachedBrowser = fireEvent.click(screen.getByRole('link', { name: 'About' }), { ctrlKey: true });
    const externalClickReachedBrowser = fireEvent.click(screen.getByRole('link', { name: 'External' }));

    expect(ctrlClickReachedBrowser).toBe(true);
    expect(externalClickReachedBrowser).toBe(true);
    expect(screen.getByRole('status').textContent).toBe('idle');
    expect(requests).toHaveLength(0);
  });

  it('shows the loading state while the next page is fetched', async () => {
    server.use(
      http.get(`${origin}/about`, async () => {
        await delay(50);
        return HttpResponse.json(payload('about', '/about'));
      }),
    );
    const { user } = await renderApp();

    await user.click(screen.getByRole('link', { name: 'About' }));

    expect(screen.getByRole('status').textContent).toBe('loading');
    await screen.findByRole('heading', { name: /About/ });
    expect(screen.getByRole('status').textContent).toBe('idle');
  });

  it('follows redirect payloads and records the final URL', async () => {
    server.use(
      pageHandler('/about', { kind: 'redirect', location: '/blog/hello', version: 'v1' }),
      pageHandler('/blog/hello', payload('blog/[slug]', '/blog/hello', { params: { slug: 'hello' } })),
    );
    const { user } = await renderApp();

    await user.click(screen.getByRole('link', { name: 'About' }));

    expect(await screen.findByRole('heading', { name: /Post hello/ })).toBeDefined();
    expect(window.location.pathname).toBe('/blog/hello');
  });

  it('reloads the document when the server runs a different build', async () => {
    server.use(pageHandler('/about', payload('about', '/about', { version: 'v2' })));
    const { user, hardNavigate } = await renderApp();

    await user.click(screen.getByRole('link', { name: 'About' }));

    await waitFor(() => expect(hardNavigate).toHaveBeenCalledWith(`${origin}/about`));
  });

  it('reloads the document when the page is not part of this build', async () => {
    server.use(pageHandler('/about', payload('brand-new', '/about')));
    const { user, hardNavigate } = await renderApp();

    await user.click(screen.getByRole('link', { name: 'About' }));

    await waitFor(() => expect(hardNavigate).toHaveBeenCalledWith(`${origin}/about`));
  });

  it('falls back to a full page load when the request fails', async () => {
    server.use(http.get(`${origin}/about`, () => HttpResponse.error()));
    const { user, hardNavigate } = await renderApp();

    await user.click(screen.getByRole('link', { name: 'About' }));

    await waitFor(() => expect(hardNavigate).toHaveBeenCalledWith(`${origin}/about`));
  });

  it("loads the page in full, once, when its module fails to load, and says which module", async () => {
    const errors = vi.spyOn(console, 'error').mockImplementation(() => {});
    server.use(pageHandler('/about', payload('unreachable', '/about')));
    const { user, hardNavigate } = await renderApp();

    // The hover prefetches the page and fails; the click loads it again and fails too.
    await user.click(screen.getByRole('link', { name: 'About' }));

    await waitFor(() => expect(hardNavigate).toHaveBeenCalledExactlyOnceWith(`${origin}/about`));
    const logged = errors.mock.calls.map((call) => call.map((part) => (part instanceof Error ? part.message : String(part))).join(' '));
    expect(logged).toEqual([expect.stringContaining('app/unreachable/page.tsx')]);
    errors.mockRestore();
  });

  it('renders the previous page from memory on back navigation', async () => {
    const requests: Request[] = [];
    server.use(pageHandler('/about', payload('about', '/about'), requests));
    const { user } = await renderApp();
    await user.click(screen.getByRole('link', { name: 'About' }));
    await screen.findByRole('heading', { name: /About/ });

    act(() => window.history.back());

    expect(await screen.findByRole('heading', { name: 'Home' })).toBeDefined();
    expect(requests).toHaveLength(1);
  });

  it('uses a prefetched payload instead of fetching again', async () => {
    const requests: Request[] = [];
    server.use(pageHandler('/about', payload('about', '/about'), requests));
    const { user } = await renderApp();

    await user.hover(screen.getByRole('link', { name: 'About' }));
    await waitFor(() => expect(requests).toHaveLength(1));
    await user.click(screen.getByRole('link', { name: 'About' }));

    expect(await screen.findByRole('heading', { name: /About/ })).toBeDefined();
    expect(requests).toHaveLength(1);
  });

  it('refetches the current page on refresh', async () => {
    let n = 0;
    server.use(http.get(`${origin}/refresh`, () => HttpResponse.json(payload('refresh', '/refresh', { props: { n: ++n } }))));
    const { user } = await renderApp(payload('refresh', '/refresh', { props: { n: 0 } }));

    await user.click(screen.getByRole('button', { name: 'refresh 0' }));

    expect(await screen.findByRole('button', { name: 'refresh 1' })).toBeDefined();
  });

  it('keeps the fragment of the page it refreshes, without scrolling', async () => {
    let n = 0;
    server.use(http.get(`${origin}/refresh`, () => HttpResponse.json(payload('refresh', '/refresh', { props: { n: ++n } }))));
    const { user } = await renderApp(payload('refresh', '/refresh', { props: { n: 0 } }));
    await act(() => pageRouter!.push('#section'));
    const historyLength = window.history.length;
    vi.mocked(window.scrollTo).mockClear();

    await user.click(screen.getByRole('button', { name: 'refresh 0' }));

    expect(await screen.findByRole('button', { name: 'refresh 1' })).toBeDefined();
    expect(window.location.pathname + window.location.hash).toBe('/refresh#section');
    expect(window.history.length).toBe(historyLength);
    expect(window.scrollTo).not.toHaveBeenCalled();
  });
});

describe('safety', () => {
  it('never hands a javascript: redirect to the browser', async () => {
    server.use(pageHandler('/about', { kind: 'redirect', location: 'javascript:alert(document.cookie)', version: 'v1' }));
    const { user, hardNavigate } = await renderApp();
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {});

    await user.click(screen.getByRole('link', { name: 'About' }));

    await waitFor(() => expect(hardNavigate).toHaveBeenCalled());
    for (const [href] of hardNavigate.mock.calls) expect(String(href)).toMatch(/^http:/);
    consoleError.mockRestore();
  });

  it('ignores router.push to a non-http URL', async () => {
    const { hardNavigate } = await renderApp(payload('refresh', '/refresh', { props: { n: 0 } }));
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {});

    await act(() => pageRouter!.push('javascript:alert(1)'));

    expect(hardNavigate).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'refresh 0' })).toBeDefined();
    consoleError.mockRestore();
  });

  it('survives a fragment that is not valid percent-encoding', async () => {
    const { user } = await renderApp(payload('refresh', '/refresh', { props: { n: 0 } }));

    await act(() => pageRouter!.push('#100%'));
    await user.click(screen.getByRole('button', { name: 'clicks 0' }));

    expect(screen.getByRole('button', { name: 'clicks 1' })).toBeDefined();
    expect(window.location.hash).toBe('#100%');
  });

  it('a fragment navigation cancels a slower pending navigation', async () => {
    server.use(
      http.get(`${origin}/about`, async () => {
        await delay(80);
        return HttpResponse.json(payload('about', '/about'));
      }),
    );
    await renderApp(payload('refresh', '/refresh', { props: { n: 0 } }));

    let slow: Promise<void> = Promise.resolve();
    act(() => {
      slow = pageRouter!.push('/about');
    });
    await act(() => pageRouter!.push('#section'));
    await act(async () => {
      await slow;
      await delay(100);
    });

    expect(screen.queryByRole('heading', { name: /About/ })).toBeNull();
    expect(window.location.pathname + window.location.hash).toBe('/refresh#section');
    expect(screen.getByRole('status').textContent).toBe('idle');
  });
});

describe('server-sent page URLs', () => {
  // A request path can start with `//` (https://app//evil.example/x); the server echoes it as the page's url.
  const doubleSlash = '//evil.example/x';

  /** Answers every GET, on any origin, with the Refresher page at `url`, and records the requests. */
  function answerAll(url: string, requests: Request[]) {
    let n = 0;
    server.use(
      http.get('*', ({ request }) => {
        requests.push(request);
        return HttpResponse.json(payload('refresh', url, { props: { n: ++n } }));
      }),
    );
  }

  /** Back or forward to a history entry the router has not seen, e.g. one from before a reload. */
  function popTo(url: string) {
    act(() => {
      window.history.pushState(null, '', url);
      window.dispatchEvent(new PopStateEvent('popstate', { state: null }));
    });
  }

  it('refreshes a page whose path starts with // from this origin', async () => {
    const requests: Request[] = [];
    answerAll(doubleSlash, requests);
    const { hardNavigate } = await renderApp(payload('refresh', doubleSlash, { props: { n: 0 } }));

    await act(() => pageRouter!.refresh());

    expect(requests.map((r) => r.url)).toEqual([origin + doubleSlash]);
    expect(hardNavigate).not.toHaveBeenCalled();
    expect(await screen.findByRole('button', { name: 'refresh 1' })).toBeDefined();
  });

  it('stays on this origin after going back to a page whose path starts with //', async () => {
    const requests: Request[] = [];
    answerAll(doubleSlash, requests);
    const { hardNavigate } = await renderApp(payload('refresh', '/refresh', { props: { n: 0 } }));
    popTo(origin + doubleSlash);
    await screen.findByRole('button', { name: 'refresh 1' });

    await act(() => pageRouter!.refresh());

    expect(requests.map((r) => r.url)).toEqual([origin + doubleSlash, origin + doubleSlash]);
    expect(hardNavigate).not.toHaveBeenCalled();
    expect(await screen.findByRole('button', { name: 'refresh 2' })).toBeDefined();
  });

  it('refreshes the page the browser is on when the first page has a url on another origin', async () => {
    const requests: Request[] = [];
    answerAll('/here', requests);
    const initial = payload('refresh', 'https://evil.example/x', { props: { n: 0 } });
    const { hardNavigate } = await renderApp(initial, vi.fn(), vi.fn(), vi.fn(), `${origin}/here`);

    await act(() => pageRouter!.refresh());

    expect(requests.map((r) => r.url)).toEqual([`${origin}/here`]);
    expect(hardNavigate).not.toHaveBeenCalled();
    expect(await screen.findByRole('button', { name: 'refresh 1' })).toBeDefined();
  });

  it('loads the page from the server when its payload names another origin', async () => {
    const requests: Request[] = [];
    answerAll('https://evil.example/x', requests);
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {});
    const { hardNavigate } = await renderApp(payload('refresh', '/refresh', { props: { n: 0 } }));

    popTo(`${origin}/x`);

    await waitFor(() => expect(hardNavigate).toHaveBeenCalledWith(`${origin}/x`));
    expect(screen.getByRole('button', { name: 'refresh 0' })).toBeDefined();
    consoleError.mockRestore();
  });
});

describe('forms', () => {
  const antiforgery = { formFieldName: '__RequestVerificationToken', headerName: 'RequestVerificationToken', requestToken: 'tok' };

  it('refuses a post to another origin handed straight to the router', async () => {
    const requests: Request[] = [];
    server.use(
      http.all('*', ({ request }) => {
        requests.push(request);
        return HttpResponse.json(payload('refresh', '/x', { props: { n: 1 } }));
      }),
    );
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {});
    const { hardNavigate } = await renderApp(payload('refresh', '/refresh', { props: { n: 0 } }));
    // What Form posts through, once it has left other origins to the browser.
    const router = pageRouter as InternalRouter;

    await act(() => router.submit(new URL('https://evil.example/x'), { contentType: 'application/x-www-form-urlencoded', body: 'a=1' }));

    expect(requests).toHaveLength(0);
    expect(hardNavigate).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'refresh 0' })).toBeDefined();
    consoleError.mockRestore();
  });

  it('posts the fields with the antiforgery token and follows the redirect', async () => {
    let body: URLSearchParams | undefined;
    server.use(
      http.post(`${origin}/contact`, async ({ request }) => {
        body = new URLSearchParams(await request.text());
        return HttpResponse.json({ kind: 'redirect', location: '/thanks', version: 'v1' });
      }),
      pageHandler('/thanks', payload('thanks', '/thanks')),
    );
    const { user } = await renderApp(payload('contact', '/contact', { antiforgery }));

    await user.type(screen.getByLabelText('Name'), 'Ada');
    await user.click(screen.getByRole('button', { name: 'Send' }));

    expect(await screen.findByRole('heading', { name: 'Thanks' })).toBeDefined();
    expect(body?.get('name')).toBe('Ada');
    expect(body?.get('__RequestVerificationToken')).toBe('tok');
    expect(window.location.pathname).toBe('/thanks');
  });

  it('fetches a token for a form post on a page that carries none (an output-cached page)', async () => {
    let header: string | null = null;
    server.use(
      http.get(`${origin}/_netjes/antiforgery`, () => HttpResponse.json({ ...antiforgery, requestToken: 'fetched' })),
      http.post(`${origin}/contact`, ({ request }) => {
        header = request.headers.get('RequestVerificationToken');
        return HttpResponse.json({ kind: 'redirect', location: '/thanks', version: 'v1' });
      }),
      pageHandler('/thanks', payload('thanks', '/thanks')),
    );
    const { user } = await renderApp(payload('contact', '/contact'));

    await user.click(screen.getByRole('button', { name: 'Send' }));

    expect(await screen.findByRole('heading', { name: 'Thanks' })).toBeDefined();
    expect(header).toBe('fetched');
  });

  it('re-renders the page with the props an action returns', async () => {
    server.use(
      http.post(`${origin}/contact`, () =>
        HttpResponse.json(payload('contact', '/contact', { props: { error: 'Name is required' }, status: 422 }), {
          status: 422,
        }),
      ),
    );
    const { user } = await renderApp(payload('contact', '/contact', { antiforgery }));

    await user.click(screen.getByRole('button', { name: 'Send' }));

    expect((await screen.findByRole('alert')).textContent).toBe('Name is required');
    expect(window.location.pathname).toBe('/contact');
  });

  it('shows an HTML page a post returns, as a native submission would', async () => {
    server.use(http.post(`${origin}/contact`, () => new HttpResponse('<h1>Thanks</h1>', { headers: { 'Content-Type': 'text/html' } })));
    const { user, hardNavigate, showDocument } = await renderApp(payload('contact', '/contact', { antiforgery }));

    await user.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() => expect(showDocument).toHaveBeenCalledWith('<h1>Thanks</h1>', `${origin}/contact`));
    expect(hardNavigate).not.toHaveBeenCalled();
  });

  it('keeps the page and its input when a post is rejected without a page', async () => {
    server.use(http.post(`${origin}/contact`, () => new HttpResponse(null, { status: 400 })));
    const { user, hardNavigate, showDocument } = await renderApp(payload('contact', '/contact', { antiforgery }));
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {});

    await user.type(screen.getByLabelText('Name'), 'Ada');
    await user.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() => expect(consoleError).toHaveBeenCalled());
    expect(screen.getByRole('status').textContent).toBe('idle');
    expect(showDocument).not.toHaveBeenCalled();
    expect(hardNavigate).not.toHaveBeenCalled();
    expect((screen.getByLabelText('Name') as HTMLInputElement).value).toBe('Ada');
    consoleError.mockRestore();
  });

  it('downloads an attachment a post returns', async () => {
    server.use(
      http.post(`${origin}/contact`, () =>
        new HttpResponse('a,b\n1,2', {
          headers: { 'Content-Type': 'text/csv', 'Content-Disposition': 'attachment; filename="export.csv"' },
        }),
      ),
    );
    const download = vi.fn();
    const { user, showDocument } = await renderApp(payload('contact', '/contact', { antiforgery }), vi.fn(), vi.fn(), download);

    await user.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() => expect(download).toHaveBeenCalledWith(expect.objectContaining({ size: 7, type: 'text/csv' }), 'export.csv'));
    expect(showDocument).not.toHaveBeenCalled();
  });

  it('never renders a plain-text response as HTML', async () => {
    server.use(
      http.post(`${origin}/contact`, () =>
        new HttpResponse('<img src=x onerror=alert(1)>', { headers: { 'Content-Type': 'text/plain' } }),
      ),
    );
    const { user, showDocument } = await renderApp(payload('contact', '/contact', { antiforgery }));
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {});

    await user.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() => expect(consoleError).toHaveBeenCalled());
    expect(showDocument).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Send' })).toBeDefined();
    consoleError.mockRestore();
  });

  it('keeps the page and its input when the post cannot reach the server', async () => {
    server.use(http.post(`${origin}/contact`, () => HttpResponse.error()));
    const { user, hardNavigate, showDocument } = await renderApp(payload('contact', '/contact', { antiforgery }));
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {});

    await user.type(screen.getByLabelText('Name'), 'Ada');
    await user.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() => expect(screen.getByRole('status').textContent).toBe('idle'));
    expect(hardNavigate).not.toHaveBeenCalled();
    expect(showDocument).not.toHaveBeenCalled();
    expect((screen.getByLabelText('Name') as HTMLInputElement).value).toBe('Ada');
    consoleError.mockRestore();
  });

  it('drops prefetched pages after a submission so they show fresh data', async () => {
    const requests: Request[] = [];
    server.use(
      pageHandler('/about', payload('about', '/about'), requests),
      http.post(`${origin}/contact`, () => HttpResponse.json(payload('contact', '/contact', { props: { error: 'Saved?' } }))),
    );
    const { user } = await renderApp(payload('contact', '/contact', { antiforgery }));

    await user.hover(screen.getByRole('link', { name: 'About' }));
    await waitFor(() => expect(requests).toHaveLength(1));
    await user.click(screen.getByRole('button', { name: 'Send' }));
    await screen.findByRole('alert');
    await user.click(screen.getByRole('link', { name: 'About' }));
    await screen.findByRole('heading', { name: /About/ });

    expect(requests).toHaveLength(2);
  });

  // A sign-in or sign-out: the pages kept for back and forward showed whoever was signed in before.
  it.each([
    { cookies: 'changed', loads: 1 },
    { cookies: null, loads: 0 },
  ])('loads the pages behind it from the server after a post that changes cookies (X-Netjes-Cookies: $cookies)', async ({ cookies, loads }) => {
    const requests: Request[] = [];
    server.use(
      http.post(`${origin}/contact`, () =>
        HttpResponse.json({ kind: 'redirect', location: '/thanks', version: 'v1' }, { headers: cookies ? { 'X-Netjes-Cookies': cookies } : {} }),
      ),
      pageHandler('/thanks', payload('thanks', '/thanks')),
      pageHandler('/contact', payload('contact', '/contact', { antiforgery, props: { error: 'loaded again' } }), requests),
    );
    const { user } = await renderApp(payload('contact', '/contact', { antiforgery }));

    await user.click(screen.getByRole('button', { name: 'Send' }));
    await screen.findByRole('heading', { name: 'Thanks' });
    act(() => window.history.back());

    await screen.findByRole('button', { name: 'Send' });
    expect(requests.filter((r) => new URL(r.url).pathname === '/contact')).toHaveLength(loads);
    expect(screen.queryAllByText('loaded again')).toHaveLength(loads);
  });

  it('keeps the page a post that changed cookies answered for its own history entry', async () => {
    const requests: Request[] = [];
    server.use(
      http.post(`${origin}/contact`, () =>
        HttpResponse.json({ kind: 'redirect', location: '/thanks', version: 'v1' }, { headers: { 'X-Netjes-Cookies': 'changed' } }),
      ),
      pageHandler('/thanks', payload('thanks', '/thanks'), requests),
      pageHandler('/about', payload('about', '/about', { props: { team: 'core' } })),
    );
    const { user } = await renderApp(payload('contact', '/contact', { antiforgery }));

    await user.click(screen.getByRole('button', { name: 'Send' }));
    await screen.findByRole('heading', { name: 'Thanks' });
    await user.click(screen.getByRole('link', { name: 'About' }));
    await screen.findByRole('heading', { name: 'About core' });
    act(() => window.history.back());

    expect(await screen.findByRole('heading', { name: 'Thanks' })).toBeDefined();
    expect(requests).toHaveLength(1);
  });

  // The prefetch went out with the old cookies; the one started over goes out after the post, so it is kept.
  it('starts a prefetch on its way over after a post that changes cookies, and keeps it', async () => {
    const about: Request[] = [];
    let answerFirst: () => void = () => {};
    const first = new Promise<void>((resolve) => {
      answerFirst = resolve;
    });
    server.use(
      http.get(`${origin}/about`, async ({ request }) => {
        about.push(request);
        const team = `request ${about.length}`;
        if (about.length === 1) await first;
        return HttpResponse.json(payload('about', '/about', { props: { team } }));
      }),
      http.post(`${origin}/contact`, () =>
        HttpResponse.json(payload('contact', '/contact', { antiforgery, props: { error: 'Signed in' } }), {
          headers: { 'X-Netjes-Cookies': 'changed' },
        }),
      ),
    );
    const { user } = await renderApp(payload('contact', '/contact', { antiforgery }));

    await user.hover(screen.getByRole('link', { name: 'About' }));
    await waitFor(() => expect(about).toHaveLength(1));
    await user.click(screen.getByRole('button', { name: 'Send' }));
    await screen.findByRole('alert');
    await waitFor(() => expect(about).toHaveLength(2));
    answerFirst();
    await user.click(screen.getByRole('link', { name: 'About' }));

    expect(await screen.findByRole('heading', { name: 'About request 2' })).toBeDefined();
    expect(about).toHaveLength(2);
  });

  it('turns GET forms into a navigation with the fields as query string', async () => {
    const requests: Request[] = [];
    server.use(pageHandler('/search', payload('about', '/search?q=react', { props: { team: 'search' } }), requests));
    const { user } = await renderApp(payload('search', '/'));

    await user.click(screen.getByRole('button', { name: 'Search' }));

    expect(await screen.findByRole('heading', { name: 'About search' })).toBeDefined();
    expect(new URL(requests[0]!.url).search).toBe('?q=react');
  });
});
