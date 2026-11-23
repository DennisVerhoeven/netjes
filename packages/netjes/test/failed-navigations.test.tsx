import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import type { ReactNode } from 'react';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { serverAction } from '../src/actions.tsx';
import { Link } from '../src/link.tsx';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload } from '../src/payload.ts';
import { RouterProvider, useHistoryState, useNavigation, useRouter } from '../src/router.tsx';

const origin = window.location.origin;
// A page that carries no token has the router fetch one before a post.
const server = setupServer(
  http.get(`${origin}/_netjes/antiforgery`, () => HttpResponse.json({ formFieldName: 'f', headerName: 'RequestVerificationToken', requestToken: 't' })),
);
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));
afterEach(() => {
  server.resetHandlers();
  vi.restoreAllMocks();
});
afterAll(() => server.close());

/** What `useNavigation()` says, as a page would show it. */
function Status() {
  const navigation = useNavigation();
  if (navigation.state !== 'idle') return <p role="status">{`${navigation.state} ${navigation.location}`}</p>;
  return <p role="status">{navigation.error ? `failed ${JSON.stringify(navigation.error)}` : 'idle'}</p>;
}

function Layout({ children }: { children: ReactNode }) {
  return (
    <div>
      <nav>
        <Link href="/about">About</Link>
        <Link href="/live">Live</Link>
      </nav>
      <Status />
      {children}
    </div>
  );
}

/** The router as pages see it. */
let pageRouter: ReturnType<typeof useRouter> | undefined;

function Live(props: Record<string, unknown>) {
  pageRouter = useRouter();
  return <h1>Live {String(props.n)}</h1>;
}

/** The setter of a value the about page keeps with its history entry. */
let setAboutNote: ((note: string) => void) | undefined;

function About() {
  const [, setNote] = useHistoryState('note', 'none');
  setAboutNote = setNote;
  return <h1>About</h1>;
}

const modules = {
  '/app/layout.tsx': async () => ({ default: Layout }),
  '/app/live/page.tsx': async () => ({ default: Live }),
  '/app/about/page.tsx': async () => ({ default: About }),
};

function payload(page: string, url: string, overrides: Partial<PagePayload> = {}): PagePayload {
  return {
    kind: 'page',
    route: url.split('?')[0] ?? url,
    page: `app/${page}/page.tsx`,
    segments: [{ id: 'app', layout: 'app/layout.tsx' }],
    props: {},
    params: {},
    shared: {},
    status: 200,
    url,
    version: 'v1',
    ...overrides,
  };
}

async function renderApp(initial: PagePayload = payload('live', '/live', { props: { n: 0 } }), { keepHistory = false } = {}) {
  // Unless a test set up the history itself.
  if (!keepHistory) window.history.replaceState(null, '', origin + initial.url);
  const loader = createModuleLoader(modules, { nameFailedImports: true });
  const initialModules = await loader.loadRoute(initial);
  const hardNavigate = vi.fn();
  render(<RouterProvider initial={initial} initialModules={initialModules} loader={loader} errorPage={null} hardNavigate={hardNavigate} />);
  return { user: userEvent.setup(), hardNavigate };
}

/** Keeps the router's own console.error out of the output, and lets a test read it. */
const quiet = () => vi.spyOn(console, 'error').mockImplementation(() => {});

/** How each request the router makes follows redirects: MSW's request reports `follow` for every one. */
function redirectModes() {
  const fetches = vi.spyOn(globalThis, 'fetch');
  return () => fetches.mock.calls.map(([, init]) => init?.redirect ?? 'follow');
}

