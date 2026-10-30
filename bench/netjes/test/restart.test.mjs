import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { after, test } from 'node:test';
import { FALLBACK_SHELL_MARKER, restartUnderLoad } from '../lib/restart.mjs';
import { startServer } from './server.mjs';

const servers = [];
after(() => Promise.all(servers.map((s) => s.close())));

const PAGE = '<ul data-bench="items"><li>Item 7 in stock</li></ul>';
/** What Netjes sends when no sidecar is ready: the payload (which may hold the page's text) and no page. */
const SHELL =
  '<!DOCTYPE html><html lang="en"><head></head><body><script id="__NETJES_DATA__" type="application/json">' +
  '{"name":"Item 7 in stock"}</script><!-- No Node sidecar was ready: this page renders in the browser. --></body></html>';

/**
 * Like a Netjes app whose sidecar died: renders in flight fail, new ones wait for the restart, health says Unhealthy.
 * @param {number} offset
 * @param {number} outageMs
 * @param {{ downStatus?: number, downBody?: string, inFlight?: 'fail' | 'fail-shell' | 'hold', whileDown?: 'wait' | 'shell' | 'other' }} [options]
 *   the health status and body while down (ASP.NET Core answers 200 for Degraded), what happens to the renders in
 *   flight at the kill (they fail with a 500, fail with a 500 whose error page goes out as the shell, or finish once
 *   the app is back), and to requests that arrive while down: they wait for the restart, get the client-rendered
 *   fallback's shell at once, or some other 200 at once
 */
async function fakeApp(offset, outageMs, { downStatus = 503, downBody = 'Unhealthy', inFlight = 'fail', whileDown = 'wait' } = {}) {
  let down = false;
  /** @type {(() => void)[]} */
  let waiting = [];
  /** @type {Set<import('node:http').ServerResponse>} */
  const rendering = new Set();
  const server = await startServer((req, res) => {
    if (req.url === '/healthz') {
      res.statusCode = down ? downStatus : 200;
      return res.end(down ? downBody : 'Healthy');
    }
    res.setHeader('content-type', 'text/html; charset=utf-8');
    const render = () => {
      rendering.add(res);
      setTimeout(() => {
        if (!rendering.delete(res)) return;
        res.end(PAGE);
      }, 20);
    };
    if (!down) render();
    else if (whileDown === 'shell') res.end(SHELL);
    else if (whileDown === 'other') res.end('<p>Down for maintenance</p>');
    else waiting.push(render);
  }, offset);
  servers.push(server);
  const kill = async () => {
    down = true;
    for (const res of rendering) {
      if (inFlight === 'hold') {
        waiting.push(() => res.end(PAGE));
        continue;
      }
      res.statusCode = 500;
      res.end(inFlight === 'fail-shell' ? SHELL : 'render failed');
    }
    rendering.clear();
    setTimeout(() => {
      down = false;
      const queued = waiting;
      waiting = [];
      for (const render of queued) render();
    }, outageMs);
  };
  return { url: server.url, kill };
}

test('a restart under load reports how long the app was down and what the requests saw', async () => {
  const app = await fakeApp(20, 300);
  const result = await restartUnderLoad(
    { url: new URL('/page', app.url) },
    { rate: 100, beforeMs: 300, afterMs: 900, kill: app.kill, health: { url: new URL('/healthz', app.url), healthy: 'Healthy' } },
  );
  assert.ok(Math.abs(result.killAtMs - 300) < 50, `kill at ${result.killAtMs}`);
  assert.ok(result.restartMs !== null && result.restartMs >= 280 && result.restartMs < 700, `restart ${result.restartMs}`);
  assert.ok(
    result.firstOkAfterKillMs !== null && result.firstOkAfterKillMs >= 280 && result.firstOkAfterKillMs < 700,
    `first ok after ${result.firstOkAfterKillMs}`,
  );
  // Two renders are in flight at any time (20 ms each, one every 10 ms); both fail at the kill.
  assert.ok(result.failed >= 1 && result.failed <= 4, `${result.failed} failed`);
  assert.equal(result.errorKinds['500'], result.failed);
  assert.equal(result.requests, 120);
  assert.ok(result.before.count >= 25);
  // The requests that waited for the restart are charged for the wait.
  assert.ok(/** @type {number} */ (result.after.max) >= 250, `after max ${result.after.max}`);
  assert.ok(/** @type {number} */ (result.before.p99) < 200, `before p99 ${result.before.p99}`);
});

