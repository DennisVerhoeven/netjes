import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { performance } from 'node:perf_hooks';
import { after, test } from 'node:test';
import { FALLBACK_SHELL_MARKER } from '../lib/restart.mjs';
import { parseRoutes } from '../lib/routes.mjs';
import { runBenchmark } from '../lib/run.mjs';
import { startServer } from './server.mjs';

let down = false;
const server = await startServer((req, res) => {
  if (req.url === '/healthz') {
    res.statusCode = down ? 503 : 200;
    return res.end(down ? 'Unhealthy' : 'Healthy');
  }
  if (req.url === '/__kill') {
    down = true;
    setTimeout(() => (down = false), 300);
    return res.end();
  }
  if (req.url === '/items') {
    const data = req.headers['x-netjes-data'] === '1';
    res.setHeader('content-type', data ? 'application/json' : 'text/html');
    return setTimeout(() => res.end(data ? '{"items":[]}' : '<ul data-bench="items"></ul>'), down ? 300 : 1);
  }
  res.statusCode = 404;
  res.end();
}, 30);
after(() => server.close());

const routeList = parseRoutes(
  {
    routes: [
      { name: 'document', path: '/items', expect: { contentType: 'text/html', contains: 'data-bench="items"' } },
      { name: 'navigation', path: '/items', headers: { 'X-Netjes-Data': '1' }, expect: { contentType: 'application/json' } },
    ],
    restart: { route: 'document', health: '/healthz', healthy: 'Healthy' },
  },
  'routes.json',
);

/** @type {import('../lib/run.mjs').RunOptions} */
const base = {
  target: server.url,
  routeList,
  rates: [40, 80],
  concurrency: [2],
  warmupMs: 50,
  durationMs: 250,
  maxConnections: 16,
  timeoutMs: 5000,
  restartRate: 40,
  restartBeforeMs: 200,
  restartAfterMs: 600,
  provisional: true,
  note: 'test',
};

test('a run measures every route at every rate and concurrency, with memory and a restart', async () => {
  const lines = [];
  const results = await runBenchmark({
    ...base,
    pid: process.pid,
    kill: `"${process.execPath}" -e "fetch('${new URL('/__kill', server.url).href}')"`,
    versions: { netjes: '0.1.0' },
    settings: { sidecars: 1 },
    log: (line) => lines.push(line),
  });
  assert.equal(results.schema, 2);
  assert.equal(results.target, server.url.href);
  assert.equal(results.provisional, true);
  assert.equal(results.note, 'test');
  assert.deepEqual(results.versions, { netjes: '0.1.0' });
  assert.equal(results.settings.sidecars, 1);
  assert.deepEqual(results.settings.rates, [40, 80]);
  assert.deepEqual(
    results.routes.map((r) => [r.name, r.steps.map((s) => (s.mode === 'open' ? `open ${s.rate}` : `closed ${s.concurrency}`))]),
    [
      ['document', ['open 40', 'open 80', 'closed 2']],
      ['navigation', ['open 40', 'open 80', 'closed 2']],
    ],
  );
  const open = results.routes[0]?.steps[0];
  assert.equal(open?.requests, 10);
  assert.equal(open?.errors, 0);
  assert.ok(results.memory && (results.memory.idle.node?.rssBytes ?? 0) > 0 && (results.memory.peak.node ?? 0) > 0);
  assert.equal(results.runtimes.node, process.version);
  // Without a relaunch command, one restart: the app as it was started.
  assert.equal(results.restarts.length, 1);
  const restart = results.restarts[0];
  assert.ok(restart && restart.fallback === 'default' && restart.route === 'document' && restart.rate === 40);
  assert.ok(restart.restartMs !== null && restart.restartMs >= 250, `restart ${restart.restartMs}`);
  assert.equal(results.machine.node, process.version);
  assert.equal(results.loadAverageAfter.length, 3);
  assert.ok(lines.some((l) => l.includes('document')));
});

test('a run without a process or a kill command skips memory and the restart; --only picks routes', async () => {
  const results = await runBenchmark({ ...base, only: ['navigation'], rates: [40], concurrency: [] });
  assert.deepEqual(results.routes.map((r) => r.name), ['navigation']);
  assert.equal(results.routes[0]?.steps.length, 1);
  assert.equal(results.memory, null);
  assert.deepEqual(results.restarts, []);
  assert.deepEqual(results.runtimes, {});
});

