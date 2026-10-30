// @vitest-environment node
import { renderToString } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { type Metadata, resolveMetadata } from '../src/metadata.tsx';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload } from '../src/payload.ts';
import { RouterProvider } from '../src/router.tsx';

describe('resolveMetadata', () => {
  it('applies the nearest layout title template to the page title', () => {
    const root: Metadata = { title: { default: 'Acme', template: '%s · Acme' }, description: 'Root' };
    const blog: Metadata = { title: { template: '%s · Blog · Acme' } };

    expect(resolveMetadata([root, blog], { title: 'Hello' }).title).toBe('Hello · Blog · Acme');
    expect(resolveMetadata([root], {}).title).toBe('Acme');
    expect(resolveMetadata([root], { title: { absolute: 'Just this' } }).title).toBe('Just this');
    expect(resolveMetadata([root], { title: 'Hi' }).description).toBe('Root');
  });

  it('puts titles into templates literally, even with $ patterns in them', () => {
    expect(resolveMetadata([{ title: { template: '%s · Acme' } }], { title: 'Save $& more, $$ off' }).title).toBe('Save $& more, $$ off · Acme');
  });

  it('lets the server metadata from C# override the files, field by field', () => {
    const resolved = resolveMetadata([{ title: { template: '%s · Acme' } }], { title: 'Post', description: 'static' }, { title: 'From C#' });

    expect(resolved.title).toBe('From C# · Acme');
    expect(resolved.description).toBe('static');
  });

  it('merges Open Graph from the layouts and the page', () => {
    const resolved = resolveMetadata(
      [{ openGraph: { siteName: 'Acme', type: 'website' } }],
      { openGraph: { title: 'Post', images: [{ url: '/og.png', width: 1200, height: 630 }] } },
    );

    expect(resolved.openGraph).toEqual({ siteName: 'Acme', type: 'website', title: 'Post', images: [{ url: '/og.png', width: 1200, height: 630 }] });
  });
});

