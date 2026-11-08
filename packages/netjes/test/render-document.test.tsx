// @vitest-environment node
// renderDocument and inlineScriptHashes from netjes/testing: a page's whole document, rendered from its props as the
// sidecar renders it for a crawler. Each test passes a pages map of its own.
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { type ReactNode, Suspense, use } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { z } from 'zod';
import { CSP_SCRIPT_HASHES, DEFERRED_BOOTSTRAP } from '../src/deferred.ts';
import type { ErrorPageProps, LayoutProps } from '../src/modules.ts';
import { mounted } from '../src/mounted.tsx';
import { DATA_ELEMENT_ID, type PageKind, type Params, type SegmentRef } from '../src/payload.ts';
import { usePublicConfig } from '../src/public-config.tsx';
import { documentPayload } from '../src/render-document.tsx';
import { useParams, useSharedProps } from '../src/router.tsx';
import { type RenderDocumentOptions, inlineScriptHashes, renderDocument } from '../src/testing.tsx';

const site = z.object({ siteName: z.string() });

function RootLayout({ children }: LayoutProps) {
  return (
    <div className="root">
      <header>{useSharedProps(site).siteName}</header>
      <main>{children}</main>
    </div>
  );
}

function BlogLayout({ children, categories }: LayoutProps & { categories?: string[] }) {
  return (
    <section className="blog">
      <nav aria-label="Categories">{categories?.join(', ') ?? 'No categories'}</nav>
      {children}
    </section>
  );
}

function AboutLayout({ children }: LayoutProps) {
  return <aside className="about">{children}</aside>;
}

function Post({ title }: { title?: unknown }) {
  return <h1>{String(title)}</h1>;
}

function Settings() {
  const config = usePublicConfig(z.object({ region: z.string() }));
  return <p>{`slug ${String(useParams().slug)} in ${config.region}`}</p>;
}

function Comments({ comments }: { comments: PromiseLike<string[]> }) {
  return (
    <ul>
      {use(comments).map((comment) => (
        <li key={comment}>{comment}</li>
      ))}
    </ul>
  );
}

function PostWithComments({ thread }: { thread: { comments: PromiseLike<string[]> } }) {
  return (
    <article>
      <h1>With comments</h1>
      <Suspense fallback={<p>Loading comments</p>}>
        <Comments comments={thread.comments} />
      </Suspense>
    </article>
  );
}

function Tags({ tags }: { tags: PromiseLike<string>[] }) {
  return (
    <Suspense fallback={<p>Loading tags</p>}>
      <ul>
        {tags.map((tag, i) => (
          <li key={i}>{use(tag)}</li>
        ))}
      </ul>
    </Suspense>
  );
}

function Notice({ notice }: { notice: PromiseLike<string> }) {
  return <p className="notice">{use(notice)}</p>;
}

function NoticeLayout({ children, notice }: LayoutProps & { notice: PromiseLike<string> }) {
  return (
    <div>
      <Suspense fallback={<p>Loading notice</p>}>
        <Notice notice={notice} />
      </Suspense>
      {children}
    </div>
  );
}

function ErrorPage({ error }: ErrorPageProps) {
  return <h1>{`Error: ${error.message} (${String(error.status)})`}</h1>;
}

function Scripts() {
  return (
    <>
      <script dangerouslySetInnerHTML={{ __html: 'x()' }} />
      <script type="application/ld+json" dangerouslySetInnerHTML={{ __html: '{"@type":"WebSite"}' }} />
      <script type="module" src="/widget.js" async />
    </>
  );
}

const page = (Component: (props: never) => ReactNode) => async () => ({ default: Component });

const modules = {
  '/app/layout.tsx': page(RootLayout),
  '/app/error.tsx': page(ErrorPage),
  '/app/not-found.tsx': page(() => <h1>Not found</h1>),
  '/app/forbidden.tsx': page(() => <h1>Forbidden</h1>),
  '/app/unauthorized.tsx': page(() => <h1>Sign in</h1>),
  '/app/page.tsx': page(() => <h1>Home</h1>),
  '/app/about/layout.tsx': page(AboutLayout),
  '/app/about/page.tsx': page(() => <h1>About</h1>),
  '/app/blog/layout.tsx': page(BlogLayout),
  '/app/blog/[slug]/page.tsx': page(Post),
  '/app/blog/[slug]/settings/page.tsx': page(Settings),
  '/app/comments/page.tsx': page(PostWithComments),
  '/app/notice/layout.tsx': page(NoticeLayout),
  '/app/notice/page.tsx': page(() => <h1>Notice board</h1>),
  '/app/scripts/page.tsx': page(Scripts),
};

