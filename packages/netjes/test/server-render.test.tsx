// @vitest-environment node
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { useQuery } from '@tanstack/react-query';
import { type ReactNode, Suspense, use } from 'react';
import { renderToString } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { createModuleLoader } from '../src/modules.ts';
import { Image } from '../src/image.tsx';
import { useNonce } from '../src/nonce.tsx';
import { usePublicConfig } from '../src/public-config.tsx';
import { createQueryShell } from '../src/query.tsx';
import { RouterProvider } from '../src/router.tsx';
import { z } from 'zod';
import { usePathname, useSharedProps } from '../src/router.tsx';
import { createServerRenderer } from '../src/server-render.tsx';

const sharedSchema = z.object({ siteName: z.string() });

function Layout({ children }: { children: ReactNode }) {
  const shared = useSharedProps(sharedSchema);
  return (
    <div className="shell">
      <header>{shared.siteName}</header>
      {children}
    </div>
  );
}

function Post(props: Record<string, unknown>) {
  return (
    <article>
      <title>{`Post: ${String(props.title)}`}</title>
      <meta name="description" content="A post" />
      <h1>{String(props.title)}</h1>
    </article>
  );
}

function Likes() {
  const likes = useQuery({ queryKey: ['likes', 'hello'], queryFn: (): Promise<number> => new Promise(() => {}) });
  return <p>{likes.data === undefined ? 'loading' : `${likes.data} likes`}</p>;
}

function OwnGcTime() {
  const likes = useQuery({ queryKey: ['likes', 'own'], queryFn: (): Promise<number> => new Promise(() => {}), gcTime: 60_000 });
  return <p>{likes.data === undefined ? 'loading' : `${likes.data} likes`}</p>;
}

function Broken(): ReactNode {
  throw new Error('window is not defined');
}

const renderer = createServerRenderer({
  '/app/layout.tsx': async () => ({ default: Layout }),
  '/app/blog/[slug]/page.tsx': async () => ({ default: Post }),
  '/app/broken/page.tsx': async () => ({ default: Broken }),
  '/app/likes/page.tsx': async () => ({ default: Likes }),
  '/app/own-gc/page.tsx': async () => ({ default: OwnGcTime }),
  '/app/hero/page.tsx': async () => ({ default: () => <Image src="/hero.png" width={300} height={100} alt="Hero" /> }),
  '/app/nonce/page.tsx': async () => ({ default: () => <p>{`nonce ${useNonce()}`}</p> }),
  '/app/config/page.tsx': async () => ({ default: () => <p>{`api at ${usePublicConfig(z.object({ apiUrl: z.url() })).apiUrl}`}</p> }),
  '/app/pathname/page.tsx': async () => ({ default: () => <p>{`at ${usePathname()}`}</p> }),
});

function data(overrides: Record<string, unknown> = {}, document: Record<string, unknown> = {}) {
  return JSON.stringify({
    kind: 'page',
    route: '/blog/[slug]',
    page: 'app/blog/[slug]/page.tsx',
    segments: [{ id: 'app', layout: 'app/layout.tsx' }],
    props: { title: 'Hello </script>' },
    params: { slug: 'hello' },
    shared: { siteName: 'Demo' },
    status: 200,
    url: '/blog/hello',
    version: 'v1',
    ...overrides,
    document: {
      lang: 'nl',
      assets: { stylesheets: ['/assets/app.css'], modulePreloads: ['/assets/react.js'], scripts: ['/assets/entry.js'] },
      errorPage: null,
      ...document,
    },
  }).replace(/</g, '\\u003c');
}

