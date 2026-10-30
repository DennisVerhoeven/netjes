import { describe, expect, it } from 'vitest';
import { buildHref, isRouteHref } from '../src/href.ts';

describe('isRouteHref', () => {
  const patterns = ['/', '/about', '/blog/[slug]', '/docs/[...path]', '/shop/[[...filters]]'];

  it('accepts URLs one of the patterns serves, with a query or fragment', () => {
    for (const href of ['/', '/about', '/about?x=1', '/blog/hello#top', '/docs/a/b/c', '/shop', '/shop/red/large', '/about/']) {
      expect(isRouteHref(href, patterns), href).toBe(true);
    }
  });

  it('accepts other sites and same-page links', () => {
    for (const href of ['https://example.com/x', 'http://example.com', 'mailto:a@b.c', 'tel:123', '?page=2', '#section']) {
      expect(isRouteHref(href, patterns), href).toBe(true);
    }
  });

  it('rejects paths no page serves, and other schemes', () => {
    for (const href of ['/nope', '/blog', '/blog/a/b', '/docs', 'about', 'javascript:alert(1)', '', '//about', '/\\about']) {
      expect(isRouteHref(href, patterns), href).toBe(false);
    }
  });
});

describe('buildHref', () => {
  it('fills dynamic segments, encoding each value', () => {
    expect(buildHref('/blog/[slug]', { slug: 'a b/c?' })).toBe('/blog/a%20b%2Fc%3F');
    expect(buildHref('/[team]/settings', { team: 'core' })).toBe('/core/settings');
  });

  it('joins catch-all segments with slashes, encoding each part', () => {
    expect(buildHref('/docs/[...path]', { path: ['guides', 'deploy now'] })).toBe('/docs/guides/deploy%20now');
  });

  it('drops an empty optional catch-all', () => {
    expect(buildHref('/shop/[[...filters]]', {})).toBe('/shop');
    expect(buildHref('/shop/[[...filters]]', { filters: [] })).toBe('/shop');
    expect(buildHref('/shop/[[...filters]]', { filters: ['red'] })).toBe('/shop/red');
    expect(buildHref('/[[...all]]', {})).toBe('/');
  });

  it('leaves static patterns alone', () => {
    expect(buildHref('/', {})).toBe('/');
    expect(buildHref('/about', {})).toBe('/about');
  });

  it('appends search params, repeating array values and skipping empty ones', () => {
    expect(buildHref('/blog', {}, { tag: 'a&b', page: 2, draft: undefined, sort: null, ids: [1, 2] })).toBe('/blog?tag=a%26b&page=2&ids=1&ids=2');
    expect(buildHref('/blog', {}, {})).toBe('/blog');
  });

  it('throws for a missing parameter or an empty catch-all', () => {
    expect(() => buildHref('/blog/[slug]', {})).toThrow("netjes: route '/blog/[slug]' needs the parameter 'slug'");
    expect(() => buildHref('/docs/[...path]', { path: [] })).toThrow("needs the parameter 'path'");
  });
});
