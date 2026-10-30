import { describe, expect, it } from 'vitest';
import { PayloadError, parseInitialPayload, parseNavigationPayload } from '../src/payload.ts';

const page = {
  kind: 'page',
  route: '/blog/[slug]',
  page: 'app/blog/[slug]/page.tsx',
  segments: [{ id: 'app', layout: 'app/layout.tsx' }],
  props: { title: 'Hello' },
  params: { slug: 'hello', rest: ['a', 'b'] },
  shared: { siteName: 'Demo' },
  status: 200,
  url: '/blog/hello',
  version: 'abc',
};

describe('parseNavigationPayload', () => {
  it('accepts a page payload', () => {
    expect(parseNavigationPayload(page)).toEqual(page);
  });

  it('accepts a redirect payload', () => {
    expect(parseNavigationPayload({ kind: 'redirect', location: '/login', version: 'abc' })).toEqual({
      kind: 'redirect',
      location: '/login',
      version: 'abc',
    });
  });

  it('keeps seeded queries, with null data', () => {
    const queries = [
      { key: ['likes', 'hello'], data: { likes: 3 }, updatedAt: 1_000 },
      { key: ['empty'], data: null, updatedAt: 2_000 },
    ];
    expect(parseNavigationPayload({ ...page, queries })).toEqual({ ...page, queries });
  });

  it('keeps the URL depth the server counted for a layout', () => {
    const parsed = parseNavigationPayload({ ...page, segments: [{ id: 'src/app/(docs)/docs/[...path]', layout: 'x.tsx', depth: 3 }] });

    expect(parsed.kind === 'page' && parsed.segments[0]?.depth).toBe(3);
  });

  it('keeps the params of slot pages and intercepting pages', () => {
    const parsed = parseNavigationPayload({
      ...page,
      segments: [{ id: 'app', slots: { crumbs: 'app/@crumbs/[...slug]/page.tsx' }, slotParams: { crumbs: { slug: ['a', 'b'] } } }],
      intercepts: [{ segment: 'app', slot: 'modal', page: 'app/@modal/(.)p/[photoId]/page.tsx', params: { photoId: '7' } }],
    });

    expect(parsed.kind === 'page' && parsed.segments[0]?.slotParams).toEqual({ crumbs: { slug: ['a', 'b'] } });
    expect(parsed.kind === 'page' && parsed.intercepts?.[0]?.params).toEqual({ photoId: '7' });
  });

  it('accepts built-in special pages without a page module', () => {
    expect(parseNavigationPayload({ ...page, route: null, page: null, status: 404 }).kind).toBe('page');
  });

  it.each([
    ['not an object', 'nope'],
    ['unknown kind', { ...page, kind: 'other' }],
    ['segments not objects', { ...page, segments: ['app/layout.tsx'] }],
    ['a segment without an id', { ...page, segments: [{ layout: 'app/layout.tsx' }] }],
    ['a segment file not a string', { ...page, segments: [{ id: 'app', loading: 1 }] }],
    ['a segment depth not a number', { ...page, segments: [{ id: 'app', depth: '1' }] }],
    ['missing status', { ...page, status: undefined }],
    ['params with numbers', { ...page, params: { id: 1 } }],
    ['redirect without location', { kind: 'redirect', version: 'abc' }],
    ['queries not an array', { ...page, queries: {} }],
    ['a query without a key', { ...page, queries: [{ data: 1, updatedAt: 1 }] }],
    ['a query with an empty key', { ...page, queries: [{ key: [], data: 1, updatedAt: 1 }] }],
    ['a query without updatedAt', { ...page, queries: [{ key: ['a'], data: 1 }] }],
  ])('rejects %s', (_, value) => {
    expect(() => parseNavigationPayload(value)).toThrow(PayloadError);
  });
});

describe('parseInitialPayload', () => {
  const document = {
    lang: 'en',
    assets: { stylesheets: ['/a.css'], modulePreloads: [], scripts: ['/entry.js'] },
    errorPage: { page: 'app/error.tsx', segments: [] },
  };

  it('requires the document section', () => {
    expect(() => parseInitialPayload(page)).toThrow(PayloadError);
    expect(parseInitialPayload({ ...page, document }).document.assets.scripts).toEqual(['/entry.js']);
  });

  it('keeps optional dev styles', () => {
    const parsed = parseInitialPayload({ ...page, document: { ...document, devStyles: [{ id: '/x.css', css: 'a{}' }] } });
    expect(parsed.document.devStyles).toEqual([{ id: '/x.css', css: 'a{}' }]);
  });

  it('keeps the image optimizer config', () => {
    const parsed = parseInitialPayload({ ...page, document: { ...document, images: { path: '/_img', widths: [320, 640] } } });
    expect(parsed.document.images).toEqual({ path: '/_img', widths: [320, 640] });
  });

  it('keeps the public config', () => {
    const parsed = parseInitialPayload({ ...page, document: { ...document, publicConfig: { apiUrl: 'https://x' } } });
    expect(parsed.document.publicConfig).toEqual({ apiUrl: 'https://x' });
  });

  it('keeps the mark of a document the server sent for the browser to render', () => {
    expect(parseInitialPayload({ ...page, document: { ...document, clientRender: true } }).document.clientRender).toBe(true);
    expect(parseInitialPayload({ ...page, document: { ...document, clientRender: false } }).document).not.toHaveProperty('clientRender');
    expect(parseInitialPayload({ ...page, document }).document).not.toHaveProperty('clientRender');
  });

  it('rejects a client-render mark that is not a boolean', () => {
    expect(() => parseInitialPayload({ ...page, document: { ...document, clientRender: 'yes' } })).toThrow(PayloadError);
  });

  it('rejects an image config with non-numeric widths', () => {
    expect(() => parseInitialPayload({ ...page, document: { ...document, images: { path: '/_img', widths: ['320'] } } })).toThrow(PayloadError);
  });
});
