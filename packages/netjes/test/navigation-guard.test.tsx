import { act, cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { useState } from 'react';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { Form } from '../src/form.tsx';
import { Link } from '../src/link.tsx';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload } from '../src/payload.ts';
import { type NavigationAttempt, type Router, RouterProvider, useNavigationGuard, useRouter } from '../src/router.tsx';

const origin = window.location.origin;
const server = setupServer();
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

/** The attempts the guards were asked about, in order, by the guard asked, and the waiters for the next one. */
const asked: Array<{ guard: string; attempt: NavigationAttempt }> = [];
const waiting: Array<(attempt: NavigationAttempt) => void> = [];
afterEach(() => {
  asked.length = 0;
  waiting.length = 0;
});

/** Resolves with the next attempt a guard is asked about: the signal a test waits on. */
function nextAttempt(): Promise<NavigationAttempt> {
  return new Promise((resolve) => waiting.push(resolve));
}

function asker(guard: string) {
  return (attempt: NavigationAttempt) => {
    asked.push({ guard, attempt });
    for (const resolve of waiting.splice(0)) resolve(attempt);
  };
}

/** The only attempt asked so far. */
function onlyAttempt(): NavigationAttempt {
  expect(asked).toHaveLength(1);
  return asked[0]!.attempt;
}

/** The router as the editor sees it. */
let editorRouter: Router | undefined;

/** Files on their way up: a second guard, which starts to hold when "Uploading" is ticked. */
function Uploads() {
  const [uploading, setUploading] = useState(false);
  useNavigationGuard({ when: uploading, onAttempt: asker('uploads') });
  return (
    <label>
      <input type="checkbox" checked={uploading} onChange={(event) => setUploading(event.target.checked)} /> Uploading
    </label>
  );
}

/** A form with changes: holds every navigation while "Unsaved" is ticked. */
function Editor() {
  const router = useRouter();
  editorRouter = router;
  const [unsaved, setUnsaved] = useState(true);
  useNavigationGuard({ when: unsaved, onAttempt: asker('changes') });
  return (
    <>
      <h1>Editor</h1>
      <label>
        <input type="checkbox" checked={unsaved} onChange={(event) => setUnsaved(event.target.checked)} /> Unsaved
      </label>
      <Uploads />
      <Link href="/other">Other</Link>
      <Link href="#notes">Notes</Link>
      {/* Not a Link: the browser moves to the fragment itself, as in Markdown content or a skip link. */}
      <a href="#plain">Plain</a>
      <Link href="https://example.com/">Elsewhere</Link>
      <Form method="get" action="/other">
        <label>
          Search <input name="q" defaultValue="tulips" />
        </label>
        <button type="submit">Search</button>
      </Form>
      <Form method="post">
        <button type="submit">Save</button>
      </Form>
      <button type="button" onClick={() => void router.replace('/other?tab=2')}>
        Replace
      </button>
      <h2 id="notes">Notes</h2>
    </>
  );
}

function Other() {
  return (
    <>
      <h1>Other</h1>
      <Link href="/editor">Editor</Link>
      <Link href="/other?again">Other again</Link>
    </>
  );
}

const modules = {
  '/app/editor/page.tsx': async () => ({ default: Editor }),
  '/app/other/page.tsx': async () => ({ default: Other }),
};