test('a route that does not answer as expected stops the run before any load', async () => {
  const broken = parseRoutes({ routes: [{ name: 'missing', path: '/nope', expect: { contentType: 'text/html' } }] }, 'x');
  await assert.rejects(runBenchmark({ ...base, routeList: broken }), /missing \(GET \/nope\): status 404, expected 200/);
});

test('an unknown route in --only is an error', async () => {
  await assert.rejects(runBenchmark({ ...base, only: ['nope'] }), /--only: no route named "nope"/);
});

test('a server that answers fewer requests than arrive gets a warning', async () => {
  const results = await runBenchmark({
    ...base,
    routeList: parseRoutes({ routes: [{ name: 'slow', path: '/items', expect: {} }] }, 'x'),
    // The fake answers in a millisecond or more; one connection cannot carry 2000 requests a second.
    rates: [2000],
    concurrency: [],
    maxConnections: 1,
    durationMs: 200,
  });
  assert.ok(results.warnings.some((w) => /slow at 2000\/s: answered [\d.]+\/s/.test(w)), results.warnings.join('\n'));
});

test('a load generator that was the bottleneck gets a warning', async () => {
  // The fake server shares this thread and answers its health check at once, so a closed loop keeps it busy.
  const results = await runBenchmark({
    ...base,
    routeList: parseRoutes({ routes: [{ name: 'health', path: '/healthz' }] }, 'x'),
    rates: [],
    concurrency: [8],
    durationMs: 300,
  });
  assert.ok(
    results.warnings.some((w) => /health with 8 connections: the load generator was busy \d+% of the step/.test(w)),
    results.warnings.join('\n'),
  );
});

test('a load generator that sent requests late gets a warning', async () => {
  // This server shares the load generator's thread. The route check takes the first two requests; the third, the
  // step's first, busy-waits 40 ms and holds up the requests due meanwhile (one every 5 ms).
  let seen = 0;
  const stalling = await startServer((_, res) => {
    if (++seen === 3) {
      const until = performance.now() + 40;
      while (performance.now() < until);
    }
    res.end('ok');
  }, 32);
  try {
    const results = await runBenchmark({
      ...base,
      target: stalling.url,
      routeList: parseRoutes({ routes: [{ name: 'stall', path: '/' }] }, 'x'),
      rates: [200],
      concurrency: [],
      warmupMs: 0,
      durationMs: 200,
    });
    assert.ok(
      results.warnings.some((w) => /^stall at 200\/s: the load generator sent requests up to [\d.]+ ms late \(p99\)/.test(w)),
      results.warnings.join('\n'),
    );
  } finally {
    await stalling.close();
  }
});

test('the idle check uses the load average from before the build when there is one', async () => {
  // run.sh builds and starts the app first, which alone lifts the 1-minute load average at the start.
  const quiet = await runBenchmark({ ...base, only: ['navigation'], rates: [40], concurrency: [], loadBeforeBuild: 0.25 });
  assert.equal(quiet.loadAverageBeforeBuild, 0.25);
  assert.ok(!quiet.warnings.some((w) => w.includes('not idle')), quiet.warnings.join('\n'));
  const busy = await runBenchmark({ ...base, only: ['navigation'], rates: [40], concurrency: [], loadBeforeBuild: 3 });
  assert.ok(busy.warnings.includes('the machine was not idle: its load average was 3.00 before the build'), busy.warnings.join('\n'));
  const direct = await runBenchmark({ ...base, only: ['navigation'], rates: [40], concurrency: [] });
  assert.equal(direct.loadAverageBeforeBuild, null);
});

test('a run can spread its load over several threads', async () => {
  const results = await runBenchmark({ ...base, only: ['document'], rates: [40], concurrency: [2], workers: 2 });
  assert.equal(results.settings.workers, 2);
  assert.equal(results.routes[0]?.steps[0]?.requests, 10);
  assert.ok((results.routes[0]?.steps[1]?.requests ?? 0) > 10);
});

