#!/usr/bin/env node
// The Netjes load generator: open-loop latency, closed-loop throughput, a restart under load and memory, against any
// base URL and route list. run.sh builds and starts the benchmark app and calls this; docs/benchmarks.md explains it.
import { copyFile, mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { parseArgs } from 'node:util';
import { renderMarkdown } from './lib/report.mjs';
import { loadRoutes } from './lib/routes.mjs';
import { runBenchmark } from './lib/run.mjs';

export const USAGE = `Usage: node bench.mjs --target <url> --routes <file> [options]

  --target <url>          Base URL of the app under test (may include a base path)
  --routes <file>         Route list (JSON; see bench/netjes/routes.json)
  --rates <list>          Open-loop arrival rates in requests/s (default 25,100,400; empty to skip)
  --concurrency <list>    Closed-loop connection counts (default 1,8,32; empty to skip)
  --warmup <s>            Warm-up per route (default 10)
  --duration <s>          Length of each step (default 15)
  --quick                 Short smoke run: 1 s warm-up, 3 s steps, 2 s + 5 s restart
  --max-connections <n>   Connection cap for the open loop (default 256)
  --workers <n>           Load generator threads (default 4); the restart always runs on one
  --timeout <s>           Per-request timeout (default 30)
  --pid <pid>             Process to watch for memory and runtime versions (with its children)
  --kill <command>        Shell command that kills the renderer, for the restart under load
  --relaunch-without-fallback <command>
                          Shell command that starts the app again with ClientRenderFallback off and returns once it
                          is healthy; the restart under load is then measured a second time
  --restart-rate <r>      Arrival rate during the restart (default 50)
  --restart-before <s>    Load before the kill (default 5)
  --restart-after <s>     Load after the kill (default 15)
  --only <names>          Only these routes (comma-separated)
  --meta <file>           JSON with "versions" and "settings" to record with the results
  --provisional           Mark the results PROVISIONAL (machine under load)
  --load-before-build <n> The 1-minute load average from before the app was built, for the idle check (run.sh
                          passes it; without it the check uses the load average at the start)
  --note <text>           A note under the heading
  --title <text>          The heading (default "Netjes benchmark")
  --out <dir>             Write <timestamp>.json/.md and latest.json/.md there
`;

const DEFAULTS = {
  rates: [25, 100, 400],
  concurrency: [1, 8, 32],
  warmup: 10,
  duration: 15,
  restartBefore: 5,
  restartAfter: 15,
};
const QUICK = { warmup: 1, duration: 3, restartBefore: 2, restartAfter: 5 };

/**
 * @param {string[]} argv
 */
export function parseCli(argv) {
  const { values } = parseArgs({
    args: argv,
    strict: true,
    allowPositionals: false,
    options: {
      target: { type: 'string' },
      routes: { type: 'string' },
      rates: { type: 'string' },
      concurrency: { type: 'string' },
      warmup: { type: 'string' },
      duration: { type: 'string' },
      quick: { type: 'boolean', default: false },
      'max-connections': { type: 'string' },
      workers: { type: 'string' },
      timeout: { type: 'string' },
      pid: { type: 'string' },
      kill: { type: 'string' },
      'relaunch-without-fallback': { type: 'string' },
      'restart-rate': { type: 'string' },
      'restart-before': { type: 'string' },
      'restart-after': { type: 'string' },
      only: { type: 'string' },
      meta: { type: 'string' },
      provisional: { type: 'boolean', default: false },
      'load-before-build': { type: 'string' },
      note: { type: 'string' },
      title: { type: 'string' },
      out: { type: 'string' },
      help: { type: 'boolean', default: false },
    },
  });
  if (values.help) return null;
  if (!values.target) throw new Error('--target is required');
  if (!values.routes) throw new Error('--routes is required');
  /** @type {URL} */
  let target;
  try {
    target = new URL(values.target);
  } catch {
    target = new URL('invalid:');
  }
  if (target.protocol !== 'http:' && target.protocol !== 'https:') throw new Error('--target must be an http(s) URL');

  const preset = values.quick ? { ...DEFAULTS, ...QUICK } : DEFAULTS;
  return {
    target,
    routesFile: values.routes,
    rates: values.rates === undefined ? preset.rates : list('--rates', values.rates),
    concurrency: values.concurrency === undefined ? preset.concurrency : list('--concurrency', values.concurrency, true),
    warmupMs: seconds('--warmup', values.warmup, preset.warmup, true),
    durationMs: seconds('--duration', values.duration, preset.duration),
    maxConnections: integer('--max-connections', values['max-connections'], 256),
    workers: integer('--workers', values.workers, 4),
    timeoutMs: seconds('--timeout', values.timeout, 30),
    pid: values.pid === undefined ? null : integer('--pid', values.pid, 0),
    kill: values.kill ?? null,
    relaunchWithoutFallback: values['relaunch-without-fallback'] ?? null,
    restartRate: positive('--restart-rate', values['restart-rate'], 50),
    restartBeforeMs: seconds('--restart-before', values['restart-before'], preset.restartBefore),
    restartAfterMs: seconds('--restart-after', values['restart-after'], preset.restartAfter),
    only: values.only === undefined ? null : values.only.split(',').filter(Boolean),
    meta: values.meta ?? null,
    provisional: values.provisional,
    loadBeforeBuild: values['load-before-build'] === undefined ? null : loadAverage('--load-before-build', values['load-before-build']),
    note: values.note ?? null,
    title: values.title,
    out: values.out ?? null,
  };
}

/**
 * @param {string[]} argv
 * @returns {Promise<number>} the exit code
 */
export async function main(argv) {
  try {
    const options = parseCli(argv);
    if (options === null) {
      process.stdout.write(USAGE);
      return 0;
    }
    const routeList = await loadRoutes(options.routesFile);
    const meta = options.meta === null ? {} : JSON.parse(await readFile(options.meta, 'utf8'));
    const results = await runBenchmark({
      ...options,
      routeList,
      versions: meta.versions ?? {},
      settings: meta.settings ?? {},
      log: (line) => process.stderr.write(`${line}\n`),
    });
    const markdown = renderMarkdown(results);
    if (options.out !== null) {
      await mkdir(options.out, { recursive: true });
      const stamp = results.startedAt.replace(/:/g, '-').replace(/\.\d+Z$/, 'Z');
      const json = path.join(options.out, `${stamp}.json`);
      const md = path.join(options.out, `${stamp}.md`);
      await writeFile(json, `${JSON.stringify(results, null, 2)}\n`);
      await writeFile(md, markdown);
      await copyFile(json, path.join(options.out, 'latest.json'));
      await copyFile(md, path.join(options.out, 'latest.md'));
      process.stderr.write(`results: ${json}\n         ${md}\n`);
    }
    process.stdout.write(markdown);
    return 0;
  } catch (error) {
    process.stderr.write(`bench: ${/** @type {Error} */ (error).message}\n`);
    return 1;
  }
}

/**
 * @param {string} name
 * @param {string} value
 * @param {boolean} [integers]
 */
function list(name, value, integers = false) {
  const items = value.split(',').filter((v) => v.trim() !== '').map(Number);
  if (items.some((n) => !Number.isFinite(n) || n <= 0 || (integers && !Number.isInteger(n)))) {
    throw new Error(`${name} must be a comma-separated list of positive ${integers ? 'integers' : 'numbers'}`);
  }
  return items;
}

/**
 * @param {string} name
 * @param {string | undefined} value
 * @param {number} fallback seconds
 * @param {boolean} [zero] whether 0 is allowed
 */
function seconds(name, value, fallback, zero = false) {
  const n = value === undefined ? fallback : Number(value);
  if (!Number.isFinite(n) || n < 0 || (n === 0 && !zero)) throw new Error(`${name} must be a positive number of seconds`);
  return Math.round(n * 1000);
}

/**
 * @param {string} name
 * @param {string | undefined} value
 * @param {number} fallback
 */
function integer(name, value, fallback) {
  const n = value === undefined ? fallback : Number(value);
  if (!Number.isInteger(n) || n <= 0) throw new Error(`${name} must be a positive integer`);
  return n;
}

/**
 * @param {string} name
 * @param {string | undefined} value
 * @param {number} fallback
 */
function positive(name, value, fallback) {
  const n = value === undefined ? fallback : Number(value);
  if (!Number.isFinite(n) || n <= 0) throw new Error(`${name} must be a positive number`);
  return n;
}

/**
 * @param {string} name
 * @param {string} value
 */
function loadAverage(name, value) {
  const n = value.trim() === '' ? Number.NaN : Number(value);
  if (!Number.isFinite(n) || n < 0) throw new Error(`${name} must be a number, 0 or more`);
  return n;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  process.exitCode = await main(process.argv.slice(2));
}
