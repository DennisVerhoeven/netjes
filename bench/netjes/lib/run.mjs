import { execFile } from 'node:child_process';
import os from 'node:os';
import { promisify } from 'node:util';
import { machineInfo, runtimeVersions } from './machine.mjs';
import { RssSampler, rssByName } from './memory.mjs';
import { restartUnderLoad } from './restart.mjs';
import { checkRoute, resolvePath, toRequest } from './routes.mjs';
import { summarizeStep } from './stats.mjs';
import { runStep } from './workers.mjs';

const run = promisify(execFile);

/** Connections for the warm-up: enough to bring the server's thread pool and JIT up. */
const WARMUP_CONCURRENCY = 8;
/** A send lag (p99) above this means the load generator, not the server, set the pace. */
const MAX_SEND_LAG_MS = 5;
/** So does a load generator thread busier than this. */
const MAX_GENERATOR_BUSY = 0.8;

/**
 * @typedef {{
 *   target: URL,
 *   routeList: import('./routes.mjs').RouteList,
 *   only?: string[] | null,
 *   rates: number[],
 *   concurrency: number[],
 *   warmupMs: number,
 *   durationMs: number,
 *   maxConnections: number,
 *   workers?: number,
 *   timeoutMs: number,
 *   pid?: number | null,
 *   kill?: string | null,
 *   relaunchWithoutFallback?: string | null,
 *   restartRate: number,
 *   restartBeforeMs: number,
 *   restartAfterMs: number,
 *   versions?: Record<string, string>,
 *   settings?: Record<string, string | number | boolean | number[]>,
 *   provisional: boolean,
 *   loadBeforeBuild?: number | null,
 *   note: string | null,
 *   title?: string,
 *   log?: (line: string) => void,
 * }} RunOptions
 */

/**
 * Checks the routes, then for each: a warm-up, the open-loop rates and the closed-loop concurrency levels; then the
 * restart under load when there is a kill command and the restart's route is among those chosen; memory throughout
 * when there is a process to watch. With a relaunch command, the restart is measured again after it: the app started
 * again with ClientRenderFallback off, its restart route checked and warmed up as at the start.
 * @param {RunOptions} options
 * @returns {Promise<import('./report.mjs').Results>}
 */
export async function runBenchmark(options) {
  const { target, routeList, rates, concurrency, warmupMs, durationMs, maxConnections, timeoutMs } = options;
  const { pid = null, kill = null, workers = 1, log = () => {} } = options;
  const startedAt = new Date().toISOString();
  const machine = await machineInfo();

  const routes = selectRoutes(routeList.routes, options.only ?? null);
  const idle = pid === null ? null : await rssByName(pid);
  const runtimes = pid === null ? {} : await runtimeVersions(pid);

  const problems = [];
  for (const route of routes) {
    for (const problem of await checkRoute(target, route)) problems.push(`${route.name} (${route.method} ${route.path}): ${problem}`);
  }
  if (problems.length > 0) throw new Error(`Some routes do not answer as expected:\n${problems.join('\n')}`);

  const sampler = pid === null ? null : new RssSampler(pid, 250);
  sampler?.start();
  /** @type {import('./report.mjs').RouteResult[]} */
  const routeResults = [];
  /** @type {Record<string, number> | null} */
  let peak = null;
  try {
    for (const route of routes) {
      const request = { ...toRequest(target, route), expect: route.expect };
      if (warmupMs > 0) {
        log(`${route.name}: warm-up, ${WARMUP_CONCURRENCY} connections for ${warmupMs / 1000} s`);
        await runStep('closed', request, { concurrency: WARMUP_CONCURRENCY, durationMs: warmupMs, timeoutMs }, workers);
      }
      /** @type {import('./report.mjs').Step[]} */
      const steps = [];
      for (const rate of rates) {
        const step = await runStep('open', request, { rate, durationMs, maxConnections, timeoutMs }, workers);
        const summary = summarizeStep(step, route.expect);
        log(`${route.name}: ${rate}/s open loop, total p99 ${summary.total.p99?.toFixed(1) ?? '–'} ms, ${summary.errors} errors`);
        steps.push({ mode: 'open', rate, ...summary });
      }
      for (const connections of concurrency) {
        const step = await runStep('closed', request, { concurrency: connections, durationMs, timeoutMs }, workers);
        const summary = summarizeStep(step, route.expect);
        log(`${route.name}: ${connections} connections, ${summary.throughput.toFixed(0)}/s, ${summary.errors} errors`);
        steps.push({ mode: 'closed', concurrency: connections, ...summary });
      }
      routeResults.push({ name: route.name, method: route.method, path: route.path, steps });
    }
  } finally {
    // On every path, as a sampler thread left running would keep the process alive.
    peak = sampler ? (await sampler.stop()).peak : null;
  }
  const end = pid === null ? null : await rssByName(pid);

  /** @type {import('./report.mjs').RestartEntry[]} */
  const restarts = [];
  /** @type {string[]} */
  const restartWarnings = [];
  const plan = routeList.restart;
  const route = plan ? routes.find((r) => r.name === plan.route) : undefined;
  if (kill && plan && route) {
    const request = toRequest(target, route);
    const health = plan.health ? { url: resolvePath(target, plan.health), healthy: plan.healthy } : null;
    /**
     * @param {import('./report.mjs').RestartEntry['fallback']} fallback
     * @param {string} label what the log lines start with, and what a warning says was not measured
     * @param {() => Promise<void>} [prepare] runs first; what it throws is the warning
     */
    const measure = async (fallback, label, prepare) => {
      try {
        await prepare?.();
        log(`${label}: ${route.name} at ${options.restartRate}/s, kill after ${options.restartBeforeMs / 1000} s`);
        const result = await restartUnderLoad(request, {
          rate: options.restartRate,
          beforeMs: options.restartBeforeMs,
          afterMs: options.restartAfterMs,
          kill: () => shell('kill', kill, label),
          health,
          expect: route.expect,
          timeoutMs,
        });
        restarts.push({ fallback, route: route.name, rate: options.restartRate, ...result });
      } catch (error) {
        // The routes measured so far are worth keeping; the warning says why the restart is missing.
        const warning = /** @type {Error} */ (error).message;
        log(`${label}: ${warning}`);
        restartWarnings.push(warning);
      }
    };

    await measure('default', 'restart');
    const relaunch = options.relaunchWithoutFallback ?? null;
    if (relaunch) {
      const label = 'restart with ClientRenderFallback off';
      await measure('off', label, async () => {
        log(`${label}: relaunching the app`);
        await shell('relaunch', relaunch, label);
        const problems = await checkRoute(target, route);
        if (problems.length > 0) {
          const at = `${route.name} (${route.method} ${route.path})`;
          throw new Error(`after the relaunch, ${at}: ${problems.join('; ')}, so the ${label} was not measured`);
        }
        if (warmupMs > 0) {
          log(`${label}: warm-up, ${WARMUP_CONCURRENCY} connections for ${warmupMs / 1000} s`);
          await runStep('closed', request, { concurrency: WARMUP_CONCURRENCY, durationMs: warmupMs, timeoutMs }, workers);
        }
      });
    }
  }

  return {
    schema: 2,
    ...(options.title ? { title: options.title } : {}),
    startedAt,
    finishedAt: new Date().toISOString(),
    provisional: options.provisional,
    note: options.note,
    target: target.href,
    machine,
    loadAverageBeforeBuild: options.loadBeforeBuild ?? null,
    loadAverageAfter: os.loadavg(),
    runtimes,
    versions: options.versions ?? {},
    settings: {
      warmupSeconds: warmupMs / 1000,
      stepSeconds: durationMs / 1000,
      rates,
      concurrency,
      maxConnections,
      workers,
      ...options.settings,
    },
    routes: routeResults,
    restarts,
    memory: idle && peak && end ? { idle, peak, end } : null,
    warnings: [...warnings(machine, options.loadBeforeBuild ?? null, routeResults), ...restartWarnings],
  };
}

