import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { after, test } from 'node:test';
import { parseCli } from '../bench.mjs';
import { startServer } from './server.mjs';

const BENCH = fileURLToPath(new URL('../bench.mjs', import.meta.url));
const required = ['--target', 'http://127.0.0.1:5480', '--routes', 'routes.json'];

test('the command line has defaults for everything but the target and the routes', () => {
  const options = parseCli(required);
  assert.equal(options.target.href, 'http://127.0.0.1:5480/');
  assert.equal(options.routesFile, 'routes.json');
  assert.deepEqual(options.rates, [25, 100, 400]);
  assert.deepEqual(options.concurrency, [1, 8, 32]);
  assert.equal(options.warmupMs, 10_000);
  assert.equal(options.durationMs, 15_000);
  assert.equal(options.maxConnections, 256);
  assert.equal(options.workers, 4);
  assert.equal(options.timeoutMs, 30_000);
  assert.equal(options.pid, null);
  assert.equal(options.kill, null);
  assert.equal(options.relaunchWithoutFallback, null);
  assert.equal(options.restartRate, 50);
  assert.equal(options.restartBeforeMs, 5_000);
  assert.equal(options.restartAfterMs, 15_000);
  assert.equal(options.provisional, false);
  assert.equal(options.note, null);
  assert.equal(options.out, null);
  assert.equal(options.meta, null);
  assert.equal(options.only, null);
  assert.equal(options.loadBeforeBuild, null);
});

test('lists, seconds and the quick preset parse; an empty list skips that kind of step', () => {
  const options = parseCli([
    ...required,
    '--rates', '10,20',
    '--concurrency', '',
    '--warmup', '0.5',
    '--duration', '2',
    '--pid', '1234',
    '--workers', '2',
    '--kill', 'kill -9 1',
    '--relaunch-without-fallback', 'run.sh --relaunch-without-fallback',
    '--only', 'document,cached',
    '--provisional',
    '--note', 'smoke',
    '--out', 'results',
    '--meta', 'meta.json',
    '--load-before-build', '0.42',
  ]);
  assert.deepEqual(options.rates, [10, 20]);
  assert.deepEqual(options.concurrency, []);
  assert.equal(options.warmupMs, 500);
  assert.equal(options.durationMs, 2000);
  assert.equal(options.pid, 1234);
  assert.equal(options.workers, 2);
  assert.equal(options.kill, 'kill -9 1');
  assert.equal(options.relaunchWithoutFallback, 'run.sh --relaunch-without-fallback');
  assert.deepEqual(options.only, ['document', 'cached']);
  assert.equal(options.provisional, true);
  assert.equal(options.note, 'smoke');
  assert.equal(options.loadBeforeBuild, 0.42);
  const quick = parseCli([...required, '--quick', '--duration', '4']);
  assert.equal(quick.warmupMs, 1000);
  // An explicit value wins over the preset.
  assert.equal(quick.durationMs, 4000);
  assert.equal(quick.restartBeforeMs, 2000);
  assert.equal(quick.restartAfterMs, 5000);
});

test('bad command lines are refused with a reason', () => {
  assert.throws(() => parseCli(['--routes', 'r.json']), /--target is required/);
  assert.throws(() => parseCli(['--target', 'http://x']), /--routes is required/);
  assert.throws(() => parseCli(['--target', 'not a url', '--routes', 'r']), /--target must be an http\(s\) URL/);
  assert.throws(() => parseCli([...required, '--rates', '10,abc']), /--rates must be a comma-separated list of positive numbers/);
  assert.throws(() => parseCli([...required, '--concurrency', '0']), /--concurrency/);
  assert.throws(() => parseCli([...required, '--duration=-1']), /--duration must be a positive number of seconds/);
  assert.throws(() => parseCli([...required, '--load-before-build', 'high']), /--load-before-build must be a number, 0 or more/);
  assert.throws(() => parseCli([...required, '--bogus']), /bogus/);
});

const server = await startServer((req, res) => {
  res.setHeader('content-type', 'text/html');
  res.end('<p>Items</p>');
}, 40);
/** @type {string[]} */
const dirs = [];
after(async () => {
  await server.close();
  await Promise.all(dirs.map((d) => rm(d, { recursive: true, force: true })));
});

/** @param {string[]} args */
async function cli(args) {
  const child = spawn(process.execPath, [BENCH, ...args], { stdio: ['ignore', 'pipe', 'pipe'] });
  let stdout = '';
  let stderr = '';
  child.stdout.on('data', (chunk) => (stdout += chunk));
  child.stderr.on('data', (chunk) => (stderr += chunk));
  const [code] = await once(child, 'exit');
  return { code, stdout, stderr };
}

test('a run writes its results as JSON and markdown, and prints the markdown', async () => {
  const dir = await mkdtemp(path.join(os.tmpdir(), 'netjes-bench-'));
  dirs.push(dir);
  const routes = path.join(dir, 'routes.json');
  await writeFile(routes, JSON.stringify({ routes: [{ name: 'document', path: '/', expect: { contains: 'Items' } }] }));
  const meta = path.join(dir, 'meta.json');
  await writeFile(meta, JSON.stringify({ versions: { netjes: '0.1.0' }, settings: { sidecars: 1 } }));
  const out = path.join(dir, 'results');
  const { code, stdout, stderr } = await cli([
    '--target', server.url.href, '--routes', routes, '--meta', meta, '--out', out,
    '--rates', '20', '--concurrency', '1', '--warmup', '0.05', '--duration', '0.2', '--provisional',
  ]);
  assert.equal(code, 0, stderr);
  const files = (await readdir(out)).sort();
  assert.equal(files.length, 4, files.join());
  assert.ok(files.includes('latest.json') && files.includes('latest.md'));
  const stamped = files.find((f) => /^\d{4}-\d\d-\d\dT\d\d-\d\d-\d\dZ\.json$/.test(f));
  assert.ok(stamped, files.join());
  const json = JSON.parse(await readFile(path.join(out, stamped), 'utf8'));
  assert.equal(json.schema, 2);
  assert.equal(json.versions.netjes, '0.1.0');
  assert.equal(json.settings.sidecars, 1);
  assert.equal(json.routes[0].steps[0].requests, 4);
  assert.equal(await readFile(path.join(out, 'latest.md'), 'utf8'), stdout);
  assert.match(stdout, /^### Netjes benchmark, /);
  assert.match(stdout, /PROVISIONAL/);
  assert.match(stderr, /document: 20\/s open loop/);
});

test('a run that cannot start says why and exits with 1', async () => {
  const { code, stderr } = await cli(['--target', server.url.href, '--routes', '/nonexistent/routes.json']);
  assert.equal(code, 1);
  assert.match(stderr, /routes\.json/);
});