describe('server rendering', () => {
  it('renders a complete document with the page inside its layouts', async () => {
    const html = await renderer.render(data());

    expect(html.startsWith('<!DOCTYPE html>')).toBe(true);
    expect(html).toContain('<html lang="nl">');
    expect(html).toMatch(/<header>Demo<\/header>.*<h1>Hello &lt;\/script&gt;<\/h1>/s);
  });

  it('hoists the title and meta tags a page renders into the head', async () => {
    const html = await renderer.render(data());

    const head = html.slice(html.indexOf('<head>'), html.indexOf('</head>'));
    expect(head).toContain('<title>Post: Hello &lt;/script&gt;</title>');
    expect(head).toContain('<meta name="description" content="A post"/>');
  });

  it('lets the client start before the stream ends: async entry, bootstrap and payload ahead of the content', async () => {
    const html = await renderer.render(data());

    const head = html.slice(html.indexOf('<head>'), html.indexOf('</head>'));
    expect(head).toMatch(/<script type="module" src="\/assets\/entry\.js" async=""><\/script>/);
    // React hoists async scripts ahead of other head content, so the bootstrap must not replace a handler the
    // client entry may already have installed.
    expect(head).toContain('self.__netjes_d=self.__netjes_d||');
    const body = html.slice(html.indexOf('<body>') + '<body>'.length);
    expect(body.startsWith('<script id="__NETJES_DATA__" type="application/json">')).toBe(true);
  });

  it('links the build assets in the head', async () => {
    const html = await renderer.render(data());

    const head = html.slice(html.indexOf('<head>'), html.indexOf('</head>'));
    expect(head).toContain('<link rel="stylesheet" href="/assets/app.css"/>');
    expect(head).toContain('<link rel="modulepreload" href="/assets/react.js"/>');
    expect(head).toMatch(/<script type="module" src="\/assets\/entry\.js"[^>]*><\/script>/);
  });

  it('embeds the payload verbatim for hydration', async () => {
    const json = data();

    const html = await renderer.render(json);

    expect(html).toContain(`<script id="__NETJES_DATA__" type="application/json">${json}</script>`);
    // The entry script, the deferred-values bootstrap and the payload: nothing in the payload closes a script.
    expect(html.match(/<\/script>/g)?.length).toBe(3);
  });

  it('puts the CSP nonce on every script and style it renders, and gives it to pages', async () => {
    const html = await renderer.render(
      data({ page: 'app/nonce/page.tsx' }, { nonce: 'n0nce', devStyles: [{ id: '/app.css', css: 'body{}' }] }),
    );

    expect(html).toContain('<script type="module" src="/assets/entry.js" async="" nonce="n0nce"></script>');
    expect(html).toContain('<link rel="modulepreload" href="/assets/react.js" nonce="n0nce"/>');
    expect(html).toContain('<style data-vite-dev-id="/app.css" nonce="n0nce">');
    expect(html).toMatch(/<script id="__NETJES_DATA__" type="application\/json" nonce="n0nce">/);
    // Vite reads it from here for the styles it injects.
    expect(html).toContain('<meta property="csp-nonce" nonce="n0nce"/>');
    expect(html).toContain('<p>nonce n0nce</p>');
  });

  it('renders no nonce attributes without a nonce', async () => {
    const html = await renderer.render(data());

    expect(html).not.toContain('nonce');
  });

  it('gives pages the public config the server sent', async () => {
    const html = await renderer.render(data({ page: 'app/config/page.tsx' }, { publicConfig: { apiUrl: 'https://api.example.com' } }));

    expect(html).toContain('<p>api at https://api.example.com</p>');
  });

  it('builds image URLs from the server\'s image config', async () => {
    const html = await renderer.render(data({ page: 'app/hero/page.tsx' }, { images: { path: '/_img', widths: [256, 512] } }));

    // 1x and 2x both need the largest width here, so it is offered once (as Next.js does).
    expect(html).toContain('srcSet="/_img?url=%2Fhero.png&amp;w=512&amp;q=75 1x"');
  });

  it('inlines dev styles so the page is styled before scripts run', async () => {
    const html = await renderer.render(data({}, { devStyles: [{ id: '/app/styles/global.css', css: 'body{color:red}' }] }));

    expect(html).toContain('<style data-vite-dev-id="/app/styles/global.css">body{color:red}</style>');
  });

  it('renders the built-in 404 page when the app has none', async () => {
    const html = await renderer.render(data({ route: null, page: null, status: 404, props: {} }));

    expect(html).toContain('This page could not be found');
    expect(html).toContain('<header>Demo</header>');
  });

  it('renders the built-in error page with a message of several lines on lines of its own', async () => {
    // A syntax error in development: Vite's code frame.
    const message = 'Transform failed with 1 error:\n\n   ╭─[ app/about/page.tsx:3:1 ]\n 3 │ }\n   │ ╰── `>` expected';

    const html = await renderer.render(data({ route: null, page: null, status: 500, props: { status: 500, message, stack: null } }));

    expect(html).toContain('Something went wrong');
    expect(html).toMatch(/<p style="white-space:pre-wrap;[^"]*">Transform failed/);
    expect(html).toContain(`${message.replaceAll('>', '&gt;')}</p>`);
  });

  it('renders queries seeded by the C# loader', async () => {
    const html = await renderer.render(
      data({ page: 'app/likes/page.tsx', queries: [{ key: ['likes', 'hello'], data: 4, updatedAt: Date.now() }] }),
    );

    expect(html).toContain('<p>4 likes</p>');
  });

  it('renders a path that starts with // as that path, as the browser reads it', async () => {
    const html = await renderer.render(data({ page: 'app/pathname/page.tsx', url: '//evil.example/x' }));

    expect(html).toContain('<p>at //evil.example/x</p>');
  });

  it('rejects when the page throws while rendering', async () => {
    await expect(renderer.render(data({ page: 'app/broken/page.tsx' }))).rejects.toThrow('window is not defined');
  });

  it('rejects with the error a page module throws while it loads, so the sidecar reports its message and stack', async () => {
    // A syntax error, a bad import, or code at the top of the module that needs the browser.
    const thrown = new ReferenceError('window is not defined');
    const failing = createServerRenderer({
      '/app/layout.tsx': async () => ({ default: Layout }),
      '/app/broken/page.tsx': async () => {
        throw thrown;
      },
    });

    await expect(failing.render(data({ page: 'app/broken/page.tsx' }))).rejects.toBe(thrown);
    await expect(failing.renderToStream(data({ page: 'app/broken/page.tsx' }))).rejects.toBe(thrown);
  });
});