/**
 * Runs a shell command for a restart. When it fails, the error says why, from what execFile rejected with (its exit
 * code or signal, and what it wrote to stderr), and that the restart was not measured.
 * @param {'kill' | 'relaunch'} kind
 * @param {string} command
 * @param {string} label the restart, as in "so the <label> was not measured"
 * @returns {Promise<void>}
 */
async function shell(kind, command, label) {
  try {
    await run('/bin/sh', ['-c', command]);
  } catch (error) {
    const { code, signal, stderr, message } = /** @type {Error & { code?: number | string, signal?: string | null, stderr?: string }} */ (
      error
    );
    const how = signal ? `was killed by ${signal}` : typeof code === 'number' ? `exited with ${code}` : `could not run (${code ?? message})`;
    const output = (stderr ?? '').trim().replace(/\s*\n\s*/g, '; ');
    throw new Error(`the ${kind} command \`${command}\` ${how}${output ? `: ${output}` : ''}, so the ${label} was not measured`);
  }
}

/**
 * @param {import('./routes.mjs').Route[]} routes
 * @param {string[] | null} only
 */
function selectRoutes(routes, only) {
  if (only === null) return routes;
  for (const name of only) {
    if (!routes.some((r) => r.name === name)) throw new Error(`--only: no route named "${name}"`);
  }
  return routes.filter((r) => only.includes(r.name));
}

/**
 * @param {import('./machine.mjs').Machine} machine
 * @param {number | null} loadBeforeBuild the 1-minute load average from before the app was built: the build alone
 *   lifts the one at the start
 * @param {import('./report.mjs').RouteResult[]} routes
 */
function warnings(machine, loadBeforeBuild, routes) {
  const out = [];
  const load = loadBeforeBuild ?? machine.loadAverage[0] ?? 0;
  if (load >= 1) {
    const when = loadBeforeBuild === null ? 'when the run started' : 'before the build';
    out.push(`the machine was not idle: its load average was ${load.toFixed(2)} ${when}`);
  }
  for (const route of routes) {
    for (const step of route.steps) {
      const at = step.mode === 'open' ? `${route.name} at ${step.rate}/s` : `${route.name} with ${step.concurrency} connections`;
      if (step.errors > 0) out.push(`${at}: ${step.errors} of ${step.requests} requests failed`);
      if (step.generatorBusy > MAX_GENERATOR_BUSY) {
        out.push(
          `${at}: the load generator was busy ${Math.round(step.generatorBusy * 100)}% of the step, so the server may be faster than this shows`,
        );
      }
      if (step.mode !== 'open') continue;
      if (step.throughput < step.rate * 0.9) {
        out.push(
          `${at}: answered ${step.throughput.toFixed(1)}/s, below the arrival rate, so requests queued and latency grew through the step`,
        );
      }
      const lag = step.sendLag.p99 ?? 0;
      if (lag > MAX_SEND_LAG_MS) {
        out.push(`${at}: the load generator sent requests up to ${lag.toFixed(1)} ms late (p99); that delay counts as latency`);
      }
    }
  }
  return out;
}