test('without a health URL the restart time is not known', async () => {
  const app = await fakeApp(21, 100);
  const result = await restartUnderLoad({ url: new URL('/page', app.url) }, { rate: 50, beforeMs: 100, afterMs: 400, kill: app.kill });
  assert.equal(result.restartMs, null);
  assert.ok(result.firstOkAfterKillMs !== null);
});

test('health counts as healthy only when the body is exactly the healthy text', async () => {
  // A status of 200 alone does not say healthy, and neither does a body that merely contains the word.
  const app = await fakeApp(22, 300, { downStatus: 200, downBody: 'Not Healthy' });
  const result = await restartUnderLoad(
    { url: new URL('/page', app.url) },
    { rate: 50, beforeMs: 100, afterMs: 600, kill: app.kill, health: { url: new URL('/healthz', app.url), healthy: 'Healthy' } },
  );
  assert.ok(result.restartMs !== null && result.restartMs >= 280, `restart ${result.restartMs}`);
});

test('requests in flight at the kill count after it, not in the baseline', async () => {
  // Renders in flight at the kill finish only once the app is back, 300 ms later.
  const app = await fakeApp(23, 300, { inFlight: 'hold' });
  const result = await restartUnderLoad(
    { url: new URL('/page', app.url) },
    { rate: 100, beforeMs: 300, afterMs: 600, kill: app.kill, health: { url: new URL('/healthz', app.url), healthy: 'Healthy' } },
  );
  assert.equal(result.failed, 0);
  assert.ok(/** @type {number} */ (result.before.max) < 150, `before max ${result.before.max}`);
  assert.ok(/** @type {number} */ (result.after.max) >= 250, `after max ${result.after.max}`);
  assert.equal(result.before.count + result.after.count, result.requests);
});

test('a kill that fails ends the load and the health polling before its error comes back', async () => {
  let polls = 0;
  let requests = 0;
  const server = await startServer((req, res) => {
    if (req.url === '/healthz') polls++;
    else requests++;
    res.end('Healthy');
  }, 25);
  servers.push(server);
  await assert.rejects(
    restartUnderLoad(
      { url: new URL('/page', server.url) },
      {
        rate: 50,
        beforeMs: 100,
        afterMs: 200,
        kill: async () => {
          throw new Error('no renderer to kill');
        },
        health: { url: new URL('/healthz', server.url), healthy: 'Healthy' },
        pollMs: 10,
      },
    ),
    /no renderer to kill/,
  );
  // The whole schedule ran, and nothing is sent once the error is back.
  assert.equal(requests, 15);
  const seen = { polls, requests };
  await new Promise((resolve) => setTimeout(resolve, 100));
  assert.deepEqual({ polls, requests }, seen);
});

test('the kill counts from when the kill command has finished, not from when it started', async () => {
  // A kill command takes a while (a shell, pgrep); the app keeps answering until it has run.
  const app = await fakeApp(24, 300);
  const result = await restartUnderLoad(
    { url: new URL('/page', app.url) },
    {
      rate: 100,
      beforeMs: 200,
      afterMs: 700,
      kill: async () => {
        await new Promise((resolve) => setTimeout(resolve, 100));
        await app.kill();
      },
      health: { url: new URL('/healthz', app.url), healthy: 'Healthy' },
    },
  );
  assert.ok(Math.abs(result.killAtMs - 300) < 50, `kill at ${result.killAtMs}`);
  assert.ok(result.firstOkAfterKillMs !== null && result.firstOkAfterKillMs >= 250, `first ok after ${result.firstOkAfterKillMs}`);
});

