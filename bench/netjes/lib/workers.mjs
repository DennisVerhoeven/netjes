import { performance } from 'node:perf_hooks';
import { Worker } from 'node:worker_threads';
import { closedLoop, openLoop } from './load.mjs';

/**
 * @typedef {import('./load.mjs').Sample} Sample
 * @typedef {import('./load.mjs').StepResult} StepResult
 * @typedef {{ scheduled: Float64Array, sent: Float64Array, ttfb: Float64Array, total: Float64Array, status: Float64Array, bytes: Float64Array, errors: [number, string][] }} PackedSamples
 */

/** Time for the threads to start before the step's first request is due. */
const START_DELAY_MS = 250;

/**
 * Samples as columns of numbers (NaN for null), which pass between threads far cheaper than objects.
 * @param {Sample[]} samples
 * @returns {PackedSamples}
 */
export function packSamples(samples) {
  const column = (/** @type {(s: Sample) => number | null} */ pick) => Float64Array.from(samples, (s) => pick(s) ?? Number.NaN);
  return {
    scheduled: column((s) => s.scheduled),
    sent: column((s) => s.sent),
    ttfb: column((s) => s.ttfb),
    total: column((s) => s.total),
    status: column((s) => s.status),
    bytes: column((s) => s.bytes),
    errors: samples.flatMap((s, i) => (s.error === null ? [] : [/** @type {[number, string]} */ ([i, s.error])])),
  };
}

/**
 * @param {PackedSamples} packed
 * @returns {Sample[]}
 */
export function unpackSamples(packed) {
  const errors = new Map(packed.errors);
  const value = (/** @type {Float64Array} */ column, /** @type {number} */ i) => {
    const n = /** @type {number} */ (column[i]);
    return Number.isNaN(n) ? null : n;
  };
  return Array.from(packed.scheduled, (scheduled, i) => ({
    scheduled,
    sent: /** @type {number} */ (packed.sent[i]),
    ttfb: value(packed.ttfb, i),
    total: value(packed.total, i),
    status: value(packed.status, i),
    bytes: /** @type {number} */ (packed.bytes[i]),
    error: errors.get(i) ?? null,
  }));
}

/**
 * One load step on `workers` threads: the open loop's requests dealt out in turn (each thread sends every
 * `workers`-th request of the same schedule), the closed loop's connections split between them. With one, it runs
 * on this thread. Busy is the busiest thread's.
 * @param {'open' | 'closed'} kind
 * @param {import('./load.mjs').RequestSpec} request
 * @param {{ rate?: number, concurrency?: number, durationMs: number, maxConnections?: number, timeoutMs?: number }} options
 * @param {number} workers
 * @returns {Promise<StepResult>}
 */
export async function runStep(kind, request, options, workers) {
  if (workers <= 1) {
    return kind === 'open'
      ? openLoop(request, /** @type {Parameters<typeof openLoop>[1]} */ (options))
      : closedLoop(request, /** @type {Parameters<typeof closedLoop>[1]} */ (options));
  }
  const originEpoch = performance.timeOrigin + performance.now() + START_DELAY_MS;
  const shares = Array.from({ length: workers }, (_, k) => {
    if (kind === 'open') {
      const maxConnections = Math.max(1, Math.ceil((options.maxConnections ?? 256) / workers));
      return { ...options, maxConnections, stride: workers, phase: k };
    }
    const concurrency = /** @type {number} */ (options.concurrency);
    return { ...options, concurrency: Math.floor(concurrency / workers) + (k < concurrency % workers ? 1 : 0) };
  }).filter((share) => kind === 'open' || (share.concurrency ?? 0) > 0);

  const results = await Promise.all(
    shares.map(
      (share) =>
        /** @type {Promise<Omit<StepResult, 'samples'> & { samples: PackedSamples }>} */ (
          new Promise((resolve, reject) => {
            const worker = new Worker(new URL('./load-worker.mjs', import.meta.url), {
              workerData: {
                kind,
                request: { ...request, url: request.url.href },
                options: share,
                originEpoch,
              },
            });
            worker.once('message', resolve);
            worker.once('error', reject);
            worker.once('exit', (code) => code !== 0 && reject(new Error(`a load thread exited with code ${code}`)));
          })
        ),
    ),
  );
  return {
    samples: results.flatMap((r) => unpackSamples(r.samples)),
    elapsedMs: Math.max(...results.map((r) => r.elapsedMs)),
    maxInFlight: results.reduce((sum, r) => sum + r.maxInFlight, 0),
    generatorBusy: Math.max(...results.map((r) => r.generatorBusy)),
  };
}