const render = (file: string, options: RenderDocumentOptions = {}) =>
  renderDocument(file, { modules, shared: { siteName: 'Demo' }, ...options });

/** The payload the document carries for the client router, as the browser reads it back. */
function payloadOf(html: string) {
  const json = new RegExp(`<script id="${DATA_ELEMENT_ID}" type="application/json"[^>]*>(.*?)</script>`, 's').exec(html)?.[1];
  return JSON.parse(json ?? 'null') as Record<string, unknown> & { segments: SegmentRef[] };
}

/** The source that allows a script by its text, as a browser hashes it. */
const hashSource = (script: string) => `'sha256-${createHash('sha256').update(script).digest('base64')}'`;

afterEach(() => {
  vi.restoreAllMocks();
});

describe('renderDocument', () => {
  it('renders a page inside the layouts the pages map holds for it, with its props', async () => {
    const html = await render('app/blog/[slug]/page.tsx', { props: { title: 'Hello' }, params: { slug: 'hello' }, url: '/blog/hello' });

    expect(html.startsWith('<!DOCTYPE html><html lang="en">')).toBe(true);
    expect(html).toMatch(/<header>Demo<\/header><main><section class="blog">.*<h1>Hello<\/h1><\/section><\/main>/s);
    expect(html).toContain(`<script>${DEFERRED_BOOTSTRAP}</script>`);
    expect(html).not.toContain('class="about"');
    expect(payloadOf(html)).toMatchObject({
      route: '/blog/[slug]',
      page: 'app/blog/[slug]/page.tsx',
      props: { title: 'Hello' },
      params: { slug: 'hello' },
      url: '/blog/hello',
      status: 200,
    });
  });

  it('hands each layout the props it is given', async () => {
    const html = await render('app/blog/[slug]/page.tsx', {
      props: { title: 'Hello' },
      layouts: { 'app/blog/layout.tsx': { categories: ['react', 'dotnet'] } },
    });

    expect(html).toContain('<nav aria-label="Categories">react, dotnet</nav>');
    expect(payloadOf(html).segments).toEqual([
      { id: 'app', layout: 'app/layout.tsx', error: 'app/error.tsx', depth: 0 },
      { id: 'app/blog', layout: 'app/blog/layout.tsx', props: { categories: ['react', 'dotnet'] }, depth: 1 },
    ]);
  });

  it('says which layouts a page has when it is given props for another one', async () => {
    await expect(render('app/blog/[slug]/page.tsx', { layouts: { 'app/about/layout.tsx': {} } })).rejects.toThrow(
      'netjes: renderDocument has props for app/about/layout.tsx, which is not a layout of app/blog/[slug]/page.tsx. ' +
        'Its layouts are app/layout.tsx and app/blog/layout.tsx.',
    );
  });

  it("sets the document's lang, the public config and the route values from its options", async () => {
    const html = await render('app/blog/[slug]/settings/page.tsx', { lang: 'nl', publicConfig: { region: 'eu' }, params: { slug: 'hello' } });

    expect(html.startsWith('<!DOCTYPE html><html lang="nl">')).toBe(true);
    expect(html).toContain('<p>slug hello in eu</p>');
  });

  it('renders a not-found page with its kind and status', async () => {
    const html = await render('app/not-found.tsx', { url: '/missing' });

    expect(html).toContain('<header>Demo</header><main><h1>Not found</h1></main>');
    expect(payloadOf(html)).toMatchObject({ route: null, page: 'app/not-found.tsx', pageKind: 'notFound', status: 404, url: '/missing' });
  });

  it.each([
    ['app/forbidden.tsx', 'forbidden', 403, '<h1>Forbidden</h1>'],
    ['app/unauthorized.tsx', 'unauthorized', 401, '<h1>Sign in</h1>'],
    ['app/error.tsx', 'error', 500, '<h1>Error: Something went wrong (undefined)</h1>'],
  ])('renders %s with its kind and the status the server gives it', async (file, kind, status, heading) => {
    const html = await render(file);

    expect(html).toContain(`<main>${heading}</main>`);
    expect(payloadOf(html)).toMatchObject({ route: null, page: file, pageKind: kind, status });
  });

  it("renders a folder's boundary file inside the layouts down to its own folder, with no route", async () => {
    const blog = { ...modules, '/app/blog/not-found.tsx': page(() => <h1>No such post</h1>) };

    const html = await render('app/blog/not-found.tsx', { modules: blog, url: '/blog/missing', params: { slug: 'missing' } });

    expect(html).toContain('<main><section class="blog"><nav aria-label="Categories">No categories</nav><h1>No such post</h1></section></main>');
    expect(payloadOf(html)).toMatchObject({ route: null, page: 'app/blog/not-found.tsx', pageKind: 'notFound', status: 404 });
    expect(payloadOf(html).segments).toEqual([
      { id: 'app', layout: 'app/layout.tsx', error: 'app/error.tsx', depth: 0 },
      { id: 'app/blog', layout: 'app/blog/layout.tsx', depth: 1 },
    ]);
  });

  it('renders the global error page without any layout, and only from the app directory itself', async () => {
    const failing = {
      ...modules,
      '/app/global-error.tsx': page(() => <h1>Everything failed</h1>),
      '/app/blog/global-error.tsx': page(() => <h1>Not a global error page</h1>),
    };

    const html = await render('app/global-error.tsx', { modules: failing });

    expect(html).toContain('<h1>Everything failed</h1>');
    expect(html).not.toContain('<header>Demo</header>');
    expect(payloadOf(html)).toMatchObject({ route: null, page: 'app/global-error.tsx', pageKind: 'error', status: 500, segments: [] });
    await expect(render('app/blog/global-error.tsx', { modules: failing })).rejects.toThrow(
      'netjes: renderDocument renders a page or a not-found, error, forbidden, unauthorized or global-error file, not app/blog/global-error.tsx.',
    );
  });

  it('renders an error page from the props it is given, outside the error boundary of its own folder', async () => {
    const html = await render('app/error.tsx', { props: { status: 503, message: 'Down for maintenance' }, status: 503 });

    expect(html).toContain('<h1>Error: Down for maintenance (503)</h1>');
    expect(payloadOf(html)).toMatchObject({ pageKind: 'error', status: 503, props: { status: 503, message: 'Down for maintenance' } });
    expect(payloadOf(html).segments).toEqual([{ id: 'app', layout: 'app/layout.tsx', depth: 0 }]);
  });

  it('puts the nonce on every script', async () => {
    const html = await render('app/comments/page.tsx', { props: { thread: { comments: Promise.resolve(['First']) } }, nonce: 'n0nce' });

    const scripts = [...html.matchAll(/<script\b[^>]*>/g)].map(([tag]) => tag);
    // The bootstrap, the payload and the deferred value.
    expect(scripts).toHaveLength(3);
    for (const tag of scripts) expect(tag).toContain('nonce="n0nce"');
    expect(html).toContain('<meta property="csp-nonce" nonce="n0nce"/>');
    expect(payloadOf(html)).toMatchObject({ document: { nonce: 'n0nce' } });
  });

  describe('renders Promises in props as Page.Defer values', () => {
    it('in place, once they resolve', async () => {
      const html = await render('app/comments/page.tsx', { props: { thread: { comments: Promise.resolve(['First', 'Second']) } } });

      expect(html).toContain('<ul><li>First</li><li>Second</li></ul>');
      expect(html).not.toContain('Loading comments');
      expect(payloadOf(html)).toMatchObject({ props: { thread: { comments: { $netjes: 'deferred', id: 'd0' } } } });
      expect(html).toContain('<script>self.__netjes_d("d0",JSON.parse("[\\"First\\",\\"Second\\"]"))</script>');
    });

    it('in arrays too', async () => {
      const tagged = { ...modules, '/app/tags/page.tsx': page(Tags) };

      const html = await render('app/tags/page.tsx', { modules: tagged, props: { tags: [Promise.resolve('react'), Promise.resolve('dotnet')] } });

      expect(html).toContain('<ul><li>react</li><li>dotnet</li></ul>');
      expect(payloadOf(html)).toMatchObject({
        props: { tags: [{ $netjes: 'deferred', id: 'd0' }, { $netjes: 'deferred', id: 'd1' }] },
      });
    });

    it("in a layout's props", async () => {
      const html = await render('app/notice/page.tsx', { layouts: { 'app/notice/layout.tsx': { notice: Promise.resolve('Closed on Monday') } } });

      expect(html).toContain('<p class="notice">Closed on Monday</p>');
      expect(payloadOf(html).segments[1]).toMatchObject({ props: { notice: { $netjes: 'deferred', id: 'd0' } } });
    });

    it('as their Suspense fallback when they reject, with the message the browser gets', async () => {
      const failure = new Error('Comments are down');
      const comments = Promise.reject(failure);
      const handled = vi.spyOn(comments, 'then');
      const logged = vi.spyOn(console, 'error').mockImplementation(() => {});

      const rendering = render('app/comments/page.tsx', { props: { thread: { comments } } });
      // Before renderDocument's first await, whatever is in Vitest's module cache: Node reports a rejection that has
      // no handler once the current task ends.
      expect(handled).toHaveBeenCalled();
      const html = await rendering;

      expect(html).toContain('<p>Loading comments</p>');
      expect(html).toContain('<script>self.__netjes_d("d0",null,"Comments are down")</script>');
      // React reports the error and leaves the boundary for the browser to render, as in the sidecar.
      expect(logged).toHaveBeenCalledWith(failure);
    });

    it('until the signal aborts when they are still pending, rejecting with its reason', async () => {
      const signal = AbortSignal.timeout(100);

      const error: unknown = await render('app/comments/page.tsx', { props: { thread: { comments: new Promise(() => {}) } }, signal }).catch(
        (reason: unknown) => reason,
      );

      expect(error).toBe(signal.reason);
      expect(error).toMatchObject({ name: 'TimeoutError' });
    });
  });

  it('says which page files it knows when the page is not in the map', async () => {
    await expect(render('app/missing/page.tsx')).rejects.toThrow(
      'netjes: renderDocument has no app/missing/page.tsx in the pages map. The files it can render are app/about/page.tsx, ' +
        'app/blog/[slug]/page.tsx, app/blog/[slug]/settings/page.tsx, app/comments/page.tsx, app/error.tsx, app/forbidden.tsx, ' +
        'app/not-found.tsx, app/notice/page.tsx, app/page.tsx, app/scripts/page.tsx and app/unauthorized.tsx.',
    );
    await expect(render('/app/page.tsx')).rejects.toThrow('name the file without the leading slash: app/page.tsx');
  });

  it('refuses a file that is not a page or a boundary', async () => {
    await expect(render('app/blog/layout.tsx')).rejects.toThrow(
      'netjes: renderDocument renders a page or a not-found, error, forbidden, unauthorized or global-error file, not app/blog/layout.tsx.',
    );
  });

  it('refuses a page in a private folder, which the server does not route', async () => {
    const drafts = { ...modules, '/app/_drafts/page.tsx': page(() => <h1>Draft</h1>) };

    await expect(render('app/_drafts/page.tsx', { modules: drafts })).rejects.toThrow(
      'netjes: renderDocument cannot render app/_drafts/page.tsx: a private folder is not routed.',
    );
  });

  describe('with page packages mounted', () => {
    const help = mounted('@acme/help-center');
    function HelpLayout({ children }: LayoutProps) {
      return (
        <div className="help">
          <help.Link href="/">Help home</help.Link>
          {children}
        </div>
      );
    }
    const withHelp = {
      ...modules,
      '/@acme/help-center/app/layout.tsx': page(HelpLayout),
      '/@acme/help-center/app/(center)/layout.tsx': page(({ children }: LayoutProps) => <section className="center">{children}</section>),
      '/@acme/help-center/app/(center)/articles/[slug]/page.tsx': page(() => <help.Link href="/articles">All articles</help.Link>),
      '/app/(marketing)/help/(center)/shortcuts/page.tsx': page(() => <h1>Shortcuts</h1>),
      '/app/(marketing)/help/page.tsx': page(() => <h1>Not here</h1>),
    };

    it("renders an app page in a package's folder inside the package's layouts and the app's", async () => {
      const html = await render('app/(marketing)/help/(center)/shortcuts/page.tsx', {
        modules: withHelp,
        mounts: { '@acme/help-center': '/(marketing)/help' },
        url: '/help/shortcuts',
      });

      expect(html).toMatch(/<div class="root">.*<div class="help"><a href="\/help">Help home<\/a><section class="center"><h1>Shortcuts<\/h1>/s);
      expect(payloadOf(html).mounts).toEqual({ '@acme/help-center': '/help' });
    });

    it("renders a package's page under a prefix with a value, its links below the mount for the page's params", async () => {
      const html = await render('@acme/help-center/app/(center)/articles/[slug]/page.tsx', {
        modules: withHelp,
        mounts: { '@acme/help-center': '/[tenant]/help' },
        params: { tenant: "o'brien & co", slug: 'x' },
        url: '/o%27brien%20%26%20co/help/articles/x',
      });

      expect(payloadOf(html)).toMatchObject({ route: '/[tenant]/help/articles/[slug]', mounts: { '@acme/help-center': '/o%27brien%20%26%20co/help' } });
      expect(html).toContain('<a href="/o%27brien%20%26%20co/help/articles">All articles</a>');
    });

    it('gives a prefix whose value the params lack, or leave the path with, no URL', () => {
      for (const tenant of [undefined, '', '.', '..']) {
        const payload = documentPayload(
          'app/page.tsx',
          { mounts: { '@acme/help-center': '/[tenant]/help' }, params: tenant === undefined ? {} : { tenant } },
          Object.keys(withHelp),
          'app',
        );
        expect(payload.mounts, String(tenant)).toEqual({ '@acme/help-center': null });
      }
    });

    it("refuses a package's file it does not mount, and a prefix MapPagePackage refuses", async () => {
      const listed = [{ name: '@acme/help-center', pages: 'app' }];
      const file = '@acme/help-center/app/(center)/articles/[slug]/page.tsx';

      expect(() => documentPayload(file, {}, Object.keys(withHelp), 'app', listed)).toThrow(
        `netjes: renderDocument cannot render ${file}, a file of the page package '@acme/help-center', which it does not mount: ` +
          "pass its prefix as MapPagePackage takes it, as in mounts: { '@acme/help-center': '/help' }.",
      );
      const refused = ['help', '/help/', '//help', '/../help', '/[...rest]', '/@modal', '/_private', '/(.)help', '/%2e%2e', '/a\\b'];
      for (const prefix of [...refused, '/[tenant]/x/[tenant]', '/[tenant]/x/[Tenant]', '/[1x]']) {
        await expect(render(file, { modules: withHelp, mounts: { '@acme/help-center': prefix } }), prefix).rejects.toThrow(
          `netjes: renderDocument cannot mount '@acme/help-center' at '${prefix}'`,
        );
      }
    });

    it('refuses two layouts of two sources in one folder, as the server does', async () => {
      const twice = { ...withHelp, '/app/(marketing)/help/layout.tsx': page(AboutLayout) };

      await expect(
        render('app/(marketing)/help/(center)/shortcuts/page.tsx', { modules: twice, mounts: { '@acme/help-center': '/(marketing)/help' } }),
      ).rejects.toThrow(
        'netjes: renderDocument has two layout files for app/(marketing)/help: @acme/help-center/app/layout.tsx and app/(marketing)/help/layout.tsx.',
      );
    });

    it("refuses a page package's global error page, which only the app has", () => {
      const files = [...Object.keys(withHelp), '/@acme/help-center/app/global-error.tsx'];

      expect(() => documentPayload('@acme/help-center/app/global-error.tsx', { mounts: { '@acme/help-center': '/' } }, files, 'app')).toThrow(
        'netjes: renderDocument renders a page or a not-found, error, forbidden, unauthorized or global-error file, not @acme/help-center/app/global-error.tsx.',
      );
    });
  });

  it('refuses a page under parallel route slots', async () => {
    const slots = { ...modules, '/app/@modal/default.tsx': page(() => null), '/app/@modal/login/page.tsx': page(() => <h1>Log in</h1>) };

    await expect(renderDocument('app/@modal/login/page.tsx', { modules: slots })).rejects.toThrow(
      'netjes: renderDocument cannot render app/@modal/login/page.tsx, which is in the parallel route slot app/@modal: ' +
        'the server chooses what a slot renders.',
    );
    await expect(renderDocument('app/about/page.tsx', { modules: slots })).rejects.toThrow(
      'netjes: renderDocument cannot render app/about/page.tsx, which renders under the parallel route slot app/@modal: ' +
        'the server chooses what a slot renders.',
    );
  });
});