describe('the server QueryClient', () => {
  it('leaves no garbage-collection timers behind, whatever gcTime the app configured', async () => {
    const Shell = createQueryShell({ defaultOptions: { queries: { gcTime: 300_000 } } });
    const loader = createModuleLoader({ '/app/likes/page.tsx': async () => ({ default: Likes }) });
    const initial = JSON.parse(data({ page: 'app/likes/page.tsx', segments: [], queries: [{ key: ['likes', 'hello'], data: 4, updatedAt: Date.now() }] }));
    const modules = await loader.loadRoute(initial);
    vi.useFakeTimers();
    try {
      const html = renderToString(<RouterProvider initial={initial} initialModules={modules} loader={loader} errorPage={null} shell={Shell} />);

      expect(html).toContain('4 likes');
      expect(vi.getTimerCount()).toBe(0);
    } finally {
      vi.useRealTimers();
    }
  });

  it('clears the render\'s QueryClient when the document is done, so a query\'s own gcTime leaves no timer', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      // Not seeded by C#: the query is created by useQuery, with its own gcTime.
      const html = await renderer.render(data({ page: 'app/own-gc/page.tsx', segments: [] }));

      expect(html).toContain('loading');
      expect(vi.getTimerCount()).toBe(0);
    } finally {
      vi.useRealTimers();
    }
  });
});