test('a kill command that fails leaves the restart out with a warning and keeps the measured routes', async () => {
  const lines = [];
  const results = await runBenchmark({
    ...base,
    only: ['document'],
    rates: [40],
    concurrency: [],
    restartBeforeMs: 100,
    restartAfterMs: 200,
    kill: 'echo "no such process" >&2; exit 3',
    log: (line) => lines.push(line),
  });
  assert.equal(results.routes[0]?.steps[0]?.requests, 10);
  assert.deepEqual(results.restarts, []);
  const warning = 'the kill command `echo "no such process" >&2; exit 3` exited with 3: no such process, so the restart was not measured';
  assert.ok(results.warnings.includes(warning), results.warnings.join('\n'));
  assert.ok(lines.includes(`restart: ${warning}`), lines.join('\n'));
});

test('a step that throws still stops the memory sampler, so the process can exit', async () => {
  // In a process of its own: a sampler thread left running would keep it alive.
  const options = { ...base, target: undefined, routeList: undefined, rates: [40], concurrency: [], warmupMs: 0, durationMs: 100 };
  const script = `
    import { parseRoutes } from ${JSON.stringify(new URL('../lib/routes.mjs', import.meta.url).href)};
    import { runBenchmark } from ${JSON.stringify(new URL('../lib/run.mjs', import.meta.url).href)};
    import { startServer } from ${JSON.stringify(new URL('./server.mjs', import.meta.url).href)};
    const server = await startServer((_, res) => res.end('ok'), 31);
    const routeList = parseRoutes({ routes: [{ name: 'page', path: '/' }] }, 'x');
    // The first line is logged after the first step, while the sampler runs.
    const log = () => {
      throw new Error('the step failed');
    };
    await runBenchmark({ ...${JSON.stringify(options)}, target: server.url, routeList, pid: process.pid, log }).catch((error) =>
      console.log(error.message),
    );
    await server.close();
  `;
  const child = spawn(process.execPath, ['-e', script], { stdio: ['ignore', 'pipe', 'inherit'] });
  let out = '';
  child.stdout.on('data', (chunk) => (out += chunk));
  const timer = setTimeout(() => child.kill('SIGKILL'), 10_000);
  const [code, signal] = await once(child, 'exit');
  clearTimeout(timer);
  assert.equal(signal, null, 'the process was still running after 10 s');
  assert.equal(code, 0);
  assert.equal(out.trim(), 'the step failed');
});

test('--only leaves out the restart when its route is not among the chosen', async () => {
  const results = await runBenchmark({
    ...base,
    only: ['navigation'],
    rates: [40],
    concurrency: [],
    kill: `"${process.execPath}" -e "fetch('${new URL('/__kill', server.url).href}')"`,
  });
  assert.deepEqual(results.restarts, []);
});

/**
 * Like a Netjes app in production: while its renderer is down it answers with the fallback shell at once, until
 * /__relaunch starts it "again" with the fallback off, after which requests wait for the restart. It counts the
 * requests for pages, and how many had come when it was relaunched.
 */
async function netjesLike() {
  const state = { down: false, fallback: true, relaunches: 0, broken: false, requests: 0, requestsAtRelaunch: 0 };
  const app = await startServer((req, res) => {
    if (req.url === '/healthz') {
      res.statusCode = state.down ? 503 : 200;
      return res.end(state.down ? 'Unhealthy' : 'Healthy');
    }
    if (req.url === '/__kill') {
      state.down = true;
      setTimeout(() => (state.down = false), 300);
      return res.end();
    }
    if (req.url?.startsWith('/__relaunch')) {
      state.relaunches++;
      state.requestsAtRelaunch = state.requests;
      state.fallback = false;
      state.broken = req.url.endsWith('broken=1');
      return res.end();
    }
    state.requests++;
    res.setHeader('content-type', 'text/html');
    if (state.broken) {
      res.statusCode = 500;
      return res.end('<ul data-bench="items"></ul>');
    }
    if (state.down && state.fallback) return res.end(`<html><body><script>{}</script>${FALLBACK_SHELL_MARKER}</body></html>`);
    setTimeout(() => res.end('<ul data-bench="items"></ul>'), state.down ? 300 : 1);
  }, 33);
  const command = (/** @type {string} */ path) => `"${process.execPath}" -e "fetch('${new URL(path, app.url).href}')"`;
  return { app, state, command };
}

