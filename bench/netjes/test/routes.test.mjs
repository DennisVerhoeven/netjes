import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { checkRoute, parseRoutes, toRequest } from '../lib/routes.mjs';
import { startServer } from './server.mjs';

test('a route list parses, with defaults for method, headers and expect', () => {
  const list = parseRoutes(
    {
      routes: [
        { name: 'document', path: '/items', expect: { contentType: 'text/html', contains: 'Items' } },
        { name: 'navigation', path: '/items', headers: { 'X-Netjes-Data': '1' }, expect: { status: 200 } },
        { name: 'post', path: '/form', method: 'POST', body: 'a=1', headers: { 'content-type': 'application/x-www-form-urlencoded' } },
      ],
      restart: { route: 'document', health: '/healthz', healthy: 'Healthy' },
    },
    'routes.json',
  );
  assert.deepEqual(list.routes[0], {
    name: 'document',
    path: '/items',
    method: 'GET',
    headers: {},
    expect: { contentType: 'text/html', contains: 'Items' },
  });
  assert.equal(list.routes[2]?.method, 'POST');
  assert.equal(list.routes[2]?.body, 'a=1');
  assert.deepEqual(list.restart, { route: 'document', health: '/healthz', healthy: 'Healthy' });
});

test('a route list without restart has none', () => {
  assert.equal(parseRoutes({ routes: [{ name: 'a', path: '/' }] }, 'x').restart, null);
});

test('a route list names every problem, with the file it came from', () => {
  assert.throws(
    () =>
      parseRoutes(
        {
          routes: [{ name: 'a', path: 'items' }, { path: '/b' }, { name: 'a', path: '/c' }, { name: 'd', path: '/d', expect: { status: '200' } }],
          restart: { route: 'missing' },
        },
        'routes.json',
      ),
    (error) => {
      assert.ok(error instanceof Error);
      assert.match(error.message, /^routes\.json:/);
      assert.match(error.message, /routes\[0\]\.path must start with \//);
      assert.match(error.message, /routes\[1\]\.name/);
      assert.match(error.message, /routes\[2\]: "a" is listed twice/);
      assert.match(error.message, /routes\[3\]\.expect\.status must be a number/);
      assert.match(error.message, /restart\.route "missing" is not one of the routes/);
      return true;
    },
  );
  assert.throws(() => parseRoutes({ routes: [] }, 'empty.json'), /empty\.json: routes must be a non-empty array/);
});

test('a route resolves below the target, keeping its base path and query', () => {
  const [route] = parseRoutes({ routes: [{ name: 'a', path: '/items?page=2', headers: { 'X-A': '1' } }] }, 'x').routes;
  assert.ok(route);
  assert.equal(toRequest(new URL('http://127.0.0.1:5480'), route).url.href, 'http://127.0.0.1:5480/items?page=2');
  assert.equal(toRequest(new URL('http://127.0.0.1:5480/base/'), route).url.href, 'http://127.0.0.1:5480/base/items?page=2');
  assert.equal(toRequest(new URL('http://127.0.0.1:5480/base'), route).url.href, 'http://127.0.0.1:5480/base/items?page=2');
  assert.deepEqual(toRequest(new URL('http://127.0.0.1:5480'), route).headers, { 'X-A': '1' });
});

const servers = [];
after(() => Promise.all(servers.map((s) => s.close())));

test('checking a route reports each expectation the answer misses, on the second request', async () => {
  let calls = 0;
  const server = await startServer((req, res) => {
    calls++;
    if (req.url === '/cached') {
      // Only the second answer comes from the cache.
      if (calls % 2 === 0) res.setHeader('age', '0');
      res.setHeader('content-type', 'text/html; charset=utf-8');
      return res.end('<p>Items</p>');
    }
    res.statusCode = 404;
    res.setHeader('content-type', 'application/json');
    res.end('{}');
  }, 10);
  servers.push(server);
  const routes = parseRoutes(
    {
      routes: [
        { name: 'cached', path: '/cached', expect: { contentType: 'text/html', contains: 'Items', header: 'Age' } },
        { name: 'broken', path: '/nope', expect: { contentType: 'text/html', contains: 'Items', header: 'age' } },
      ],
    },
    'x',
  ).routes;
  assert.deepEqual(await checkRoute(server.url, /** @type {any} */ (routes[0])), []);
  assert.deepEqual(await checkRoute(server.url, /** @type {any} */ (routes[1])), [
    'status 404, expected 200',
    'content-type application/json, expected text/html',
    'the body does not contain "Items"',
    'no age header',
  ]);
  const unreachable = await startServer((_, res) => res.end(), 11);
  await unreachable.close();
  assert.deepEqual(await checkRoute(unreachable.url, /** @type {any} */ (routes[0])), ['request failed: ECONNREFUSED']);
});