describe('streaming', () => {
  async function chunks(stream: AsyncIterable<Buffer | string>, onChunk: (text: string) => void) {
    for await (const chunk of stream) onChunk(chunk.toString());
  }

  it('clears the render\'s QueryClient once a streamed document has been sent', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      const stream = await renderer.renderToStream(data({ page: 'app/own-gc/page.tsx', segments: [] }));
      let html = '';
      await chunks(stream, (text) => (html += text));
      await new Promise((resolve) => setImmediate(resolve));

      expect(html).toContain('loading');
      expect(vi.getTimerCount()).toBe(0);
    } finally {
      vi.useRealTimers();
    }
  });

  it('sends the shell with the loading fallback first, then the suspended content', async () => {
    let release: (value: string) => void = () => {};
    const slowData = new Promise<string>((resolve) => {
      release = resolve;
    });
    function Slow(): ReactNode {
      return <p>{use(slowData)}</p>;
    }
    const streaming = createServerRenderer({
      '/app/slow/page.tsx': async () => ({
        default: () => (
          <Suspense fallback={<p>loading comments…</p>}>
            <Slow />
          </Suspense>
        ),
      }),
    });

    const stream = await streaming.renderToStream(data({ page: 'app/slow/page.tsx', segments: [] }));
    const received: string[] = [];
    const done = chunks(stream, (text) => received.push(text));
    await new Promise((r) => setTimeout(r, 30));
    const shell = received.join('');
    release('3 comments');
    await done;

    expect(shell).toContain('loading comments…');
    expect(shell).not.toContain('3 comments');
    expect(received.join('')).toContain('3 comments');
    expect(received.join('')).toContain('</html>');
  });

  it('waits for everything when asked, e.g. for crawlers', async () => {
    let release: (value: string) => void = () => {};
    const slowData = new Promise<string>((resolve) => {
      release = resolve;
    });
    function Slow(): ReactNode {
      return <p>{use(slowData)}</p>;
    }
    const streaming = createServerRenderer({
      '/app/slow/page.tsx': async () => ({ default: () => <Suspense fallback={<p>loading…</p>}><Slow /></Suspense> }),
    });

    const pending = streaming.renderToStream(data({ page: 'app/slow/page.tsx', segments: [] }), { waitForAll: true });
    let ready = false;
    void pending.then(() => {
      ready = true;
    });
    await new Promise((r) => setTimeout(r, 30));
    expect(ready).toBe(false);
    release('done');
    const received: string[] = [];
    await chunks(await pending, (text) => received.push(text));

    expect(received.join('')).toContain('<p>done</p>');
  });

  it('renders deferred values when they arrive and hands them to the browser in scripts', async () => {
    function Comments({ comments }: { comments: Promise<string[]> }) {
      return <ul>{use(comments).map((c) => <li key={c}>{c}</li>)}</ul>;
    }
    const streaming = createServerRenderer({
      '/app/post/page.tsx': async () => ({
        default: (props: Record<string, unknown>) => (
          <Suspense fallback={<p>loading comments…</p>}>
            <Comments comments={props.comments as Promise<string[]>} />
          </Suspense>
        ),
      }),
    });
    const values = new Map<string, (value: unknown) => void>();
    const source = (id: string) => new Promise<unknown>((resolve) => values.set(id, resolve));

    const stream = await streaming.renderToStream(
      data({ page: 'app/post/page.tsx', segments: [], props: { comments: { $netjes: 'deferred', id: 'd0' } } }),
      { deferred: source },
    );
    const received: string[] = [];
    const done = chunks(stream, (text) => received.push(text));
    await new Promise((r) => setTimeout(r, 20));
    const shell = received.join('');
    values.get('d0')?.(['</script><b>hi</b>']);
    await done;
    const html = received.join('');

    expect(shell).toContain('loading comments…');
    expect(html).toContain('<li>&lt;/script&gt;&lt;b&gt;hi&lt;/b&gt;</li>');
    expect(html).toContain('<script>self.__netjes_d("d0",JSON.parse("[\\"\\u003c/script\\u003e\\u003cb\\u003ehi\\u003c/b\\u003e\\"]"))</script>');
    expect(html.indexOf('self.__netjes_d("d0"')).toBeGreaterThan(html.indexOf('</head>'));
  });

  it('puts the CSP nonce on the deferred-values bootstrap and on each streamed value', async () => {
    function Value({ value }: { value: Promise<string> }) {
      return <p>{use(value)}</p>;
    }
    const streaming = createServerRenderer({
      '/app/v/page.tsx': async () => ({
        default: (props: Record<string, unknown>) => (
          <Suspense fallback={<p>…</p>}>
            <Value value={props.value as Promise<string>} />
          </Suspense>
        ),
      }),
    });
    let resolveValue: (value: unknown) => void = () => {};
    const stream = await streaming.renderToStream(
      data({ page: 'app/v/page.tsx', segments: [], props: { value: { $netjes: 'deferred', id: 'd0' } } }, { nonce: 'n0nce' }),
      { deferred: () => new Promise((resolve) => (resolveValue = resolve)) },
    );
    const received: string[] = [];
    const done = chunks(stream, (text) => received.push(text));
    await new Promise((r) => setTimeout(r, 10));
    resolveValue('ready');
    await done;
    const html = received.join('');

    expect(html).toMatch(/<script nonce="n0nce">self\.__netjes_d=/);
    expect(html).toContain('<script nonce="n0nce">self.__netjes_d("d0",JSON.parse("\\"ready\\""))</script>');
  });

  it('includes the deferred-values bootstrap in every document', async () => {
    const html = await renderer.render(data());

    expect(html).toMatch(/<script>self\.__netjes_d=/);
  });

  it('documents the hash a hash-based CSP allows the deferred-values bootstrap by', async () => {
    const html = await renderer.render(data());
    const bootstrap = /<script>(self\.__netjes_d=.*?)<\/script>/.exec(html)?.[1] ?? '';
    const hash = `sha256-${createHash('sha256').update(bootstrap).digest('base64')}`;

    // Output-cached pages cannot use a nonce, and the hash changes whenever the bootstrap's text does.
    expect(bootstrap).not.toBe('');
    expect(await readFile(new URL('../../../docs/architecture.md', import.meta.url), 'utf8')).toContain(`\`'${hash}'\``);
  });

  it.each([false, true])('puts deferred scripts between elements, never inside one (waitForAll %s)', async (waitForAll) => {
    function Comments({ comments }: { comments: Promise<string[]> }) {
      return <ul>{use(comments).map((c) => <li key={c}>{c}</li>)}</ul>;
    }
    const streaming = createServerRenderer({
      '/app/post/page.tsx': async () => ({
        default: (props: Record<string, unknown>) => (
          <div>
            <p className={'x'.repeat(10)}>short text</p>
            <Suspense fallback={<p>loading</p>}>
              <Comments comments={props.comments as Promise<string[]>} />
            </Suspense>
          </div>
        ),
      }),
    });

    // A large payload makes React write the shell in several chunks; the value is ready before the shell.
    const stream = await streaming.renderToStream(
      data({ page: 'app/post/page.tsx', segments: [], props: { big: 'z'.repeat(3000), comments: { $netjes: 'deferred', id: 'd0' } } }),
      { deferred: () => Promise.resolve(['one']), waitForAll },
    );
    let html = '';
    await chunks(stream, (text) => {
      html += text;
    });

    const at = html.indexOf('<script>self.__netjes_d("d0"');
    const dataStart = html.indexOf('<script id="__NETJES_DATA__"');
    const dataEnd = html.indexOf('</script>', dataStart);
    expect(at).toBeGreaterThan(0);
    expect(at > dataStart && at < dataEnd).toBe(false);
    expect(html[at - 1]).toBe('>');
    expect(() => JSON.parse(html.slice(html.indexOf('>', dataStart) + 1, dataEnd).replace(/\\u003c/g, '<'))).not.toThrow();
  });

  it('rejects before sending anything when the shell fails', async () => {
    await expect(renderer.renderToStream(data({ page: 'app/broken/page.tsx' }))).rejects.toThrow('window is not defined');
  });
});