describe('a navigation that fails while the browser is online', () => {
  it('keeps the page and reports that no answer came', async () => {
    quiet();
    server.use(http.get(`${origin}/about`, () => HttpResponse.error()));
    const { user, hardNavigate } = await renderApp();

    await user.click(screen.getByRole('link', { name: 'About' }));

    await waitFor(() => expect(screen.getByRole('status').textContent).toBe(`failed ${JSON.stringify({ kind: 'navigate', status: null, location: '/about' })}`));
    expect(screen.getByRole('heading', { name: 'Live 0' })).toBeDefined();
    expect(window.location.pathname).toBe('/live');
    expect(hardNavigate).not.toHaveBeenCalled();
  });

  it('keeps the page and reports the status of a server error that is no page', async () => {
    quiet();
    server.use(http.get(`${origin}/about`, () => new HttpResponse('<h1>Bad gateway</h1>', { status: 502, headers: { 'Content-Type': 'text/html' } })));
    const { hardNavigate } = await renderApp();

    await act(() => pageRouter!.push('/about'));

    expect(screen.getByRole('status').textContent).toBe(`failed ${JSON.stringify({ kind: 'navigate', status: 502, location: '/about' })}`);
    expect(screen.getByRole('heading', { name: 'Live 0' })).toBeDefined();
    expect(window.location.pathname).toBe('/live');
    expect(hardNavigate).not.toHaveBeenCalled();
  });

  it('keeps the page when a server error answers with JSON that is no page', async () => {
    quiet();
    server.use(http.get(`${origin}/about`, () => HttpResponse.json({ error: 'upstream timed out' }, { status: 504 })));
    const { hardNavigate } = await renderApp();

    await act(() => pageRouter!.push('/about'));

    expect(screen.getByRole('status').textContent).toBe(`failed ${JSON.stringify({ kind: 'navigate', status: 504, location: '/about' })}`);
    expect(hardNavigate).not.toHaveBeenCalled();
  });

  it('still loads an answer that is no page and no server error as a document', async () => {
    server.use(http.get(`${origin}/about`, () => new HttpResponse('<h1>Not here</h1>', { status: 404, headers: { 'Content-Type': 'text/html' } })));
    const { hardNavigate } = await renderApp();

    await act(() => pageRouter!.push('/about'));

    expect(hardNavigate).toHaveBeenCalledExactlyOnceWith(`${origin}/about`);
    expect(screen.getByRole('status').textContent).toBe('idle');
  });

  it('loads the URL as a document when it redirects somewhere fetch cannot follow', async () => {
    quiet();
    let answers = 0;
    server.use(
      // Following the redirect to the identity provider fails as a lost connection does; asked without following, it
      // is a redirect.
      http.get(`${origin}/about`, () =>
        ++answers === 1 ? HttpResponse.error() : new HttpResponse(null, { status: 302, headers: { Location: 'https://id.example/sign-in' } }),
      ),
    );
    const { hardNavigate } = await renderApp();
    const modes = redirectModes();

    await act(() => pageRouter!.push('/about'));

    expect(modes()).toEqual(['follow', 'manual']);
    expect(hardNavigate).toHaveBeenCalledExactlyOnceWith(`${origin}/about`);
    expect(screen.getByRole('status').textContent).toBe('idle');
  });

  it('asks again for a page whose prefetch failed on the server', async () => {
    let answers = 0;
    server.use(
      http.get(`${origin}/about`, () =>
        ++answers === 1 ? new HttpResponse('Bad gateway', { status: 502, headers: { 'Content-Type': 'text/plain' } }) : HttpResponse.json(payload('about', '/about')),
      ),
    );
    const { user } = await renderApp();
    const link = screen.getByRole('link', { name: 'About' });

    await user.hover(link);
    await waitFor(() => expect(answers).toBe(1));
    await user.click(link);

    expect(await screen.findByRole('heading', { name: 'About' })).toBeDefined();
    expect(answers).toBe(2);
  });
});

describe('a navigation that fails while the browser is offline', () => {
  it('loads the URL as a document, as before', async () => {
    quiet();
    vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(false);
    server.use(http.get(`${origin}/about`, () => HttpResponse.error()));
    const { hardNavigate } = await renderApp();
    const modes = redirectModes();

    await act(() => pageRouter!.push('/about'));

    expect(hardNavigate).toHaveBeenCalledExactlyOnceWith(`${origin}/about`);
    expect(modes()).toEqual(['follow']);
    expect(screen.getByRole('status').textContent).toBe('idle');
  });
});

