import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { useEffect, useLayoutEffect, useState } from 'react';
import { type Root, hydrateRoot } from 'react-dom/client';
import { renderToString } from 'react-dom/server';
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { z } from 'zod';
import { expectActionClient, serverAction, useAction } from '../src/actions.tsx';
import { Form } from '../src/form.tsx';
import { Link } from '../src/link.tsx';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload } from '../src/payload.ts';
import { RouterProvider, useRouter } from '../src/router.tsx';

// A page hydrated the way the browser hydrates it: outside act(), on React's own scheduler. React renders and commits
// the hydrated page in one task and runs its effects (useEffect) in a later one, yielding to the browser in between;
// a click that came in while React was rendering is dispatched in that gap.

const origin = window.location.origin;
const server = setupServer();
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));
afterAll(() => server.close());

const sign = serverAction<FormData, { id: number }>('guestbook.sign', { result: z.object({ id: z.number() }) });

/** Runs in the commit that hydrates the page, before React runs any effect (once: not when the page mounts again). */
let onHydrated: () => void = () => {};

function useHydrated() {
  useLayoutEffect(() => {
    const hydrated = onHydrated;
    onHydrated = () => {};
    hydrated();
  }, []);
}

function Guestbook() {
  const [state, formAction] = useAction(sign);
  useHydrated();
  return (
    <main>
      <Form action={formAction}>
        <input name="name" aria-label="Name" defaultValue="Ada" />
        <button type="submit">Sign</button>
      </Form>
      <output>{state?.ok ? `signed ${state.result.id}` : 'idle'}</output>
    </main>
  );
}

const greet = serverAction<{ name: string }, string>('greet');

/** Asks the server as it mounts: React runs a page's effects before its router's. */
function Welcome() {
  const [greeting, setGreeting] = useState('…');
  useEffect(() => {
    greet({ name: 'Ada' }).then(
      (state) => setGreeting(state.ok ? state.result : 'rejected'),
      (error: unknown) => setGreeting(String(error)),
    );
  }, []);
  return <p>{greeting}</p>;
}

/** The router's other ways in: a link, its methods and a page action's form. */
function Menu(props: Record<string, unknown>) {
  const router = useRouter();
  useHydrated();
  return (
    <main>
      <h1>{`Menu${props.saved ? ' saved' : ''}`}</h1>
      <Link href="/done">Link</Link>
      <Link href="#comments">Comments</Link>
      <button type="button" onClick={() => void router.push('/done')}>
        Push
      </button>
      <button type="button" onClick={() => void router.refresh()}>
        Refresh
      </button>
      <Form method="post">
        <button type="submit">Save</button>
      </Form>
    </main>
  );
}

const modules = {
  '/app/guestbook/page.tsx': async () => ({ default: Guestbook }),
  '/app/welcome/page.tsx': async () => ({ default: Welcome }),
  '/app/menu/page.tsx': async () => ({ default: Menu }),
  '/app/done/page.tsx': async () => ({ default: () => <h1>Done</h1> }),
};

const antiforgery = { formFieldName: '__RequestVerificationToken', headerName: 'RequestVerificationToken', requestToken: 'tok' };

function payload(page: string, url: string): PagePayload {
  return { kind: 'page', route: url, page, segments: [], props: {}, params: {}, shared: null, status: 200, url, version: 'v1', antiforgery };
}

let root: Root | undefined;
let container: HTMLElement | undefined;
let actEnvironment: unknown;
const globals = globalThis as { IS_REACT_ACT_ENVIRONMENT?: unknown };

beforeEach(() => {
  actEnvironment = globals.IS_REACT_ACT_ENVIRONMENT;
  globals.IS_REACT_ACT_ENVIRONMENT = false;
});

afterEach(() => {
  root?.unmount();
  container?.remove();
  root = container = undefined;
  onHydrated = () => {};
  vi.useRealTimers();
  vi.restoreAllMocks();
  globals.IS_REACT_ACT_ENVIRONMENT = actEnvironment;
  server.resetHandlers();
});

/**
 * A click on the link or button `name` that came in while React hydrated the page, so the browser dispatches it in
 * the next task: after the commit, before the effects (React's scheduler queues those behind it, on setImmediate in
 * Node).
 */
const clickInNextTask = (name: string) => () =>
  setImmediate(() => {
    const target = [...(container?.querySelectorAll('a, button') ?? [])].find((element) => element.textContent === name);
    target?.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
  });

