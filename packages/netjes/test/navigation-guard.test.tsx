import { act, render, screen } from '@testing-library/react';
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

const Other = () => <h1>Other</h1>;

const modules = {
  '/app/editor/page.tsx': async () => ({ default: Editor }),
  '/app/other/page.tsx': async () => ({ default: Other }),
};

function payload(page: string, url: string): PagePayload {
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

async function renderApp(initial: PagePayload, hardNavigate = vi.fn()) {
  window.history.replaceState(null, '', origin + initial.url);
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

  it('rejects a navigation that carries what the browser cannot copy before any guard is asked', async () => {
    await renderApp(payload('editor', '/editor'));

    const push = editorRouter!.push('/other', { historyState: { run: () => {} } });
    expect(asked).toEqual([]);
    await expect(push).rejects.toThrow();
  });
});