describe('a refresh that fails', () => {
  it('keeps the page and its props, resolves, and reports the refresh', async () => {
    quiet();
    server.use(http.get(`${origin}/live`, () => HttpResponse.error()));
    const { hardNavigate } = await renderApp();

    await act(() => expect(pageRouter!.refresh()).resolves.toBeUndefined());

    expect(screen.getByRole('status').textContent).toBe(`failed ${JSON.stringify({ kind: 'refresh', status: null, location: '/live' })}`);
    expect(screen.getByRole('heading', { name: 'Live 0' })).toBeDefined();
    expect(hardNavigate).not.toHaveBeenCalled();
  });

  it('keeps the query of the page in the location it reports', async () => {
    quiet();
    server.use(http.get(`${origin}/live`, () => new HttpResponse(null, { status: 503 })));
    await renderApp(payload('live', '/live?tab=open', { props: { n: 0 } }));

    await act(() => pageRouter!.refresh());

    expect(screen.getByRole('status').textContent).toBe(`failed ${JSON.stringify({ kind: 'refresh', status: 503, location: '/live?tab=open' })}`);
  });

  it('clears the failure when the next refresh starts, and shows the page it brings', async () => {
    quiet();
    let release!: () => void;
    const held = new Promise<void>((resolve) => (release = resolve));
    let down = true;
    server.use(
      http.get(`${origin}/live`, async () => {
        if (down) return HttpResponse.error();
        await held;
        return HttpResponse.json(payload('live', '/live', { props: { n: 1 } }));
      }),
    );
    await renderApp();
    await act(() => pageRouter!.refresh());
    expect(screen.getByRole('status').textContent).toMatch(/^failed/);
    down = false;

    let again: Promise<void> = Promise.resolve();
    act(() => {
      again = pageRouter!.refresh();
    });
    expect(screen.getByRole('status').textContent).toBe('loading /live');
    release();
    await act(() => again);

    expect(screen.getByRole('status').textContent).toBe('idle');
    expect(screen.getByRole('heading', { name: 'Live 1' })).toBeDefined();
  });
});

/** Resolves once the browser has told the router about a step through history, which listens first. */
function popped(): Promise<void> {
  return new Promise((resolve) => window.addEventListener('popstate', () => resolve(), { once: true }));
}

describe('Back or Forward that fails', () => {
  it('steps back to the entry of the page on screen and reports the navigation', async () => {
    quiet();
    server.use(http.get(`${origin}/live`, () => HttpResponse.error()));
    // An entry from before a reload, whose page the router does not keep: Back asks the server for it.
    window.history.replaceState({ __netjes: 'before-reload', __netjesIndex: 5 }, '', `${origin}/live`);
    window.history.pushState({ __netjes: 'here', __netjesIndex: 6 }, '', `${origin}/about`);
    const { hardNavigate } = await renderApp(payload('about', '/about'), { keepHistory: true });
    const modes = redirectModes();

    const back = popped();
    window.history.back();
    await act(() => back);
    // Once the request failed, the router steps forward again, to the entry of the page on screen.
    const forward = popped();
    await act(() => forward);

    expect(modes()).toEqual(['follow', 'manual']);
    expect(window.location.pathname).toBe('/about');
    expect(window.history.state).toMatchObject({ __netjes: 'here', __netjesIndex: 6 });
    expect(screen.getByRole('heading', { name: 'About' })).toBeDefined();
    expect(screen.getByRole('status').textContent).toBe(`failed ${JSON.stringify({ kind: 'navigate', status: null, location: '/live' })}`);
    expect(hardNavigate).not.toHaveBeenCalled();
  });

  it('writes no history value into the entry it went to while it steps back', async () => {
    quiet();
    server.use(http.get(`${origin}/live`, () => HttpResponse.error()));
    window.history.replaceState({ __netjes: 'before-reload', __netjesIndex: 5 }, '', `${origin}/live`);
    window.history.pushState({ __netjes: 'here', __netjesIndex: 6 }, '', `${origin}/about`);
    await renderApp(payload('about', '/about'), { keepHistory: true });
    // The page sets a value right as the router steps back, while the browser is still on the entry Back went to.
    const go = window.history.go.bind(window.history);
    let thereMeanwhile: unknown;
    vi.spyOn(window.history, 'go').mockImplementationOnce((delta) => {
      go(delta);
      setAboutNote!('set while stepping back');
      thereMeanwhile = window.history.state;
    });

    const back = popped();
    window.history.back();
    await act(() => back);
    const forward = popped();
    await act(() => forward);

    expect(thereMeanwhile).toEqual({ __netjes: 'before-reload', __netjesIndex: 5 });
    expect(window.history.state).toMatchObject({ __netjes: 'here', __netjesIndex: 6 });
  });

  it('loads the URL as a document when the entry it went to has no place the router knows', async () => {
    quiet();
    server.use(http.get(`${origin}/live`, () => HttpResponse.error()));
    const { hardNavigate } = await renderApp(payload('about', '/about'));
    // An entry the app added itself, with no place: the router cannot count the step back from it.
    window.history.pushState({ app: true }, '', `${origin}/live?step=1`);
    await act(async () => {
      const back = popped();
      window.history.back();
      await back;
    });
    expect(hardNavigate).not.toHaveBeenCalled();

    const forward = popped();
    window.history.forward();
    await act(() => forward);

    await waitFor(() => expect(hardNavigate).toHaveBeenCalledExactlyOnceWith(`${origin}/live?step=1`));
    expect(screen.getByRole('status').textContent).toBe('idle');
  });
});

