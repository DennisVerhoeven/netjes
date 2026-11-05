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

  it('appends an encoded fragment', () => {
    expect(buildHref('/docs/[...path]', { path: ['guides', 'caching'] }, undefined, { hash: 'keys' })).toBe('/docs/guides/caching#keys');
    expect(buildHref('/blog', {}, { tag: 'a' }, { hash: 'top' })).toBe('/blog?tag=a#top');
    expect(buildHref('/', {}, undefined, { hash: 'start' })).toBe('/#start');
    expect(buildHref('/blog', {}, undefined, { hash: 'a b' })).toBe('/blog#a%20b');
    // After the path and the query, each encoded on its own: a space is %20 in a path and a fragment, + in a query.
    expect(buildHref('/blog/[slug]', { slug: 'a b' }, { q: 'x y' }, { hash: 'c d' })).toBe('/blog/a%20b?q=x+y#c%20d');
  });

  it('drops one leading # from the hash', () => {
    expect(buildHref('/blog', {}, undefined, { hash: '#top' })).toBe('/blog#top');
    expect(buildHref('/blog', {}, { tag: 'a' }, { hash: '#top' })).toBe('/blog?tag=a#top');
    expect(buildHref('/blog', {}, undefined, { hash: '##top' })).toBe('/blog#%23top');
  });

  it('encodes every character that could end or change the fragment', () => {
    expect(buildHref('/blog', {}, undefined, { hash: 'a/b?c:d&e=f#g' })).toBe('/blog#a%2Fb%3Fc%3Ad%26e%3Df%23g');
    expect(buildHref('/blog', {}, undefined, { hash: 'line\nbreak' })).toBe('/blog#line%0Abreak');
    expect(buildHref('/blog', {}, undefined, { hash: 'über-uns' })).toBe('/blog#%C3%BCber-uns');
    // Whatever the hash holds stays in the fragment of this path; it never starts another URL.
    expect(buildHref('/blog', {}, undefined, { hash: '//evil.example/x' })).toBe('/blog#%2F%2Fevil.example%2Fx');
  });

  it('adds no fragment for an empty or missing hash', () => {
    for (const options of [undefined, {}, { hash: undefined }, { hash: '' }, { hash: '#' }]) {
      expect(buildHref('/blog', {}, undefined, options), JSON.stringify(options)).toBe('/blog');
      expect(buildHref('/blog', {}, { tag: 'a' }, options), JSON.stringify(options)).toBe('/blog?tag=a');
    }
    expect(buildHref('/blog', {})).toBe('/blog');
  });

  it('throws for a missing parameter or an empty catch-all', () => {
    expect(() => buildHref('/blog/[slug]', {})).toThrow("netjes: route '/blog/[slug]' needs the parameter 'slug'");
    expect(() => buildHref('/docs/[...path]', { path: [] })).toThrow("needs the parameter 'path'");
  });
});
