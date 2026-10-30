import { screen } from '@testing-library/react';
import { Suspense, use } from 'react';
import { afterEach, expect, it, vi } from 'vitest';
import { DEFERRED_BOOTSTRAP } from '../src/deferred.ts';

function Report({ report }: { report: PromiseLike<string> }) {
  return <p role="status">{use(report)}</p>;
}

function Page({ title, early, late }: { title: string; early: PromiseLike<string>; late: PromiseLike<string> }) {
  return (
    <main>
      <h1>{title}</h1>
      <Suspense fallback={<p>loading early</p>}>
        <Report report={early} />
      </Suspense>
      <Suspense fallback={<p>loading late</p>}>
        <Report report={late} />
      </Suspense>
    </main>
  );
}

vi.mock('virtual:netjes/pages', () => ({ pages: { '/app/page.tsx': async () => ({ default: Page }) } }));

type Globals = { __netjes_v?: unknown; __netjes_d?: (id: string, value: unknown, error?: string) => void };
const globals = self as unknown as Globals;

afterEach(() => {
  delete globals.__netjes_v;
  delete globals.__netjes_d;
});

/**
 * The document .NET writes when no sidecar is ready (ClientRenderShell.cs): the head with the bootstrap and the
 * client entry, the title from C#, and a body with only the payload.
 */
function clientRenderShell(payload: unknown) {
  document.documentElement.lang = 'en';
  document.head.innerHTML =
    `<meta charset="utf-8"><script>${DEFERRED_BOOTSTRAP}</script><title>From C#</title>` +
    '<link rel="stylesheet" href="/assets/entry.css"><script type="module" src="/assets/entry.js" async></script>';
  document.body.innerHTML =
    `<script id="__NETJES_DATA__" type="application/json">${JSON.stringify(payload)}</script>` +
    '<!-- No Node sidecar was ready: this page renders in the browser. -->';
}

it('renders the page of a client-render shell in the browser, with no errors, and its deferred values arrive', async () => {
  clientRenderShell({
    kind: 'page',
    route: '/',
    page: 'app/page.tsx',
    segments: [],
    props: { title: 'Hello', early: { $netjes: 'deferred', id: 'd0' }, late: { $netjes: 'deferred', id: 'd1' } },
    params: {},
    shared: null,
    status: 200,
    url: '/',
    version: 'v1',
    metadata: { title: 'Hello' },
    document: { lang: 'en', assets: { stylesheets: ['/assets/entry.css'], modulePreloads: [], scripts: ['/assets/entry.js'] }, errorPage: null, clientRender: true },
  });
  // A value that streamed in before the client entry ran, as the bootstrap collects it.
  globals.__netjes_v = { d0: ['first', undefined] };
  const errors = vi.spyOn(console, 'error');
  const reported: unknown[] = [];
  const onError = (event: ErrorEvent) => reported.push(event.error ?? event.message);
  window.addEventListener('error', onError);

  try {
    await import('../src/client-entry.tsx');

    expect(await screen.findByRole('heading', { name: 'Hello' })).toBeTruthy();
    expect(await screen.findByText('first')).toBeTruthy();
    expect(screen.getByText('loading late')).toBeTruthy();
    // A value that streams in later, as the shell's scripts hand it over.
    globals.__netjes_d?.('d1', 'second');
    expect(await screen.findByText('second')).toBeTruthy();
    // React's own title replaced the one .NET wrote.
    expect(document.title).toBe('Hello');
    expect(document.head.querySelectorAll('title')).toHaveLength(1);
    expect(errors).not.toHaveBeenCalled();
    expect(reported).toEqual([]);
  } finally {
    window.removeEventListener('error', onError);
    errors.mockRestore();
  }
});