describe('finished Suspense boundaries', () => {
  // About 22 kB in all, payload and markup: more than React's default progressiveChunkSize (12.8 kB), past which
  // React would send a finished boundary after the shell, hidden, for an inline script to reveal.
  const tasks = Array.from({ length: 800 }, (_, i) => `Task ${i}`);
  let release: () => void = () => {};
  /** Something the board waits for, when set: it is not ready when the shell is. */
  let late: Promise<void> | undefined;
  function Late({ children }: { children: ReactNode }) {
    if (late) use(late);
    return children;
  }
  const boards = createServerRenderer({
    '/app/board/page.tsx': async () => ({
      default: (props: Record<string, unknown>) => (
        <main>
          <h1>Board</h1>
          <Suspense fallback={<p>loading the board…</p>}>
            <Late>
              <ul>{(props.tasks as string[]).map((task) => <li key={task}>{task}</li>)}</ul>
            </Late>
          </Suspense>
          <footer>end of the board</footer>
        </main>
      ),
    }),
  });
  const board = () => data({ page: 'app/board/page.tsx', segments: [], props: { tasks } });

  async function documentFor(mode: 'render' | 'stream' | 'waitForAll') {
    if (mode === 'render') return boards.render(board());
    let html = '';
    for await (const chunk of await boards.renderToStream(board(), { waitForAll: mode === 'waitForAll' })) html += String(chunk);
    return html;
  }

  /** What a browser without JavaScript shows: the boundary's content where the boundary is, never its fallback. */
  function expectInPlace(html: string) {
    expect(html).not.toContain('loading the board…');
    expect(html).not.toMatch(/<div hidden/);
    const last = html.indexOf('<li>Task 799</li>');
    expect(last).toBeGreaterThan(html.indexOf('<h1>Board</h1>'));
    expect(last).toBeLessThan(html.indexOf('<footer>end of the board</footer>'));
  }

  it.each(['render', 'stream', 'waitForAll'] as const)('writes a large one in place, so it shows without JavaScript (%s)', async (mode) => {
    late = undefined;

    expectInPlace(await documentFor(mode));
  });

  it('gives crawlers a large one that finished after the shell was ready in place as well', async () => {
    late = new Promise<void>((resolve) => {
      release = resolve;
    });
    const html = documentFor('waitForAll');
    setTimeout(release, 20);

    expectInPlace(await html);
  });
});

describe('aborting', () => {
  it('stops waiting for suspended content when the render is aborted', async () => {
    const never = new Promise<never>(() => {});
    function Waits(): ReactNode {
      return use(never);
    }
    const slow = createServerRenderer({
      '/app/slow/page.tsx': async () => ({
        default: () => (
          <Suspense fallback={<p>loading</p>}>
            <Waits />
          </Suspense>
        ),
      }),
    });
    const controller = new AbortController();
    setTimeout(() => controller.abort(new Error('render timed out')), 50);

    await expect(slow.render(data({ page: 'app/slow/page.tsx', segments: [] }), { signal: controller.signal })).rejects.toThrow(
      'render timed out',
    );
  });
});
