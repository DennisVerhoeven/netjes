// Its own file, so that zod loads fresh here: zod tests for `Function` once per copy, the first time it makes an object
// schema, and the setting this checks stays on the global object for every test after it.
import { hydrateRoot } from 'react-dom/client';
import { afterEach, expect, it, vi } from 'vitest';

vi.mock('react-dom/client', () => ({ hydrateRoot: vi.fn(), createRoot: vi.fn(() => ({ render: vi.fn() })) }));

/** A document as the server sends it for `page`: the payload first, then the page. */
function serverDocument(page: string) {
  const payload = {
    kind: 'page',
    route: '/about',
    page,
    segments: [],
    props: {},
    params: {},
    shared: null,
    status: 200,
    url: '/about',
    version: 'v1',
    document: { lang: 'en', assets: { stylesheets: [], modulePreloads: [], scripts: [] }, errorPage: null },
  };
  document.body.innerHTML = `<script id="__NETJES_DATA__" type="application/json">${JSON.stringify(payload)}</script><main>About</main>`;
}

afterEach(() => {
  document.body.innerHTML = '';
  vi.doUnmock('virtual:netjes/pages');
  vi.doUnmock('@vitejs/plugin-react/preamble');
  vi.restoreAllMocks();
});

it("turns zod's compiled parsers off ahead of its other imports, so no page's schema makes a function from a string", async () => {
  // zod's setting when the entry's next import runs, and when the page's module runs.
  const jitless: Record<string, unknown> = {};
  vi.doMock('@vitejs/plugin-react/preamble', async () => {
    const { z } = await import('zod');
    jitless.preamble = z.config().jitless;
    return {};
  });
  vi.doMock('virtual:netjes/pages', () => ({
    pages: {
      // As a page module that imports the app's generated schemas does: it makes an object schema, and parses props.
      '/app/about/page.tsx': async () => {
        const { z } = await import('zod');
        jitless.page = z.config().jitless;
        z.object({ title: z.string() }).parse({ title: 'About' });
        return { default: () => null };
      },
    },
  }));
  // Without the setting, zod makes a function from a string to test whether it may, and again to compile an object
  // schema's parser; under a Content-Security-Policy without 'unsafe-eval', a browser reports the test as a violation.
  const callers: string[] = [];
  const Original = globalThis.Function;
  vi.spyOn(globalThis, 'Function').mockImplementation(function (...args: string[]) {
    callers.push(new Error().stack ?? '');
    return new Original(...args);
  } as unknown as FunctionConstructor);
  serverDocument('app/about/page.tsx');

  await import('../src/client-entry.tsx');

  expect(hydrateRoot).toHaveBeenCalledOnce();
  expect(jitless).toEqual({ preamble: true, page: true });
  expect(callers.filter((stack) => /node_modules[\\/]zod[\\/]/.test(stack))).toEqual([]);
});
