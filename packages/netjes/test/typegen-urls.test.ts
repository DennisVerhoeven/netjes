// @vitest-environment node
// The generated endpoint URLs (pinned by tests/Netjes.Tests/TypeGen/GoldenFileTests.cs) with buildEndpointUrl.
// Typechecking this file also proves that the types refuse what the endpoints would not bind.
import { describe, expect, it } from 'vitest';
import { endpointUrl } from './fixtures/typegen/urls.generated.ts';

describe('generated endpoint URLs', () => {
  it('fill the route values and send the other values as the query string', () => {
    expect(endpointUrl('GET /api/posts/{slug}/likes/', { slug: 'a b' })).toBe('/api/posts/a%20b/likes');
    expect(endpointUrl('POST /api/posts/{slug}/likes/', { slug: 'x' })).toBe('/api/posts/x/likes');
    expect(endpointUrl('GET /api/search', { q: 'a b', state: 'InProgress', tag: ['x', 'y'], take: null })).toBe('/api/search?q=a+b&state=InProgress&tag=x&tag=y');
    expect(endpointUrl('GET /api/search', { state: 1, page: 2 })).toBe('/api/search?state=1&page=2');
    expect(endpointUrl('GET /api/search')).toBe('/api/search');
    expect(endpointUrl('GET /docs/{**path:regex(\\.md$)}', { path: 'guides/routing.md' })).toBe('/docs/guides/routing.md');
    expect(endpointUrl('GET /files/{*name}', { name: 'a/b' })).toBe('/files/a%2Fb');
    expect(endpointUrl('GET /opt/{id?}')).toBe('/opt');
    expect(endpointUrl('GET /opt/{id?}', { id: 3 })).toBe('/opt/3');
    expect(endpointUrl('GET /list/{page=1}', { page: 2 })).toBe('/list/2');
    expect(endpointUrl('GET /openapi/{documentName}.json', { documentName: 'v1' })).toBe('/openapi/v1.json');
    expect(endpointUrl('GET /feed.xml')).toBe('/feed.xml');
  });

  it('take query values the handler does not name in the third argument', () => {
    expect(endpointUrl('GET /api/search', { q: 'a' }, { utm_source: 'mail', q: 'b' })).toBe('/api/search?q=a&utm_source=mail&q=b');
    expect(endpointUrl('GET /feed.xml', undefined, { format: 'atom' })).toBe('/feed.xml?format=atom');
  });

  it('are refused, as types, for a key no endpoint has, without a route value and with a name only the JSON writes', () => {
    // @ts-expect-error: no endpoint has this key.
    expect(() => endpointUrl('GET /api/nothing')).toThrow();
    // @ts-expect-error: the route needs the slug.
    expect(() => endpointUrl('GET /api/posts/{slug}/likes/', {})).toThrow("netjes: endpoint 'GET /api/posts/{slug}/likes/' needs the route value 'slug'");
    // @ts-expect-error: a value the handler binds is typed, here as the C# names of the enum and its numbers.
    expect(endpointUrl('GET /api/search', { state: 'inProgress' })).toBe('/api/search?state=inProgress');
  });
});