describe('metadata exports and tags', () => {
  it('renders title, description, canonical, robots, Open Graph and Twitter tags in the head', async () => {
    const Layout = Object.assign(({ children }: { children: React.ReactNode }) => <div>{children}</div>, {});
    const modules = {
      '/app/layout.tsx': async () => ({
        default: Layout,
        metadata: { title: { default: 'Acme', template: '%s · Acme' }, openGraph: { siteName: 'Acme' } } satisfies Metadata,
      }),
      '/app/post/page.tsx': async () => ({
        default: () => <h1>Post</h1>,
        metadata: {
          title: 'Hello',
          description: 'A post',
          alternates: { canonical: 'https://acme.test/post' },
          robots: { index: false, follow: true },
          twitter: { card: 'summary_large_image', title: 'Hello on X', description: 'A post, briefly' },
        } satisfies Metadata,
      }),
    };
    const initial: PagePayload = {
      kind: 'page',
      route: '/post',
      page: 'app/post/page.tsx',
      segments: [{ id: 'app', layout: 'app/layout.tsx' }],
      props: {},
      params: {},
      shared: null,
      status: 200,
      url: '/post',
      version: 'v1',
      metadata: { openGraph: { images: [{ url: 'https://acme.test/og/hello.png' }] } },
    };
    const loader = createModuleLoader(modules);

    const html = renderToString(<RouterProvider initial={initial} initialModules={await loader.loadRoute(initial)} loader={loader} errorPage={null} />);

    expect(html).toContain('<title>Hello · Acme</title>');
    expect(html).toContain('<meta name="description" content="A post"/>');
    expect(html).toContain('<link rel="canonical" href="https://acme.test/post"/>');
    expect(html).toContain('<meta name="robots" content="noindex, follow"/>');
    expect(html).toContain('<meta property="og:title" content="Hello · Acme"/>');
    expect(html).toContain('<meta property="og:site_name" content="Acme"/>');
    expect(html).toContain('<meta property="og:image" content="https://acme.test/og/hello.png"/>');
    expect(html).toContain('<meta name="twitter:card" content="summary_large_image"/>');
    expect(html).toContain('<meta name="twitter:title" content="Hello on X"/>');
    expect(html).toContain('<meta name="twitter:description" content="A post, briefly"/>');
    // Filled in after the C# metadata is merged, so its Open Graph image is the Twitter one too.
    expect(html).toContain('<meta name="twitter:image" content="https://acme.test/og/hello.png"/>');
  });

  /** The page's HTML with `metadata` from C#, below a layout per entry of `layouts`, outermost first. */
  async function headFor(metadata: Metadata, layouts: Metadata[] = []): Promise<string> {
    const Layout = ({ children }: { children: React.ReactNode }) => <div>{children}</div>;
    const modules = {
      '/app/page.tsx': async () => ({ default: () => <h1>Home</h1> }),
      ...Object.fromEntries(layouts.map((layout, index) => [`/app/l${index}/layout.tsx`, async () => ({ default: Layout, metadata: layout })])),
    };
    const initial: PagePayload = {
      kind: 'page',
      route: '/',
      page: 'app/page.tsx',
      segments: layouts.map((_, index) => ({ id: `l${index}`, layout: `app/l${index}/layout.tsx` })),
      props: {},
      params: {},
      shared: null,
      status: 200,
      url: '/',
      version: 'v1',
      metadata,
    };
    const loader = createModuleLoader(modules);
    return renderToString(<RouterProvider initial={initial} initialModules={await loader.loadRoute(initial)} loader={loader} errorPage={null} />);
  }

  it('writes og:image:width, height and alt right after their image, in that order', async () => {
    const html = await headFor({
      openGraph: {
        images: [
          { url: 'https://acme.test/a.png', width: 1200, height: 630, alt: 'A "quoted" & <tagged> alt' },
          { url: 'https://acme.test/b.png' },
          { url: 'https://acme.test/c.png', alt: 'Only alt' },
        ],
      },
    });

    const tags = html.match(/<meta property="og:image[^>]*>/g);
    expect(tags).toEqual([
      '<meta property="og:image" content="https://acme.test/a.png"/>',
      '<meta property="og:image:width" content="1200"/>',
      '<meta property="og:image:height" content="630"/>',
      '<meta property="og:image:alt" content="A &quot;quoted&quot; &amp; &lt;tagged&gt; alt"/>',
      '<meta property="og:image" content="https://acme.test/b.png"/>',
      '<meta property="og:image" content="https://acme.test/c.png"/>',
      '<meta property="og:image:alt" content="Only alt"/>',
    ]);
  });

  it('takes an Open Graph image as a string, a URL or a descriptor, one or an array of them, and leaves out one without a URL, as Next.js does', async () => {
    const ogImages = async (images: NonNullable<Metadata['openGraph']>['images']) =>
      (await headFor({ openGraph: { images } })).match(/<meta property="og:image[^>]*>/g);

    expect(await ogImages('https://acme.test/a.png')).toEqual(['<meta property="og:image" content="https://acme.test/a.png"/>']);
    expect(await ogImages(new URL('https://acme.test/b.png'))).toEqual(['<meta property="og:image" content="https://acme.test/b.png"/>']);
    expect(await ogImages({ url: new URL('https://acme.test/c.png'), alt: 'C' })).toEqual([
      '<meta property="og:image" content="https://acme.test/c.png"/>',
      '<meta property="og:image:alt" content="C"/>',
    ]);
    expect(await ogImages(['https://acme.test/a.png', new URL('https://acme.test/b.png'), { url: 'https://acme.test/c.png', width: 1200 }])).toEqual([
      '<meta property="og:image" content="https://acme.test/a.png"/>',
      '<meta property="og:image" content="https://acme.test/b.png"/>',
      '<meta property="og:image" content="https://acme.test/c.png"/>',
      '<meta property="og:image:width" content="1200"/>',
    ]);
    expect(await ogImages(['', { url: '', alt: 'Nothing' }, 'https://acme.test/a.png'])).toEqual(['<meta property="og:image" content="https://acme.test/a.png"/>']);
  });

  it('writes twitter:image and twitter:image:alt for each Twitter image, strings included', async () => {
    const html = await headFor({
      twitter: { images: ['https://acme.test/plain.png', { url: 'https://acme.test/t.png', alt: 'Say "hi" & wave' }] },
    });

    const tags = html.match(/<meta name="twitter:image[^>]*>/g);
    expect(tags).toEqual([
      '<meta name="twitter:image" content="https://acme.test/plain.png"/>',
      '<meta name="twitter:image" content="https://acme.test/t.png"/>',
      '<meta name="twitter:image:alt" content="Say &quot;hi&quot; &amp; wave"/>',
    ]);
  });

  it('takes a Twitter image as a string, a URL or a descriptor, one or an array of them, and leaves out one without a URL, as Next.js does', async () => {
    const twitterImages = async (images: NonNullable<Metadata['twitter']>['images']) =>
      (await headFor({ twitter: { card: 'summary', images } })).match(/<meta name="twitter:image[^>]*>/g);

    expect(await twitterImages('https://acme.test/a.png')).toEqual(['<meta name="twitter:image" content="https://acme.test/a.png"/>']);
    expect(await twitterImages(new URL('https://acme.test/b.png'))).toEqual(['<meta name="twitter:image" content="https://acme.test/b.png"/>']);
    expect(await twitterImages({ url: new URL('https://acme.test/c.png'), alt: 'C' })).toEqual([
      '<meta name="twitter:image" content="https://acme.test/c.png"/>',
      '<meta name="twitter:image:alt" content="C"/>',
    ]);
    expect(await twitterImages([new URL('https://acme.test/b.png'), 'https://acme.test/a.png'])).toEqual([
      '<meta name="twitter:image" content="https://acme.test/b.png"/>',
      '<meta name="twitter:image" content="https://acme.test/a.png"/>',
    ]);
    expect(await twitterImages(['', { url: '', alt: 'Nothing' }, 'https://acme.test/a.png'])).toEqual(['<meta name="twitter:image" content="https://acme.test/a.png"/>']);
  });

  it('skips a null image or list of images, which an untyped page can pass, as Next.js does', async () => {
    const imageTags = (html: string) => html.match(/<meta (?:property|name)="(?:og|twitter):(?:image|card)[^>]*>/g);
    const untyped = (metadata: unknown) => headFor(metadata as Metadata);

    const html = await untyped({ openGraph: { siteName: 'Acme', images: null }, twitter: { images: [null, 'https://acme.test/t.png'] } });
    expect(html).toContain('<meta property="og:site_name" content="Acme"/>');
    expect(imageTags(html)).toEqual(['<meta name="twitter:card" content="summary_large_image"/>', '<meta name="twitter:image" content="https://acme.test/t.png"/>']);
    expect(imageTags(await untyped({ openGraph: { images: [null, 'https://acme.test/og.png'] }, twitter: { images: null } }))).toEqual([
      '<meta property="og:image" content="https://acme.test/og.png"/>',
      '<meta name="twitter:card" content="summary"/>',
      '<meta name="twitter:image" content="https://acme.test/og.png"/>',
    ]);
  });

  const twitterTags = (html: string) => html.match(/<meta name="twitter:[^>]*>/g);

  it('fills the Twitter card, title, description and images from Open Graph when there is no twitter metadata, as Next.js does', async () => {
    const html = await headFor({
      title: 'Hello',
      description: 'A post',
      openGraph: { title: 'Hello on OG', description: 'An OG post', images: [{ url: 'https://acme.test/og.png', width: 1200, height: 630, alt: 'OG alt' }] },
    });

    expect(twitterTags(html)).toEqual([
      '<meta name="twitter:card" content="summary_large_image"/>',
      '<meta name="twitter:title" content="Hello on OG"/>',
      '<meta name="twitter:description" content="An OG post"/>',
      '<meta name="twitter:image" content="https://acme.test/og.png"/>',
      '<meta name="twitter:image:alt" content="OG alt"/>',
    ]);
  });

  it('keeps the Twitter fields that are set and fills the rest from Open Graph, with the card from the Twitter images alone, as Next.js does', async () => {
    const layout: Metadata = { openGraph: { siteName: 'Acme', images: 'https://acme.test/og.png' }, twitter: { site: '@acme' } };

    expect(twitterTags(await headFor({ twitter: { title: 'On X' }, openGraph: { description: 'An OG post' } }, [layout]))).toEqual([
      '<meta name="twitter:card" content="summary"/>',
      '<meta name="twitter:site" content="@acme"/>',
      '<meta name="twitter:title" content="On X"/>',
      '<meta name="twitter:description" content="An OG post"/>',
      '<meta name="twitter:image" content="https://acme.test/og.png"/>',
    ]);
    expect(twitterTags(await headFor({ twitter: { description: 'On X', images: 'https://acme.test/x.png' } }, [layout]))).toEqual([
      '<meta name="twitter:card" content="summary_large_image"/>',
      '<meta name="twitter:site" content="@acme"/>',
      '<meta name="twitter:description" content="On X"/>',
      '<meta name="twitter:image" content="https://acme.test/x.png"/>',
    ]);
  });

  it('keeps an empty twitter.images, so no Open Graph image and a summary card, but fills an empty twitter.title, as Next.js does', async () => {
    const html = await headFor({ openGraph: { title: 'On OG', images: 'https://acme.test/og.png' }, twitter: { title: '', images: [] } });

    expect(twitterTags(html)).toEqual(['<meta name="twitter:card" content="summary"/>', '<meta name="twitter:title" content="On OG"/>']);
  });

  it('falls back to the page title, after its template, and description for the Twitter tags Open Graph does not set either', async () => {
    const html = await headFor({ title: 'Hello', description: 'A post' }, [{ title: { template: '%s · Acme' }, openGraph: { siteName: 'Acme' } }]);

    expect(twitterTags(html)).toEqual([
      '<meta name="twitter:card" content="summary"/>',
      '<meta name="twitter:title" content="Hello · Acme"/>',
      '<meta name="twitter:description" content="A post"/>',
    ]);
  });

  it('gives an empty Open Graph title or description the page title and description, and writes no empty one, as Next.js does', async () => {
    const titleTags = (html: string) => html.match(/<meta (?:property|name)="(?:og|twitter):(?:title|description)"[^>]*>/g);

    expect(titleTags(await headFor({ title: 'Page', description: 'A post', openGraph: { title: '', description: '' } }))).toEqual([
      '<meta property="og:title" content="Page"/>',
      '<meta property="og:description" content="A post"/>',
      '<meta name="twitter:title" content="Page"/>',
      '<meta name="twitter:description" content="A post"/>',
    ]);
    expect(titleTags(await headFor({ title: '', description: '', openGraph: { title: '', description: '', siteName: 'Acme' } }))).toBeNull();
  });

  it('gives twitter metadata without Open Graph the page title and description and a card, and writes no og tag, as Next.js does', async () => {
    const html = await headFor({ description: 'A post', twitter: { site: '@acme' } }, [{ title: { default: 'Acme' } }]);

    expect(twitterTags(html)).toEqual([
      '<meta name="twitter:card" content="summary"/>',
      '<meta name="twitter:site" content="@acme"/>',
      '<meta name="twitter:title" content="Acme"/>',
      '<meta name="twitter:description" content="A post"/>',
    ]);
    expect(html).not.toContain('og:');
    expect(twitterTags(await headFor({ twitter: { images: 'https://acme.test/x.png' } }))).toEqual([
      '<meta name="twitter:card" content="summary_large_image"/>',
      '<meta name="twitter:image" content="https://acme.test/x.png"/>',
    ]);
  });

  it('writes no detail tag for an empty alt or a zero width or height, as Next.js does', async () => {
    const html = await headFor({
      openGraph: { images: [{ url: 'https://acme.test/a.png', width: 0, height: 0, alt: '' }] },
      twitter: { images: [{ url: 'https://acme.test/t.png', alt: '' }] },
    });

    expect(html.match(/<meta (?:property|name)="(?:og|twitter):image[^>]*>/g)).toEqual([
      '<meta property="og:image" content="https://acme.test/a.png"/>',
      '<meta name="twitter:image" content="https://acme.test/t.png"/>',
    ]);
  });
});