/** Renders the page to HTML as the server would (with no document), then hydrates it, outside act(). */
async function hydrate(initial: PagePayload): Promise<HTMLElement> {
  const loader = createModuleLoader(modules);
  const initialModules = await loader.loadRoute(initial);
  const app = <RouterProvider initial={initial} initialModules={initialModules} loader={loader} errorPage={null} />;
  const window_ = globalThis.document;
  vi.stubGlobal('document', undefined);
  let html: string;
  try {
    html = renderToString(app);
  } finally {
    vi.stubGlobal('document', window_);
    vi.unstubAllGlobals();
  }
  const element = document.createElement('div');
  element.innerHTML = html;
  document.body.append(element);
  window.history.replaceState(null, '', initial.url);
  container = element;
  root = hydrateRoot(element, app);
  return element;
}

describe('a page used as soon as it is hydrated', () => {
  it('runs a server action submitted before React has run the effects', async () => {
    server.use(http.post(`${origin}/_netjes/actions/guestbook.sign`, () => HttpResponse.json({ kind: 'action', ok: true, status: 200, result: { id: 7 }, revalidate: false })));
    onHydrated = clickInNextTask('Sign');

    const page = await hydrate(payload('app/guestbook/page.tsx', '/guestbook'));

    await vi.waitFor(() => expect(page.textContent).toContain('signed 7'));
  });

  it('runs a server action the page calls from its own effect as it mounts', async () => {
    server.use(http.post(`${origin}/_netjes/actions/greet`, () => HttpResponse.json({ kind: 'action', ok: true, status: 200, result: 'Hello Ada', revalidate: false })));

    const page = await hydrate(payload('app/welcome/page.tsx', '/welcome'));

    await vi.waitFor(() => expect(page.textContent).toBe('Hello Ada'));
  });
});

describe('the router used as soon as the page is hydrated', () => {
  for (const name of ['Link', 'Push']) {
    it(`navigates through the router (${name})`, async () => {
      let loaded = 0;
      server.use(
        http.get(`${origin}/done`, () => {
          loaded++;
          return HttpResponse.json(payload('app/done/page.tsx', '/done'));
        }),
      );
      onHydrated = clickInNextTask(name);

      const page = await hydrate(payload('app/menu/page.tsx', '/menu'));

      await vi.waitFor(() => expect(page.textContent).toBe('Done'));
      expect(loaded).toBe(1);
      expect(window.location.pathname).toBe('/done');
      window.history.back();
      await vi.waitFor(() => expect(page.textContent).toContain('Menu'));
    });
  }

  it('refreshes the page through the router', async () => {
    server.use(http.get(`${origin}/menu`, () => HttpResponse.json({ ...payload('app/menu/page.tsx', '/menu'), props: { saved: true } })));
    onHydrated = clickInNextTask('Refresh');

    const page = await hydrate(payload('app/menu/page.tsx', '/menu'));

    await vi.waitFor(() => expect(page.textContent).toContain('Menu saved'));
  });

  it("posts a page action's form through the router", async () => {
    server.use(http.post(`${origin}/menu`, () => HttpResponse.json({ ...payload('app/menu/page.tsx', '/menu'), props: { saved: true } })));
    onHydrated = clickInNextTask('Save');

    const page = await hydrate(payload('app/menu/page.tsx', '/menu'));

    await vi.waitFor(() => expect(page.textContent).toContain('Menu saved'));
  });

  it('restores the scroll position on Back from a fragment link followed then', async () => {
    vi.spyOn(window, 'scrollY', 'get').mockReturnValue(640);
    onHydrated = clickInNextTask('Comments');

    await hydrate(payload('app/menu/page.tsx', '/menu'));
    await vi.waitFor(() => expect(window.location.hash).toBe('#comments'));
    vi.mocked(window.scrollTo).mockClear();
    window.history.back();

    await vi.waitFor(() => expect(window.scrollTo).toHaveBeenCalled());
    expect(window.location.hash).toBe('');
    expect(window.scrollTo).toHaveBeenLastCalledWith(0, 640);
  });
});

describe('a server action called without a router', () => {
  const notReady = 'netjes: server action greet was called before the page was ready';

  it('fails at once when no router is on its way, as in a test without one', async () => {
    await expect(greet({ name: 'Ada' })).rejects.toThrow(notReady);
  });

  it('fails after ten seconds when the router on its way never registers', async () => {
    vi.useFakeTimers();
    // A router rendered, and React threw that render away.
    expectActionClient();
    let outcome: string | undefined;
    greet({ name: 'Ada' }).then(
      () => (outcome = 'answered'),
      (error: unknown) => (outcome = error instanceof Error ? error.message : String(error)),
    );

    await vi.advanceTimersByTimeAsync(9_999);
    expect(outcome).toBeUndefined();
    await vi.advanceTimersByTimeAsync(1);
    expect(outcome).toBe(notReady);
    // Given up on: later calls fail at once.
    await expect(greet({ name: 'Ada' })).rejects.toThrow(notReady);
  });
});