const expect = { contentType: 'text/html', contains: 'Item 7 in stock' };

test('fallback shells are counted apart: the first good answer is the first page after the restart', async () => {
  // While down the app answers at once with the shell, whose payload holds the text the page is checked for.
  const app = await fakeApp(26, 300, { whileDown: 'shell' });
  const result = await restartUnderLoad(
    { url: new URL('/page', app.url) },
    { rate: 100, beforeMs: 300, afterMs: 900, kill: app.kill, health: { url: new URL('/healthz', app.url), healthy: 'Healthy' }, expect },
  );
  assert.ok(
    result.firstOkAfterKillMs !== null && result.firstOkAfterKillMs >= 280 && result.firstOkAfterKillMs < 700,
    `first ok after ${result.firstOkAfterKillMs}`,
  );
  // About one request every 10 ms got the shell during the 300 ms outage.
  assert.ok(result.shells.count >= 20 && result.shells.count <= 35, `${result.shells.count} shells`);
  assert.ok(/** @type {number} */ (result.shells.p99) < 100, `shell p99 ${result.shells.p99}`);
  // Only the renders in flight at the kill failed; the shells are not failures, and not in the latencies after.
  assert.ok(result.failed >= 1 && result.failed <= 4, `${result.failed} failed`);
  assert.deepEqual(result.errorKinds, { 500: result.failed });
  assert.equal(result.before.count + result.after.count + result.shells.count + result.failed, result.requests);
});

test('a render in flight at the kill counts as failed by its status, even when its error page went out as a shell', async () => {
  // No sidecar is ready when the render fails, so Netjes sends its 500 error page as the shell too.
  const app = await fakeApp(28, 300, { inFlight: 'fail-shell', whileDown: 'shell' });
  const result = await restartUnderLoad(
    { url: new URL('/page', app.url) },
    { rate: 100, beforeMs: 300, afterMs: 900, kill: app.kill, health: { url: new URL('/healthz', app.url), healthy: 'Healthy' }, expect },
  );
  // Two renders are in flight at any time (20 ms each, one every 10 ms); both fail at the kill.
  assert.ok(result.failed >= 1 && result.failed <= 4, `${result.failed} failed: ${JSON.stringify(result.errorKinds)}`);
  assert.deepEqual(result.errorKinds, { 500: result.failed });
  // The shells are the 200s answered during the outage, about one every 10 ms for 300 ms.
  assert.ok(result.shells.count >= 20 && result.shells.count <= 35, `${result.shells.count} shells`);
  assert.equal(result.before.count + result.after.count + result.shells.count + result.failed, result.requests);
});

test('an answer with the right status that is neither the page nor a shell counts as failed', async () => {
  const app = await fakeApp(27, 300, { whileDown: 'other' });
  const result = await restartUnderLoad(
    { url: new URL('/page', app.url) },
    { rate: 100, beforeMs: 300, afterMs: 900, kill: app.kill, health: { url: new URL('/healthz', app.url), healthy: 'Healthy' }, expect },
  );
  assert.equal(result.shells.count, 0);
  const wrong = result.errorKinds['200 not as expected'] ?? 0;
  assert.ok(wrong >= 20 && wrong <= 35, `${wrong} wrong answers: ${JSON.stringify(result.errorKinds)}`);
  assert.equal(result.failed, wrong + (result.errorKinds['500'] ?? 0));
  assert.ok(result.firstOkAfterKillMs !== null && result.firstOkAfterKillMs >= 280, `first ok after ${result.firstOkAfterKillMs}`);
});

test('the fallback shell marker is the one Netjes writes', async () => {
  const shell = await readFile(new URL('../../../src/Netjes/Rendering/ClientRenderShell.cs', import.meta.url), 'utf8');
  assert.ok(shell.includes(FALLBACK_SHELL_MARKER), FALLBACK_SHELL_MARKER);
  assert.ok(SHELL.includes(FALLBACK_SHELL_MARKER));
});
