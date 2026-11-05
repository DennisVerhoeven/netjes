import { useLayoutEffect } from 'react';
import { type Root, hydrateRoot } from 'react-dom/client';
import { renderToString } from 'react-dom/server';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ActionClient } from '../src/actions.tsx';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload } from '../src/payload.ts';
import { RouterProvider } from '../src/router.tsx';

// The router puts a mark on <html> once it runs in the document. These tests hydrate a page the way the browser does:
// outside act(), on React's own scheduler, which commits the page in one task and runs its effects (useEffect) in a
// later one (before-ready.test.tsx has the clicks that come in between).

const MARK = vi.hoisted(() => 'data-netjes-hydrated');

/** Whether `<html>` had the mark each time a router registered as the action client. */
const markedAtRegistration = vi.hoisted((): boolean[] => []);

vi.mock('../src/actions.tsx', async (importOriginal) => {
  const actions = await importOriginal<typeof import('../src/actions.tsx')>();
  return {
    ...actions,
    setActionClient(next: ActionClient | null) {
      if (next) markedAtRegistration.push(document.documentElement.hasAttribute(MARK));
      return actions.setActionClient(next);
    },
  };
});

/** Runs in the commit that hydrates the page, before React runs any effect. */
let onCommit: () => void = () => {};

function Page() {
  useLayoutEffect(() => onCommit(), []);
  return <h1>Hello</h1>;
}

const modules = { '/app/page.tsx': async () => ({ default: Page }) };

const initial: PagePayload = { kind: 'page', route: '/', page: 'app/page.tsx', segments: [], props: {}, params: {}, shared: null, status: 200, url: '/', version: 'v1' };

let root: Root | undefined;
let container: HTMLElement | undefined;
let actEnvironment: unknown;
const globals = globalThis as { IS_REACT_ACT_ENVIRONMENT?: unknown };

beforeEach(() => {
  actEnvironment = globals.IS_REACT_ACT_ENVIRONMENT;
  globals.IS_REACT_ACT_ENVIRONMENT = false;
  markedAtRegistration.length = 0;
});

afterEach(() => {
  root?.unmount();
  container?.remove();
  root = container = undefined;
  onCommit = () => {};
  document.documentElement.removeAttribute(MARK);
  globals.IS_REACT_ACT_ENVIRONMENT = actEnvironment;
});

/** Renders the page to HTML as the server would (with no document), then hydrates it, outside act(). */
async function hydrate() {
  const loader = createModuleLoader(modules);
  const initialModules = await loader.loadRoute(initial);
  const app = <RouterProvider initial={initial} initialModules={initialModules} loader={loader} errorPage={null} />;
  const browserDocument = globalThis.document;
  vi.stubGlobal('document', undefined);
  let html: string;
  try {
    html = renderToString(app);
  } finally {
    vi.stubGlobal('document', browserDocument);
    vi.unstubAllGlobals();
  }
  container = document.createElement('div');
  container.innerHTML = html;
  document.body.append(container);
  window.history.replaceState(null, '', initial.url);
  root = hydrateRoot(container, app);
}

const marked = () => document.documentElement.getAttribute(MARK);

describe('the hydrated mark on <html>', () => {
  it("does not mark <html> in the commit that hydrates the page, only once the router's effects have run", async () => {
    // A microtask queued in the commit runs before React's next task, where the effects run.
    const inTheCommit: boolean[] = [];
    onCommit = () => queueMicrotask(() => inTheCommit.push(document.documentElement.hasAttribute(MARK)));

    await hydrate();

    await vi.waitFor(() => expect(inTheCommit).toHaveLength(1));
    expect(inTheCommit).toEqual([false]);
    await vi.waitFor(() => expect(marked()).toBe(''));
  });

  it('sets the mark only after the router has registered as the action client', async () => {
    await hydrate();

    await vi.waitFor(() => expect(marked()).toBe(''));
    expect(markedAtRegistration).toEqual([false]);
  });

  it('takes the mark off when the router unmounts', async () => {
    await hydrate();
    await vi.waitFor(() => expect(marked()).toBe(''));

    root?.unmount();
    root = undefined;

    expect(marked()).toBeNull();
  });
});