describe('the route and segments renderDocument sends, for every case in fixtures/segments/cases.json', () => {
  type SegmentCase = {
    name: string;
    appDir?: string;
    files: string[];
    url: string;
    page: string;
    params: Params;
    route: string | null;
    pageKind?: PageKind;
    status: number;
    segments: SegmentRef[];
    /** Page packages, with their files in their pages folder, app, mounted at their prefix. */
    packages?: { name: string; prefix: string; files: string[] }[];
    mounts?: Record<string, string | null>;
  };
  const fixture = JSON.parse(readFileSync(new URL('./fixtures/segments/cases.json', import.meta.url), 'utf8')) as { cases: SegmentCase[] };

  it.each(fixture.cases.map((expected) => [expected.name, expected] as const))('are what the server sends for %s', (_, expected) => {
    const packages = expected.packages ?? [];
    const payload = documentPayload(
      expected.page,
      {
        params: expected.params,
        url: expected.url,
        ...(packages.length > 0 ? { mounts: Object.fromEntries(packages.map((p) => [p.name, p.prefix])) } : {}),
      },
      [...expected.files, ...packages.flatMap((p) => p.files.map((file) => `${p.name}/app/${file}`))].map((file) => `/${file}`),
      expected.appDir ?? 'app',
      packages.map((p) => ({ name: p.name, pages: 'app' })),
    );

    const { page, route, pageKind, status, params, segments, mounts } = payload;
    expect({ page, route, pageKind, status, params, segments, mounts }).toEqual({
      page: expected.page,
      route: expected.route,
      pageKind: expected.pageKind,
      status: expected.status,
      params: expected.params,
      segments: expected.segments,
      mounts: expected.mounts,
    });
  });
});

