import type { ReactElement } from 'react';
import { createRoot, hydrateRoot } from 'react-dom/client';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Thenable } from '../src/deferred.ts';
import type { PagePayload } from '../src/payload.ts';

vi.mock('react-dom/client', () => ({ hydrateRoot: vi.fn(), createRoot: vi.fn(() => ({ render: vi.fn() })) }));

type Globals = { __netjes_v?: unknown; __netjes_d?: unknown };
const globals = self as unknown as Globals;

/** A document as the server sends it: the payload first, then the page (here: just `pageHtml`). */
function serverDocument(pageHtml: string, props: Record<string, unknown> = {}, documentInfo: Record<string, unknown> = {}, page: string | null = null) {
  const payload = {
    kind: 'page',
    route: null,
    page,
    segments: [],
    props,
    params: {},
    shared: null,
    status: 404,
    url: '/missing',
    version: 'v1',
    document: { lang: 'en', assets: { stylesheets: [], modulePreloads: [], scripts: [] }, errorPage: null, ...documentInfo },
  };
  document.body.innerHTML = `<script id="__NETJES_DATA__" type="application/json">${JSON.stringify(payload)}</script>${pageHtml}`;
}

/** A window whose `localStorage`, an own property of every window, throws when read. */
function withStorageBlocked(frame: Window | null | undefined) {
  if (!frame) throw new Error('the frame has no window');
  Object.defineProperty(frame, 'localStorage', {
    enumerable: true,
    configurable: true,
    get: () => {
      throw new DOMException('Access is denied for this document.', 'SecurityError');
    },
  });
  return frame;
}

/** The payload the client entry hydrated with. */
function hydratedPayload(): PagePayload {
  const root = vi.mocked(hydrateRoot).mock.calls[0]?.[1] as ReactElement<{ payload: PagePayload }>;
  return root.props.payload;
}

describe('the client entry', () => {
  beforeEach(() => {
    vi.resetModules();
    vi.mocked(hydrateRoot).mockClear();
    vi.mocked(createRoot).mockClear();
  });
  afterEach(() => {
    delete globals.__netjes_v;
    delete globals.__netjes_d;
    document.body.innerHTML = '';
    vi.doUnmock('virtual:netjes/pages');
    vi.restoreAllMocks();
  });

  /** The page module's import fails `failures` times (a network error, a deploy that removed its chunk), then loads. */
  function pageModuleFailing(failures: number) {
    let calls = 0;
    vi.doMock('virtual:netjes/pages', () => ({
      pages: {
        '/app/about/page.tsx': async () => {
          calls++;
          if (calls <= failures) throw new TypeError('Failed to fetch dynamically imported module: /assets/page-abc.js');
          return { default: () => null };
        },
      },
    }));
    return () => calls;
  }

  it('tries a page module that fails to load once more, and hydrates', async () => {
    const calls = pageModuleFailing(1);
    const errors = vi.spyOn(console, 'error').mockImplementation(() => {});
    serverDocument('<main>About</main>', {}, {}, 'app/about/page.tsx');

    await import('../src/client-entry.tsx');

    expect(calls()).toBe(2);
    expect(hydrateRoot).toHaveBeenCalledOnce();
    expect(errors).not.toHaveBeenCalled();
  });

  it('leaves the page as the server sent it, and says why, when a page module fails to load twice', async () => {
    const calls = pageModuleFailing(2);
    const errors = vi.spyOn(console, 'error').mockImplementation(() => {});
    serverDocument('<main><a href="/blog">Blog</a></main>', {}, {}, 'app/about/page.tsx');

    await import('../src/client-entry.tsx');

    expect(calls()).toBe(2);
    expect(hydrateRoot).not.toHaveBeenCalled();
    expect(createRoot).not.toHaveBeenCalled();
    // The server's HTML stays, with plain links and forms that the browser loads in full.
    expect(document.querySelector('main')?.outerHTML).toBe('<main><a href="/blog">Blog</a></main>');
    expect(errors).toHaveBeenCalledOnce();
    expect(String(errors.mock.calls[0]?.[0])).toContain('app/about/page.tsx');
  });

  it.each([
    // Two elements sharing the id: an HTMLCollection.
    ['<a id="__netjes_v">a</a><a id="__netjes_v">b</a>', () => document.getElementsByTagName('a')],
    // A form: the form, whose controls are its properties.
    ['<form id="__netjes_v"><input name="d0"></form>', () => document.forms[0]],
    // A frame: its window, as a browser that blocks site data gives it, where reading `localStorage` throws.
    ['<iframe name="__netjes_v"></iframe>', () => withStorageBlocked(document.querySelector('iframe')?.contentWindow)],
  ])('hydrates when the markup %s stands in for the deferred values (DOM clobbering)', async (markup, namedAccess) => {
    // Untrusted, sanitizer-approved HTML in the page.
    serverDocument(markup);
    // What window named access gives for that markup.
    globals.__netjes_v = namedAccess();

    await import('../src/client-entry.tsx');

    expect(hydrateRoot).toHaveBeenCalledOnce();
  });

  it('hydrates a document the sidecar rendered', async () => {
    serverDocument('<main>the page</main>');

    await import('../src/client-entry.tsx');

    expect(hydrateRoot).toHaveBeenCalledOnce();
    expect(vi.mocked(hydrateRoot).mock.calls[0]?.[0]).toBe(document);
    expect(createRoot).not.toHaveBeenCalled();
  });

  it('renders, rather than hydrates, a document the server sent without the page because no sidecar was ready', async () => {
    serverDocument('<!-- No Node sidecar was ready: this page renders in the browser. -->', {}, { clientRender: true });

    await import('../src/client-entry.tsx');

    expect(hydrateRoot).not.toHaveBeenCalled();
    expect(createRoot).toHaveBeenCalledExactlyOnceWith(document);
    const root = vi.mocked(createRoot).mock.results[0]?.value as { render: ReturnType<typeof vi.fn> };
    const element = root.render.mock.calls[0]?.[0] as ReactElement<{ payload: PagePayload & { document: { clientRender?: boolean } } }>;
    expect(element.props.payload.document.clientRender).toBe(true);
  });

  it('hands the page the values the bootstrap collected before it ran', async () => {
    serverDocument('', { a: { $netjes: 'deferred', id: 'd0' }, b: { $netjes: 'deferred', id: 'd1' } });
    globals.__netjes_v = { d0: [42, undefined], d1: [null, 'down'] };

    await import('../src/client-entry.tsx');

    const { a, b } = hydratedPayload().props as Record<string, Thenable<unknown>>;
    expect(a?.status).toBe('fulfilled');
    expect(a?.value).toBe(42);
    expect(b?.status).toBe('rejected');
    expect((b?.reason as Error).message).toBe('down');
  });

  it('skips collected entries that are not a [value, error] pair', async () => {
    serverDocument('', { a: { $netjes: 'deferred', id: 'd0' }, b: { $netjes: 'deferred', id: 'd1' }, c: { $netjes: 'deferred', id: 'd2' } });
    globals.__netjes_v = { d0: 'x', d1: null, d2: [7, undefined] };

    await import('../src/client-entry.tsx');

    const { a, b, c } = hydratedPayload().props as Record<string, Thenable<unknown>>;
    expect(a?.status).toBe('pending');
    expect(b?.status).toBe('pending');
    expect(c?.value).toBe(7);
  });
});