function payload(page: string, url: string, overrides: Partial<PagePayload> = {}): PagePayload {
  return {
    kind: 'page',
    route: url.split(/[?#]/)[0] ?? url,
    page: `app/${page}/page.tsx`,
    segments: [],
    props: {},
    params: {},
    shared: null,
    status: 200,
    url,
    version: 'v1',
    ...overrides,
  };
}

/** Serves each path as the page of that name, and records the requests. */
function pages(paths: string[], requests: string[] = []) {
  server.use(
    ...paths.map((path) =>
      http.get(`${origin}${path}`, ({ request }) => {
        const url = new URL(request.url);
        requests.push(url.pathname + url.search);
        return HttpResponse.json(payload(path.slice(1), url.pathname + url.search));
      }),
    ),
  );
  return requests;
}

async function renderApp(initial: PagePayload, hardNavigate = vi.fn(), state: unknown = null) {
  window.history.replaceState(state, '', origin + initial.url);
  const loader = createModuleLoader(modules);
  render(
    <RouterProvider initial={initial} initialModules={await loader.loadRoute(initial)} loader={loader} errorPage={null} hardNavigate={hardNavigate} />,
  );
  return userEvent.setup();
}

describe('useNavigationGuard', () => {
  it('holds a link while it holds, asking first, and goes on when the attempt proceeds', async () => {
    pages(['/other']);
    const user = await renderApp(payload('editor', '/editor'));

    await user.click(screen.getByRole('link', { name: 'Other' }));

    const attempt = onlyAttempt();
    expect(attempt).toMatchObject({ to: '/other', type: 'push' });
    expect(window.location.pathname).toBe('/editor');
    expect(screen.getByRole('heading', { name: 'Editor' })).toBeDefined();

    await act(async () => attempt.proceed());
    await screen.findByRole('heading', { name: 'Other' });
    expect(window.location.pathname).toBe('/other');
  });
  it('keeps the page when the attempt stays, and resolves the navigation promise', async () => {
    const requests = pages(['/other']);
    await renderApp(payload('editor', '/editor'));

    let pushed = false;
    const push = editorRouter!.push('/other').then(() => void (pushed = true));
    const attempt = onlyAttempt();
    await Promise.resolve();
    expect(pushed).toBe(false);
    attempt.stay();
    await push;

    expect(pushed).toBe(true);
    expect(window.location.pathname).toBe('/editor');
    expect(requests).toEqual([]);
    expect(screen.getByRole('heading', { name: 'Editor' })).toBeDefined();
  });

  it('holds a form that navigates, with its query, and router.replace', async () => {
    pages(['/other']);
    const user = await renderApp(payload('editor', '/editor'));

    await user.click(screen.getByRole('button', { name: 'Search' }));
    expect(onlyAttempt()).toMatchObject({ to: '/other?q=tulips', type: 'push' });
    onlyAttempt().stay();
    asked.length = 0;

    await user.click(screen.getByRole('button', { name: 'Replace' }));
    const attempt = onlyAttempt();
    expect(attempt).toMatchObject({ to: '/other?tab=2', type: 'replace' });
    const length = window.history.length;
    await act(async () => attempt.proceed());
    await screen.findByRole('heading', { name: 'Other' });
    expect(window.location.pathname + window.location.search).toBe('/other?tab=2');
    expect(window.history.length).toBe(length);
  });

  it('holds nothing once it no longer holds', async () => {
    pages(['/other']);
    const user = await renderApp(payload('editor', '/editor'));

    await user.click(screen.getByRole('checkbox', { name: 'Unsaved' }));
    await user.click(screen.getByRole('link', { name: 'Other' }));

    await screen.findByRole('heading', { name: 'Other' });
    expect(asked).toEqual([]);
  });

  it('holds no link to a fragment of the page on screen, no form post and no other site, which the browser asks about', async () => {
    const posts: string[] = [];
    server.use(
      http.post(`${origin}/editor`, ({ request }) => {
        posts.push(new URL(request.url).pathname);
        return HttpResponse.json(payload('editor', '/editor'));
      }),
    );
    const hardNavigate = vi.fn();
    const user = await renderApp(payload('editor', '/editor'), hardNavigate);

    await user.click(screen.getByRole('link', { name: 'Notes' }));
    expect(window.location.hash).toBe('#notes');
    await user.click(screen.getByRole('button', { name: 'Save' }));
    await vi.waitFor(() => expect(posts).toEqual(['/editor']));
    await act(() => editorRouter!.push('https://example.com/elsewhere'));

    expect(hardNavigate).toHaveBeenCalledWith('https://example.com/elsewhere');
    expect(asked).toEqual([]);
  });

  it('asks every guard that holds in turn, the one that started to hold last first, until one keeps the page', async () => {
    pages(['/other']);
    const user = await renderApp(payload('editor', '/editor'));
    await user.click(screen.getByRole('checkbox', { name: 'Uploading' }));

    await user.click(screen.getByRole('link', { name: 'Other' }));
    expect(asked.map(({ guard }) => guard)).toEqual(['uploads']);
    act(() => asked[0]!.attempt.proceed());
    expect(asked.map(({ guard }) => guard)).toEqual(['uploads', 'changes']);
    act(() => asked[1]!.attempt.stay());
    expect(window.location.pathname).toBe('/editor');

    await user.click(screen.getByRole('link', { name: 'Other' }));
    act(() => asked[2]!.attempt.proceed());
    await act(async () => asked[3]!.attempt.proceed());
    await screen.findByRole('heading', { name: 'Other' });
  });

  it('asks about a newer navigation in place of one still waiting, whose answer then does nothing', async () => {
    pages(['/other']);
    const user = await renderApp(payload('editor', '/editor'));

    // Asking changes nothing on screen yet: the push needs no act() until an answer lets it go on.
    const firstPush = editorRouter!.push('/other?first');
    const first = onlyAttempt();
    await user.click(screen.getByRole('link', { name: 'Other' }));
    expect(asked).toHaveLength(2);
    await firstPush;
    await act(async () => first.proceed());
    expect(window.location.pathname).toBe('/editor');

    await act(async () => asked[1]!.attempt.proceed());
    await screen.findByRole('heading', { name: 'Other' });
    expect(window.location.search).toBe('');
  });

  it('goes on when the attempt proceeds after its guard stopped holding, as after saving first', async () => {
    pages(['/other']);
    const user = await renderApp(payload('editor', '/editor'));
    await user.click(screen.getByRole('link', { name: 'Other' }));
    const attempt = onlyAttempt();

    await user.click(screen.getByRole('checkbox', { name: 'Unsaved' }));
    await act(async () => attempt.proceed());

    await screen.findByRole('heading', { name: 'Other' });
  });

  it('drops an attempt still waiting when a navigation goes on once no guard holds, as after a save', async () => {
    pages(['/other']);
    const user = await renderApp(payload('editor', '/editor'));
    const firstPush = editorRouter!.push('/other?first');
    const first = onlyAttempt();

    await user.click(screen.getByRole('checkbox', { name: 'Unsaved' }));
    await act(() => editorRouter!.push('/other?second'));
    await screen.findByRole('heading', { name: 'Other' });
    // Resolves with the navigation it starts, or at once once dropped.
    await act(async () => {
      first.proceed();
      await firstPush;
    });

    expect(window.location.pathname + window.location.search).toBe('/other?second');
  });

  it('rejects a navigation that carries what the browser cannot copy before any guard is asked', async () => {
    await renderApp(payload('editor', '/editor'));

    const push = editorRouter!.push('/other', { historyState: { run: () => {} } });
    expect(asked).toEqual([]);
    await expect(push).rejects.toThrow();
  });
});

/** Resolves once the browser has told the router about a step through history, which listens first. */
function popped(): Promise<void> {
  return new Promise((resolve) => window.addEventListener('popstate', () => resolve(), { once: true }));
}

/** Takes `delta` steps through history and waits for the browser to tell the router. */
async function go(delta: number) {
  const done = popped();
  window.history.go(delta);
  await act(() => done);
}

/** Goes from the other page to the editor by its link: an entry the editor holds, with one before it. */
async function toEditor(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole('link', { name: 'Editor' }));
  await screen.findByRole('heading', { name: 'Editor' });
}

const where = () => window.location.pathname + window.location.search;

describe('useNavigationGuard and the browser history', () => {
  it('puts the entry back on Back, then asks, and goes back when the attempt proceeds', async () => {
    pages(['/editor', '/other']);
    const user = await renderApp(payload('other', '/other'));
    await toEditor(user);

    const asking = nextAttempt();
    window.history.back();
    const attempt = await act(() => asking);

    expect(attempt).toMatchObject({ to: '/other', type: 'back' });
    expect(where()).toBe('/editor');
    expect(screen.getByRole('heading', { name: 'Editor' })).toBeDefined();

    const done = popped();
    attempt.proceed();
    await act(() => done);
    expect(where()).toBe('/other');
    expect(screen.getByRole('heading', { name: 'Other' })).toBeDefined();
  });

  it('stays on the entry when the attempt stays, and asks again on the next Back', async () => {
    pages(['/editor', '/other']);
    const user = await renderApp(payload('other', '/other'));
    await toEditor(user);

    let asking = nextAttempt();
    window.history.back();
    (await act(() => asking)).stay();
    expect(where()).toBe('/editor');

    asking = nextAttempt();
    window.history.back();
    expect(await act(() => asking)).toMatchObject({ to: '/other', type: 'back' });
    expect(where()).toBe('/editor');
    expect(screen.getByRole('heading', { name: 'Editor' })).toBeDefined();
  });

  it('holds Forward the same way', async () => {
    pages(['/editor', '/other']);
    const user = await renderApp(payload('editor', '/editor'));
    await user.click(screen.getByRole('checkbox', { name: 'Unsaved' }));
    await user.click(screen.getByRole('link', { name: 'Other' }));
    await screen.findByRole('heading', { name: 'Other' });
    // Back shows the editor from memory, which holds again as it mounts.
    await go(-1);
    expect(screen.getByRole('checkbox', { name: 'Unsaved' })).toHaveProperty('checked', true);

    const asking = nextAttempt();
    window.history.forward();
    const attempt = await act(() => asking);
    expect(attempt).toMatchObject({ to: '/other', type: 'forward' });
    expect(where()).toBe('/editor');

    const done = popped();
    attempt.proceed();
    await act(() => done);
    expect(screen.getByRole('heading', { name: 'Other' })).toBeDefined();
  });

  it('puts back several steps at once, and takes them all when the attempt proceeds', async () => {
    pages(['/editor', '/other']);
    const user = await renderApp(payload('other', '/other'));
    await user.click(screen.getByRole('link', { name: 'Other again' }));
    await vi.waitFor(() => expect(where()).toBe('/other?again'));
    await toEditor(user);

    const asking = nextAttempt();
    window.history.go(-2);
    const attempt = await act(() => asking);
    expect(attempt).toMatchObject({ to: '/other', type: 'back' });
    expect(where()).toBe('/editor');

    const done = popped();
    attempt.proceed();
    await act(() => done);
    expect(where()).toBe('/other');
  });

  it('takes the reader to the entry they chose when the attempt proceeds after the page moved within itself', async () => {
    pages(['/editor', '/other']);
    const user = await renderApp(payload('other', '/other'));
    await toEditor(user);
    const asking = nextAttempt();
    window.history.back();
    const attempt = await act(() => asking);

    // A fragment link, which keeps the page and is not held, adds an entry while the question is open.
    await user.click(screen.getByRole('link', { name: 'Notes' }));
    expect(window.location.hash).toBe('#notes');
    const done = popped();
    attempt.proceed();
    await act(() => done);

    expect(where()).toBe('/other');
    expect(screen.getByRole('heading', { name: 'Other' })).toBeDefined();
  });

  it('keeps holding Back after the browser moved to a fragment itself, from a plain link', async () => {
    pages(['/editor', '/other']);
    const user = await renderApp(payload('other', '/other'));
    await toEditor(user);
    // The browser adds the entry itself, and fires popstate with no state.
    const moved = popped();
    await user.click(screen.getByRole('link', { name: 'Plain' }));
    await act(() => moved);
    expect(window.location.hash).toBe('#plain');
    // Leaves by a link the guard lets go on, and comes back by a link.
    await user.click(screen.getByRole('link', { name: 'Other' }));
    await act(async () => onlyAttempt().proceed());
    await screen.findByRole('heading', { name: 'Other' });
    await toEditor(user);

    const asking = nextAttempt();
    await go(-1);

    expect(screen.getByRole('heading', { name: 'Editor' })).toBeDefined();
    expect(await act(() => asking)).toMatchObject({ to: '/other', type: 'back' });
    expect(where()).toBe('/editor');
  });

  it('keeps the place of the entry on screen when the browser replaces it, for the same plain link followed again', async () => {
    pages(['/editor', '/other']);
    const user = await renderApp(payload('other', '/other'));
    await toEditor(user);
    const moved = popped();
    await user.click(screen.getByRole('link', { name: 'Plain' }));
    await act(() => moved);
    // A link to the URL on screen replaces its entry, with no state, and fires popstate (jsdom does nothing there).
    act(() => {
      window.history.replaceState(null, '', window.location.href);
      window.dispatchEvent(new PopStateEvent('popstate', { state: null }));
    });
    // An entry after it, and back to it.
    await user.click(screen.getByRole('link', { name: 'Other' }));
    await act(async () => onlyAttempt().proceed());
    await screen.findByRole('heading', { name: 'Other' });
    await go(-1);

    const asking = nextAttempt();
    await go(-2);

    expect(await act(() => asking)).toMatchObject({ to: '/other', type: 'back' });
    expect(where() + window.location.hash).toBe('/editor#plain');
  });

  it('drops an attempt still waiting when Back goes on once no guard holds', async () => {
    pages(['/editor', '/other']);
    const user = await renderApp(payload('other', '/other'));
    await toEditor(user);
    const firstPush = editorRouter!.push('/other?first');
    const first = onlyAttempt();

    await user.click(screen.getByRole('checkbox', { name: 'Unsaved' }));
    await go(-1);
    expect(screen.getByRole('heading', { name: 'Other' })).toBeDefined();
    await act(async () => {
      first.proceed();
      await firstPush;
    });

    expect(where()).toBe('/other');
  });

  it("holds router.back() as the browser's Back", async () => {
    pages(['/editor', '/other']);
    const user = await renderApp(payload('other', '/other'));
    await toEditor(user);

    const asking = nextAttempt();
    editorRouter!.back();
    expect(await act(() => asking)).toMatchObject({ to: '/other', type: 'back' });
    expect(where()).toBe('/editor');
  });

  it('knows which way Back went after a reload, from the place of each entry it keeps in history.state', async () => {
    pages(['/editor', '/other']);
    const user = await renderApp(payload('other', '/other'));
    await toEditor(user);
    // An entry in the middle of the tab's history: one before it, and one after.
    await user.click(screen.getByRole('checkbox', { name: 'Unsaved' }));
    await user.click(screen.getByRole('link', { name: 'Other' }));
    await screen.findByRole('heading', { name: 'Other' });
    await go(-1);
    const kept = window.history.state;
    cleanup();

    // The browser loads the document again into the same entry, which keeps its state.
    await renderApp(payload('editor', '/editor'), vi.fn(), kept);
    const asking = nextAttempt();
    window.history.back();
    expect(await act(() => asking)).toMatchObject({ to: '/other', type: 'back' });
    expect(where()).toBe('/editor');
  });

  it('holds no Back or Forward over a fragment link, which keeps the page', async () => {
    pages(['/other']);
    const user = await renderApp(payload('editor', '/editor'));
    await user.click(screen.getByRole('link', { name: 'Notes' }));

    await go(-1);
    expect(window.location.hash).toBe('');
    // The browser takes steps in order: a step a guard took to put an entry back would come before this one.
    await go(1);

    expect(window.location.hash).toBe('#notes');
    expect(asked).toEqual([]);
  });

  it('lets Back through to an entry that has no place the router gave it, rather than guess the way back', async () => {
    pages(['/other']);
    // An entry the router did not make, such as one an app or a library pushed itself.
    window.history.replaceState(null, '', `${origin}/other`);
    window.history.pushState(null, '', `${origin}/editor`);
    await renderApp(payload('editor', '/editor'));

    await go(-1);

    await screen.findByRole('heading', { name: 'Other' });
    expect(asked).toEqual([]);
  });

  it('tells the guard where Back goes as the routes see it, without the base path', async () => {
    server.use(http.get(`${origin}/base/editor`, () => HttpResponse.json(payload('editor', '/base/editor', { basePath: '/base' }))));
    const user = await renderApp(payload('other', '/base/other', { basePath: '/base' }));
    await toEditor(user);
    expect(where()).toBe('/base/editor');

    const asking = nextAttempt();
    window.history.back();
    expect(await act(() => asking)).toMatchObject({ to: '/other', type: 'back' });
    expect(where()).toBe('/base/editor');
    (await asking).stay();

    await user.click(screen.getByRole('link', { name: 'Other' }));
    expect(asked.at(-1)?.attempt).toMatchObject({ to: '/other', type: 'push' });
  });
});

/** Whether the document would ask before it unloads (a reload, closing the tab, a full page load): the browser's own question. */
function asksBeforeUnload(): boolean {
  const event = new Event('beforeunload', { cancelable: true });
  window.dispatchEvent(event);
  return event.defaultPrevented;
}

describe('useNavigationGuard and the document unloading', () => {
  it('has the browser ask before the document unloads while it holds, and not once it no longer holds', async () => {
    const user = await renderApp(payload('editor', '/editor'));
    expect(asksBeforeUnload()).toBe(true);

    await user.click(screen.getByRole('checkbox', { name: 'Unsaved' }));
    expect(asksBeforeUnload()).toBe(false);

    await user.click(screen.getByRole('checkbox', { name: 'Unsaved' }));
    expect(asksBeforeUnload()).toBe(true);
    cleanup();
    expect(asksBeforeUnload()).toBe(false);
  });

  it('lets a full page load that a guard let through unload without asking again, and asks after it', async () => {
    // A page from another build of the app: the router loads it in full.
    server.use(http.get(`${origin}/other`, () => HttpResponse.json(payload('other', '/other', { version: 'v2' }))));
    const unloads: boolean[] = [];
    const hardNavigate = vi.fn(() => void unloads.push(asksBeforeUnload()));
    const user = await renderApp(payload('editor', '/editor'), hardNavigate);

    await user.click(screen.getByRole('link', { name: 'Other' }));
    await act(async () => onlyAttempt().proceed());
    await vi.waitFor(() => expect(hardNavigate).toHaveBeenCalledWith(`${origin}/other`));

    expect(unloads).toEqual([false]);
    expect(asksBeforeUnload()).toBe(true);
  });

  it('has the browser ask about a navigation to another site, which no guard holds', async () => {
    const unloads: boolean[] = [];
    const hardNavigate = vi.fn(() => void unloads.push(asksBeforeUnload()));
    await renderApp(payload('editor', '/editor'), hardNavigate);

    await act(() => editorRouter!.push('https://example.com/elsewhere'));

    expect(asked).toEqual([]);
    expect(unloads).toEqual([true]);
  });
});