describe('inlineScriptHashes', () => {
  it('hashes each inline script a browser runs, not the payload or module scripts', async () => {
    const html = await render('app/scripts/page.tsx');

    expect(html).toContain('<script type="module" src="/widget.js" async=""></script>');
    expect(await inlineScriptHashes(html)).toEqual([CSP_SCRIPT_HASHES, hashSource('x()')]);
  });

  it('follows the rules a browser applies to which scripts run, and lists each hash once', async () => {
    const html = [
      '<script type="module">m()</script>',
      '<SCRIPT TYPE=" Text/JavaScript ">j()</SCRIPT>',
      '<script type="text/javascript; charset=utf-8">p()</script>',
      '<script type="text/plain">t()</script>',
      '<script language="javascript">l()</script>',
      '<script language="vbscript">v()</script>',
      '<script nomodule>n()</script>',
      '<script></script>',
      '<script type="importmap">{"imports":{}}</script>',
      '<script type="speculationrules">{"prerender":[]}</script>',
      '<script type="module" type="text/plain">d()</script>',
      '<script src="/a.js">s()</script>',
      '<script>a()\r\nb()</script>',
      '<script>m()</script>',
    ].join('\n');

    expect(await inlineScriptHashes(html)).toEqual([
      hashSource('m()'),
      hashSource('j()'),
      hashSource('l()'),
      hashSource('{"imports":{}}'),
      hashSource('{"prerender":[]}'),
      // The first of two attributes with one name counts, as in HTML.
      hashSource('d()'),
      hashSource('a()\nb()'),
    ]);
  });
});
