import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { type ReactNode, useEffect } from 'react';
import { type Root, hydrateRoot } from 'react-dom/client';
import { renderToString } from 'react-dom/server';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { Link } from '../src/link.tsx';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload } from '../src/payload.ts';
import { RouterProvider, useHistoryState, useRouter } from '../src/router.tsx';

const origin = window.location.origin;
const server = setupServer();
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

/** A counter kept with the history entry: what a list keeps of its ticked rows. */
function Ticks() {
  const [ticks, setTicks] = useHistoryState('ticks', 0);
  return (
    <button type="button" onClick={() => setTicks((n) => n + 1)}>
      ticks {ticks}
    </button>
  );
}

/** Sets a value as it unmounts, when the entry on screen is already the next page's. */
function SetsAsItLeaves() {
  const [, setNote] = useHistoryState('note', 'none');
  useEffect(() => () => setNote('left'), [setNote]);
  return null;
}

function List(props: Record<string, unknown>) {
  const router = useRouter();
  return (
    <>
      <h1>List</h1>
      <p>answer {String(props.answer ?? 0)}</p>
      <Ticks />
      <SetsAsItLeaves />
      <Link href="/record">Record</Link>
      <Link href="/list?tab=2">Second tab</Link>
      <Link href="#bottom">Bottom</Link>
      <button type="button" onClick={() => void router.refresh()}>
        Refresh
      </button>
      <button type="button" onClick={() => void router.replace('/record')}>
        Record in place
      </button>
    </>
  );
}

function Record() {
  const [note] = useHistoryState('note', 'none');
  return (
    <>
      <h1>Record</h1>
      <Ticks />
      <p>note {note}</p>
    </>
  );
}

