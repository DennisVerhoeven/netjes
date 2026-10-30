import assert from 'node:assert/strict';
import { performance } from 'node:perf_hooks';
import { after, test } from 'node:test';
import { closedLoop, openLoop } from '../lib/load.mjs';
import { startServer } from './server.mjs';

const servers = [];
after(() => Promise.all(servers.map((s) => s.close())));

async function serve(handler, offset) {
  const started = await startServer(handler, offset);
  servers.push(started);
  return started.url;
}

test('open loop sends at the fixed rate and measures every response', async () => {
  const url = await serve((_, res) => res.end('ok'), 0);
  const { samples, maxInFlight } = await openLoop({ url }, { rate: 200, durationMs: 500 });
  assert.equal(samples.length, 100);
  assert.ok(samples.every((s) => s.status === 200 && s.bytes === 2 && s.error === null));
  // Each request is due 5 ms after the one before, whenever the previous one finished.
  samples.forEach((s, i) => assert.ok(Math.abs(s.scheduled - i * 5) < 1e-6, `request ${i} due at ${s.scheduled}`));
  assert.ok(samples.every((s) => s.sent >= s.scheduled && s.total !== null && s.total >= 0));
  assert.ok(maxInFlight >= 1);
});

test('open loop counts the requests that were due while the server stalled (no coordinated omission)', async () => {
  // Every request that arrives 100 to 400 ms into the run is answered only at 400 ms.
  let first = -1;
  const url = await serve((_, res) => {
    const now = performance.now();
    if (first < 0) first = now;
    const at = now - first;
    if (at >= 100 && at < 400) setTimeout(() => res.end('late'), 400 - at);
    else res.end('ok');
  }, 1);
  const { samples } = await openLoop({ url }, { rate: 100, durationMs: 1000 });
  assert.equal(samples.length, 100);
  // The requests due from 100 to 300 ms waited at least 100 ms each: about 20 of them, not just the one in flight.
  const slow = samples.filter((s) => /** @type {number} */ (s.total) >= 100).length;
  assert.ok(slow >= 15, `${slow} slow requests`);
});

test('a request the load generator sends late counts from when it was due, not from when it went out', async () => {
  // This server runs on the load generator's thread, so a first answer that busy-waits 40 ms holds up the requests
  // due meanwhile (one every 5 ms).
  let first = true;
  const url = await serve((_, res) => {
    if (first) {
      first = false;
      const until = performance.now() + 40;
      while (performance.now() < until);
    }
    res.end('ok');
  }, 9);
  const { samples } = await openLoop({ url }, { rate: 200, durationMs: 200 });
  assert.equal(samples.length, 40);
  const late = samples.filter((s) => s.sent - s.scheduled >= 10);
  assert.ok(late.length >= 3, `${late.length} requests sent 10 ms or more late`);
  for (const s of samples) {
    const lag = s.sent - s.scheduled;
    assert.ok(s.ttfb !== null && s.ttfb >= lag, `due at ${s.scheduled}, sent ${lag} late, ttfb ${s.ttfb}`);
    assert.ok(s.total !== null && s.total >= lag, `due at ${s.scheduled}, sent ${lag} late, total ${s.total}`);
  }
});

test('ttfb is the time to the response headers, total the time to the last byte', async () => {
  const url = await serve((_, res) => {
    res.writeHead(200, { 'content-type': 'text/html' });
    res.write('<p>shell</p>');
    setTimeout(() => res.end('<p>rest</p>'), 150);
  }, 2);
  const { samples } = await closedLoop({ url }, { concurrency: 1, durationMs: 200 });
  assert.ok(samples.length >= 1);
  for (const s of samples) {
    assert.ok(/** @type {number} */ (s.ttfb) < 100, `ttfb ${s.ttfb}`);
    // Timers may fire up to a millisecond before a high-resolution clock says they are due.
    assert.ok(/** @type {number} */ (s.total) >= 145, `total ${s.total}`);
    assert.equal(s.bytes, '<p>shell</p><p>rest</p>'.length);
  }
});

test('closed loop keeps exactly `concurrency` requests in flight', async () => {
  let inFlight = 0;
  let most = 0;
  const url = await serve((_, res) => {
    inFlight++;
    most = Math.max(most, inFlight);
    setTimeout(() => {
      inFlight--;
      res.end('ok');
    }, 20);
  }, 3);
  const { samples, maxInFlight } = await closedLoop({ url }, { concurrency: 4, durationMs: 300 });
  assert.equal(most, 4);
  assert.equal(maxInFlight, 4);
  assert.ok(samples.length >= 20, `${samples.length} samples`);
});

test('the method, headers and body of the request reach the server', async () => {
  /** @type {{ method?: string, data?: string, body: string }[]} */
  const seen = [];
  const url = await serve((req, res) => {
    let body = '';
    req.on('data', (chunk) => (body += chunk));
    req.on('end', () => {
      seen.push({ method: req.method, data: /** @type {string} */ (req.headers['x-netjes-data']), body });
      res.end();
    });
  }, 4);
  await openLoop(
    { url: new URL('/items?page=2', url), method: 'POST', headers: { 'X-Netjes-Data': '1' }, body: 'name=x' },
    { rate: 10, durationMs: 100 },
  );
  assert.deepEqual(seen, [{ method: 'POST', data: '1', body: 'name=x' }]);
});