test('with a relaunch command the restart is measured again, with the client-rendered fallback off', async () => {
  const { app, state, command } = await netjesLike();
  try {
    /** @type {string[]} */
    const lines = [];
    let requestsBeforeSecondRestart = 0;
    const results = await runBenchmark({
      ...base,
      target: app.url,
      only: ['document'],
      rates: [],
      concurrency: [],
      kill: command('/__kill'),
      relaunchWithoutFallback: command('/__relaunch'),
      log: (line) => {
        lines.push(line);
        if (line.startsWith('restart with ClientRenderFallback off: document at')) requestsBeforeSecondRestart = state.requests;
      },
    });
    assert.equal(state.relaunches, 1);
    assert.deepEqual(
      results.restarts.map((r) => r.fallback),
      ['default', 'off'],
    );
    const [on, off] = /** @type {import('../lib/report.mjs').Results['restarts']} */ (results.restarts);
    // With the fallback the outage was answered with shells; without it the requests waited for the page.
    assert.ok(on && on.shells.count >= 5, `${on?.shells.count} shells with the fallback`);
    assert.ok(on.firstOkAfterKillMs !== null && on.firstOkAfterKillMs >= 250, `first page ${on.firstOkAfterKillMs}`);
    assert.ok(off && off.shells.count === 0, `${off?.shells.count} shells without the fallback`);
    assert.ok(off.firstOkAfterKillMs !== null && off.firstOkAfterKillMs >= 250, `first page ${off.firstOkAfterKillMs}`);
    assert.ok(/** @type {number} */ (off.after.max) >= 250, `after max ${off.after.max}`);
    assert.deepEqual(results.warnings.filter((w) => w.includes('restart')), []);
    assert.ok(lines.some((l) => l.includes('ClientRenderFallback off')), lines.join('\n'));
    // Before its restart the relaunched app was checked (two requests) and warmed up, as at the start of the run: the
    // warm-up's 8 connections send at least one request each.
    const sinceRelaunch = requestsBeforeSecondRestart - state.requestsAtRelaunch;
    assert.ok(sinceRelaunch >= 2 + 8, `${sinceRelaunch} requests between the relaunch and its restart`);
  } finally {
    await app.close();
  }
});

test('a relaunch that fails, or an app that answers wrongly after it, leaves the second restart out with a warning', async () => {
  const { app, command } = await netjesLike();
  try {
    const options = { ...base, target: app.url, only: ['document'], rates: [], concurrency: [], kill: command('/__kill') };
    const failed = await runBenchmark({ ...options, relaunchWithoutFallback: 'echo "port in use" >&2; exit 2' });
    assert.deepEqual(
      failed.restarts.map((r) => r.fallback),
      ['default'],
    );
    const exited =
      'the relaunch command `echo "port in use" >&2; exit 2` exited with 2: port in use, so the restart with ClientRenderFallback off was not measured';
    assert.ok(failed.warnings.includes(exited), failed.warnings.join('\n'));

    // The app is checked again after the relaunch, as before the run: a restart of an error page measures nothing.
    const wrong = await runBenchmark({ ...options, relaunchWithoutFallback: command('/__relaunch?broken=1') });
    assert.deepEqual(
      wrong.restarts.map((r) => r.fallback),
      ['default'],
    );
    const unexpected =
      'after the relaunch, document (GET /items): status 500, expected 200, so the restart with ClientRenderFallback off was not measured';
    assert.ok(wrong.warnings.includes(unexpected), wrong.warnings.join('\n'));
  } finally {
    await app.close();
  }
});

test('an answer during the load that misses the route’s expectations is an error, not a good answer', async () => {
  // The route's two checks before the load pass; every answer after them is an error page that still says 200.
  let served = 0;
  const flaky = await startServer((_, res) => {
    res.setHeader('content-type', 'text/html');
    res.end(served++ < 2 ? '<ul data-bench="items"></ul>' : '<p>Something went wrong</p>');
  }, 73);
  try {
    const routes = parseRoutes(
      { routes: [{ name: 'document', path: '/items', expect: { contentType: 'text/html', contains: 'data-bench="items"' } }] },
      'x',
    );
    const results = await runBenchmark({ ...base, target: flaky.url, routeList: routes, rates: [40], concurrency: [2], warmupMs: 0 });
    const steps = results.routes[0].steps;
    assert.equal(steps.length, 2);
    for (const step of steps) {
      assert.ok(step.requests > 0);
      assert.equal(step.ok, 0);
      assert.equal(step.errors, step.requests);
      assert.deepEqual(step.errorKinds, { '200 not as expected': step.requests });
    }
  } finally {
    await flaky.close();
  }
});