describe('a form post that fails', () => {
  it('reports the post as before, with its kind', async () => {
    quiet();
    server.use(http.post(`${origin}/live`, () => new HttpResponse('Bad gateway', { status: 502, headers: { 'Content-Type': 'text/plain' } })));
    await renderApp();
    const router = pageRouter as unknown as { submit(action: URL, body: { contentType: string; body: string }): Promise<void> };

    await act(() => router.submit(new URL(`${origin}/live`), { contentType: 'application/x-www-form-urlencoded', body: 'a=1' }));

    expect(screen.getByRole('status').textContent).toBe(`failed ${JSON.stringify({ kind: 'submit', status: 502, location: '/live' })}`);
  });
});

describe('a form post the server took, whose redirect leads to a page that fails', () => {
  /** The post went through: the server answers it with a redirect to /about. */
  const accepted = () => http.post(`${origin}/live`, () => HttpResponse.json({ kind: 'redirect', location: '/about?saved=1', version: 'v1' }));
  const post = () =>
    (pageRouter as unknown as { submit(action: URL, body: { contentType: string; body: string }): Promise<void> }).submit(new URL(`${origin}/live`), {
      contentType: 'application/x-www-form-urlencoded',
      body: 'a=1',
    });

  it('reports the navigation to that page, not the post, and keeps the page', async () => {
    quiet();
    server.use(
      accepted(),
      http.get(`${origin}/about`, () => new HttpResponse('<h1>Bad gateway</h1>', { status: 502, headers: { 'Content-Type': 'text/html' } })),
    );
    const { hardNavigate } = await renderApp();

    await act(post);

    expect(screen.getByRole('status').textContent).toBe(`failed ${JSON.stringify({ kind: 'navigate', status: 502, location: '/about?saved=1' })}`);
    expect(screen.getByRole('heading', { name: 'Live 0' })).toBeDefined();
    expect(window.location.pathname).toBe('/live');
    expect(hardNavigate).not.toHaveBeenCalled();
  });

  it('asks once more without following redirects when that page gets no answer, then keeps the page', async () => {
    quiet();
    const asked: string[] = [];
    server.use(
      accepted(),
      http.get(`${origin}/about`, () => {
        asked.push('/about');
        return HttpResponse.error();
      }),
    );
    const { hardNavigate } = await renderApp();
    const modes = redirectModes();

    await act(post);

    expect(asked).toHaveLength(2);
    expect(modes().at(-1)).toBe('manual');
    expect(screen.getByRole('status').textContent).toBe(`failed ${JSON.stringify({ kind: 'navigate', status: null, location: '/about?saved=1' })}`);
    expect(screen.getByRole('heading', { name: 'Live 0' })).toBeDefined();
    expect(hardNavigate).not.toHaveBeenCalled();
  });

  it('loads that page as a document when it redirects somewhere fetch cannot follow', async () => {
    quiet();
    let answers = 0;
    server.use(
      accepted(),
      http.get(`${origin}/about`, () =>
        ++answers === 1 ? HttpResponse.error() : new HttpResponse(null, { status: 302, headers: { Location: 'https://id.example/sign-in' } }),
      ),
    );
    const { hardNavigate } = await renderApp();

    await act(post);

    expect(hardNavigate).toHaveBeenCalledExactlyOnceWith(`${origin}/about?saved=1`);
    expect(screen.getByRole('status').textContent).toBe('idle');
  });
});