test('failures are samples too: an error status, a refused connection, a timeout', async () => {
  const url = await serve((req, res) => {
    if (req.url === '/hang') return;
    res.statusCode = 500;
    res.end('boom');
  }, 5);
  const failed = await openLoop({ url }, { rate: 10, durationMs: 100 });
  assert.equal(failed.samples[0]?.status, 500);

  const hang = await openLoop({ url: new URL('/hang', url) }, { rate: 10, durationMs: 100, timeoutMs: 100 });
  assert.equal(hang.samples[0]?.error, 'timeout');
  assert.equal(hang.samples[0]?.status, null);

  const closed = await startServer((_, res) => res.end(), 6);
  await closed.close();
  const refused = await openLoop({ url: closed.url }, { rate: 10, durationMs: 100 });
  assert.equal(refused.samples[0]?.error, 'ECONNREFUSED');
});

test('each step reports how busy the load generator’s thread was', async () => {
  const url = await serve((_, res) => setTimeout(() => res.end('ok'), 20), 7);
  const idle = await openLoop({ url }, { rate: 10, durationMs: 300 });
  assert.ok(idle.generatorBusy >= 0 && idle.generatorBusy < 0.3, `open loop busy ${idle.generatorBusy}`);
  // This server runs on the same thread and answers at once: the closed loop keeps the thread busy throughout.
  const fast = await serve((_, res) => res.end('ok'), 8);
  const busy = await closedLoop({ url: fast }, { concurrency: 8, durationMs: 300 });
  assert.ok(busy.generatorBusy > 0.6 && busy.generatorBusy <= 1, `closed loop busy ${busy.generatorBusy}`);
});

test('an open loop can read each answer: inspect sees the status, the headers and the whole body', async () => {
  const url = await serve((req, res) => {
    if (req.url === '/hang') return;
    res.writeHead(200, { 'content-type': 'text/html' });
    res.write('<p>shell</p>');
    setTimeout(() => res.end('<p>rest</p>'), 20);
  }, 12);
  /** @type {import('../lib/load.mjs').Answer[]} */
  const seen = [];
  const inspect = (/** @type {import('../lib/load.mjs').Answer} */ answer) => {
    seen.push(answer);
    return answer.body.includes('rest') ? 'page' : 'other';
  };
  const { samples } = await openLoop({ url }, { rate: 20, durationMs: 100, inspect });
  assert.deepEqual(
    samples.map((s) => s.verdict),
    ['page', 'page'],
  );
  assert.equal(seen[0]?.status, 200);
  assert.equal(seen[0]?.headers['content-type'], 'text/html');
  assert.equal(seen[0]?.body, '<p>shell</p><p>rest</p>');
  // A request that failed has no answer to inspect; without inspect, no sample has a verdict.
  const hang = await openLoop({ url: new URL('/hang', url) }, { rate: 10, durationMs: 100, timeoutMs: 100, inspect });
  assert.equal(hang.samples[0]?.error, 'timeout');
  assert.equal(hang.samples[0]?.verdict, undefined);
  const plain = await openLoop({ url }, { rate: 20, durationMs: 100 });
  assert.ok(plain.samples.every((s) => !('verdict' in s)));
});

test('with an expectation, an answer that misses it is an error, like a wrong status', async () => {
  const url = await serve((req, res) => {
    const kind = new URL(req.url, 'http://x').searchParams.get('kind');
    if (kind === 'status') res.statusCode = 500;
    res.setHeader('content-type', kind === 'type' ? 'text/plain' : 'text/html');
    if (kind !== 'header') res.setHeader('age', '3');
    res.end(kind === 'body' ? '<p>an error page</p>' : '<p data-bench="items"></p>');
  }, 70);
  const expect = { contentType: 'text/html', contains: 'data-bench="items"', header: 'Age' };
  /** @param {string} kind */
  const run = async (kind) => {
    const target = new URL(`?kind=${kind}`, url);
    const [open, closed] = [
      await openLoop({ url: target, expect }, { rate: 100, durationMs: 100 }),
      await closedLoop({ url: target, expect }, { concurrency: 1, durationMs: 100 }),
    ];
    return [...open.samples, ...closed.samples].map((s) => s.error);
  };
  for (const [kind, error] of [['ok', null], ['body', '200 not as expected'], ['type', '200 not as expected'], ['header', '200 not as expected']]) {
    const errors = await run(kind);
    assert.ok(errors.length > 5);
    assert.ok(errors.every((e) => e === error), `${kind}: ${[...new Set(errors)].join(', ')}`);
  }
  // A wrong status stays the status's to report: the sample says nothing more than the status.
  assert.ok((await run('status')).every((e) => e === null));
});

test('an expected text split across the chunks of an answer is found', async () => {
  const url = await serve((_, res) => {
    res.write('<p data-ben');
    setTimeout(() => res.end('ch="items"></p>'), 5);
  }, 71);
  const { samples } = await openLoop({ url, expect: { contains: 'data-bench="items"' } }, { rate: 50, durationMs: 200 });
  assert.ok(samples.length >= 5);
  assert.ok(samples.every((s) => s.error === null));
});

test('an expectation that holds changes no sample', async () => {
  const url = await serve((_, res) => res.end('ok'), 72);
  const { samples } = await openLoop({ url, expect: { status: 200, contains: 'ok' } }, { rate: 100, durationMs: 100 });
  assert.ok(samples.every((s) => s.status === 200 && s.error === null && s.total !== null));
});