const modules = {
  '/app/list/page.tsx': async () => ({ default: List }),
  '/app/record/page.tsx': async () => ({ default: Record }),
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

/** Serves the pages at `paths`, each with the path and query it was asked for, and records the requests. */
function pages(paths: string[], requests: string[] = []) {
  server.use(
    ...paths.map((path) =>
      http.get(`${origin}${path}`, ({ request }) => {
        const url = new URL(request.url);
        requests.push(url.pathname + url.search);
        return HttpResponse.json(payload(path.slice(1), url.pathname + url.search, { props: { answer: requests.length } }));
      }),
    ),
  );
  return requests;
}

async function renderApp(initial: PagePayload) {
  window.history.replaceState(null, '', origin + initial.url);
  const loader = createModuleLoader(modules);
  const initialModules = await loader.loadRoute(initial);
  render(<RouterProvider initial={initial} initialModules={initialModules} loader={loader} errorPage={null} />);
  return userEvent.setup();
}

describe('useHistoryState', () => {
  it('keeps a value with its history entry through Back and Forward, and starts a new entry from the initial value', async () => {
    pages(['/record']);
    const user = await renderApp(payload('list', '/list'));
    await user.click(screen.getByRole('button', { name: 'ticks 0' }));
    await user.click(screen.getByRole('button', { name: 'ticks 1' }));

    await user.click(screen.getByRole('link', { name: 'Record' }));
    await screen.findByRole('heading', { name: 'Record' });
    expect(screen.getByRole('button', { name: 'ticks 0' })).toBeDefined();
    await user.click(screen.getByRole('button', { name: 'ticks 0' }));

    act(() => window.history.back());
    await screen.findByRole('heading', { name: 'List' });
    expect(screen.getByRole('button', { name: 'ticks 2' })).toBeDefined();

    act(() => window.history.forward());
    await screen.findByRole('heading', { name: 'Record' });
    expect(screen.getByRole('button', { name: 'ticks 1' })).toBeDefined();
  });

  it('brings the value back after a reload, rendering the initial value on the server and while hydrating', async () => {
    const user = await renderApp(payload('list', '/list'));
    await user.click(screen.getByRole('button', { name: 'ticks 0' }));
    await user.click(screen.getByRole('button', { name: 'ticks 1' }));
    cleanup();

    // The browser loads the document again into the same entry, which keeps its state.
    const initial = payload('list', '/list');
    const loader = createModuleLoader(modules);
    const app = <RouterProvider initial={initial} initialModules={await loader.loadRoute(initial)} loader={loader} errorPage={null} />;
    const html = renderOnServer(app);
    expect(html).toContain('ticks <!-- -->0');
    const container = document.createElement('div');
    container.innerHTML = html;
    document.body.append(container);
    const mismatches: unknown[] = [];
    let root: Root | undefined;
    await act(async () => {
      root = hydrateRoot(container, app, { onRecoverableError: (error) => mismatches.push(error) });
    });

    expect(within(container).getByRole('button', { name: 'ticks 2' })).toBeDefined();
    expect(mismatches).toEqual([]);
    act(() => root?.unmount());
    container.remove();
  });

  it('keeps the values while the page stays on screen (another query, a fragment, a refresh), and starts another page over', async () => {
    const requests = pages(['/list', '/record']);
    const user = await renderApp(payload('list', '/list'));
    await user.click(screen.getByRole('button', { name: 'ticks 0' }));

    await user.click(screen.getByRole('link', { name: 'Second tab' }));
    await waitFor(() => expect(window.location.search).toBe('?tab=2'));
    expect(screen.getByRole('button', { name: 'ticks 1' })).toBeDefined();
    await user.click(screen.getByRole('link', { name: 'Bottom' }));
    expect(window.location.hash).toBe('#bottom');
    expect(screen.getByRole('button', { name: 'ticks 1' })).toBeDefined();
    const answers = requests.length;
    await user.click(screen.getByRole('button', { name: 'Refresh' }));
    await screen.findByText(`answer ${answers + 1}`);
    expect(screen.getByRole('button', { name: 'ticks 1' })).toBeDefined();

    // Each entry keeps the value it had when it was left.
    await user.click(screen.getByRole('button', { name: 'ticks 1' }));
    act(() => window.history.back());
    await waitFor(() => expect(window.location.hash).toBe(''));
    expect(screen.getByRole('button', { name: 'ticks 1' })).toBeDefined();
    act(() => window.history.forward());
    await waitFor(() => expect(window.location.hash).toBe('#bottom'));
    expect(screen.getByRole('button', { name: 'ticks 2' })).toBeDefined();

    await user.click(screen.getByRole('button', { name: 'Record in place' }));
    await screen.findByRole('heading', { name: 'Record' });
    expect(screen.getByRole('button', { name: 'ticks 0' })).toBeDefined();
  });

  it('writes nothing into the next entry from a page that sets a value as it unmounts', async () => {
    pages(['/record']);
    const user = await renderApp(payload('list', '/list'));

    await user.click(screen.getByRole('link', { name: 'Record' }));

    await screen.findByRole('heading', { name: 'Record' });
    expect(screen.getByText('note none')).toBeDefined();
  });

  it('writes nothing from the page on screen into the entry that Back went to while that page loads', async () => {
    let release!: () => void;
    const held = new Promise<void>((resolve) => (release = resolve));
    const requests: string[] = [];
    server.use(
      http.get(`${origin}/list`, async () => {
        requests.push('/list');
        await held;
        return HttpResponse.json(payload('list', '/list'));
      }),
    );
    // After a reload on the record: the router remembers no page for the entry before.
    window.history.replaceState(null, '', `${origin}/list`);
    window.history.pushState(null, '', `${origin}/record`);
    const loader = createModuleLoader(modules);
    const initial = payload('record', '/record');
    render(<RouterProvider initial={initial} initialModules={await loader.loadRoute(initial)} loader={loader} errorPage={null} />);
    const user = userEvent.setup();

    act(() => window.history.back());
    await waitFor(() => expect(requests).toHaveLength(1));
    await user.click(screen.getByRole('button', { name: 'ticks 0' }));
    release();
    await screen.findByRole('heading', { name: 'List' });
    act(() => window.history.forward());
    await screen.findByRole('heading', { name: 'Record' });
    act(() => window.history.back());
    await screen.findByRole('heading', { name: 'List' });

    expect(screen.getByRole('button', { name: 'ticks 0' })).toBeDefined();
  });
});

/** Renders as the server does, where there is no document. */
function renderOnServer(app: ReactNode): string {
  const browserDocument = globalThis.document;
  vi.stubGlobal('document', undefined);
  try {
    return renderToString(app);
  } finally {
    vi.stubGlobal('document', browserDocument);
    vi.unstubAllGlobals();
  }
}