describe('a redirect to a URL that is no page', () => {
  /** The server's answer to the router's request for a URL no page answers, after a redirect. */
  const noPage = () => HttpResponse.json({ kind: 'document' });

  it('asks for the URL a redirect sent it to as a redirect target, and a link not', async () => {
    const marked: (string | null)[] = [];
    server.use(
      http.post(`${origin}/live`, () => HttpResponse.json({ kind: 'redirect', location: '/about', version: 'v1' })),
      http.get(`${origin}/about`, ({ request }) => {
        marked.push(request.headers.get('X-Netjes-Redirected'));
        return HttpResponse.json(payload('about', '/about'));
      }),
    );
    await renderApp();
    const router = pageRouter as unknown as { submit(action: URL, body: { contentType: string; body: string }): Promise<void> };

    await act(() => router.submit(new URL(`${origin}/live`), { contentType: 'application/x-www-form-urlencoded', body: 'a=1' }));
    await act(() => pageRouter!.push('/about'));

    expect(marked).toEqual(['1', null]);
  });

  it('loads it as a document, once, when the server says no page answers it', async () => {
    const files: Request[] = [];
    server.use(
      http.post(`${origin}/live`, () => HttpResponse.json({ kind: 'redirect', location: '/files/report.pdf?sig=abc', version: 'v1' })),
      http.get(`${origin}/files/report.pdf`, ({ request }) => {
        files.push(request);
        return noPage();
      }),
    );
    const { hardNavigate } = await renderApp();
    const router = pageRouter as unknown as { submit(action: URL, body: { contentType: string; body: string }): Promise<void> };

    await act(() => router.submit(new URL(`${origin}/live`), { contentType: 'application/x-www-form-urlencoded', body: 'intent=download' }));

    expect(files.map((request) => request.headers.get('X-Netjes-Redirected'))).toEqual(['1']);
    expect(hardNavigate).toHaveBeenCalledExactlyOnceWith(`${origin}/files/report.pdf?sig=abc`);
    expect(screen.getByRole('status').textContent).toBe('idle');
  });

  it('follows a redirect a page answers with to a URL no page answers, as a document', async () => {
    server.use(
      http.get(`${origin}/about`, ({ request }) =>
        request.headers.has('X-Netjes-Redirected')
          ? HttpResponse.json(payload('about', '/about'))
          : HttpResponse.json({ kind: 'redirect', location: '/signed/1', version: 'v1' }),
      ),
      http.get(`${origin}/signed/1`, ({ request }) => (request.headers.get('X-Netjes-Redirected') === '1' ? noPage() : HttpResponse.error())),
    );
    const { hardNavigate } = await renderApp();

    await act(() => pageRouter!.push('/about'));

    expect(hardNavigate).toHaveBeenCalledExactlyOnceWith(`${origin}/signed/1`);
  });

  it('asks for the URL a server action redirects to as a redirect target', async () => {
    const marked: (string | null)[] = [];
    server.use(
      http.post(`${origin}/_netjes/actions/files.download`, () =>
        HttpResponse.json({ kind: 'action', ok: true, status: 200, redirect: '/files/report.pdf', revalidate: false }),
      ),
      http.get(`${origin}/files/report.pdf`, ({ request }) => {
        marked.push(request.headers.get('X-Netjes-Redirected'));
        return noPage();
      }),
    );
    const { hardNavigate } = await renderApp();
    const download = serverAction<Record<string, never>>('files.download');

    await act(() => download({}));

    expect(marked).toEqual(['1']);
    expect(hardNavigate).toHaveBeenCalledExactlyOnceWith(`${origin}/files/report.pdf`);
  });
});
