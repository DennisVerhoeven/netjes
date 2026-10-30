/**
 * The nearest-rank percentile of an ascending array: the smallest value with at least p% of the values at or below
 * it. Null for an empty array.
 * @param {ArrayLike<number>} sorted
 * @param {number} p
 * @returns {number | null}
 */
export function percentile(sorted, p) {
  if (sorted.length === 0) return null;
  const rank = Math.min(sorted.length, Math.max(1, Math.ceil((p / 100) * sorted.length)));
  return /** @type {number} */ (sorted[rank - 1]);
}

/**
 * @typedef {{ count: number, min: number | null, mean: number | null, p50: number | null, p95: number | null, p99: number | null, max: number | null }} Summary
 */

/**
 * @param {Iterable<number>} values
 * @returns {Summary}
 */
export function summarize(values) {
  const sorted = Float64Array.from(values).sort();
  if (sorted.length === 0) return { count: 0, min: null, mean: null, p50: null, p95: null, p99: null, max: null };
  let sum = 0;
  for (const value of sorted) sum += value;
  return {
    count: sorted.length,
    min: /** @type {number} */ (sorted[0]),
    mean: sum / sorted.length,
    p50: percentile(sorted, 50),
    p95: percentile(sorted, 95),
    p99: percentile(sorted, 99),
    max: /** @type {number} */ (sorted[sorted.length - 1]),
  };
}

/**
 * @typedef {import('./load.mjs').StepResult} StepResult
 * @typedef {{ requests: number, ok: number, errors: number, errorKinds: Record<string, number>, throughput: number, ttfb: Summary, total: Summary, sendLag: Summary, maxInFlight: number, generatorBusy: number }} StepSummary
 */

/**
 * @param {StepResult} step
 * @param {{ status?: number }} [expect]
 * @returns {StepSummary}
 */
export function summarizeStep(step, expect = {}) {
  const status = expect.status ?? 200;
  const ok = step.samples.filter((s) => s.error === null && s.status === status);
  /** @type {Record<string, number>} */
  const errorKinds = {};
  for (const s of step.samples) {
    if (s.error === null && s.status === status) continue;
    const kind = s.error ?? String(s.status);
    errorKinds[kind] = (errorKinds[kind] ?? 0) + 1;
  }
  return {
    requests: step.samples.length,
    ok: ok.length,
    errors: step.samples.length - ok.length,
    errorKinds,
    throughput: throughput(ok, step.elapsedMs),
    ttfb: summarize(ok.map((s) => /** @type {number} */ (s.ttfb))),
    total: summarize(ok.map((s) => /** @type {number} */ (s.total))),
    sendLag: summarize(step.samples.map((s) => s.sent - s.scheduled)),
    maxInFlight: step.maxInFlight,
    generatorBusy: step.generatorBusy,
  };
}

/**
 * Good answers per second over the span they arrived in: n answers make n - 1 intervals between the first and the
 * last. Counting from the step's start would charge the first answer's latency, and to the end of the step the last
 * one's, as missing throughput. A single answer (or answers that all arrived at once) count over the whole step.
 * @param {import('./load.mjs').Sample[]} ok
 * @param {number} elapsedMs
 */
function throughput(ok, elapsedMs) {
  const overStep = elapsedMs > 0 ? ok.length / (elapsedMs / 1000) : 0;
  if (ok.length < 2) return overStep;
  let first = Infinity;
  let last = -Infinity;
  for (const s of ok) {
    const at = s.scheduled + /** @type {number} */ (s.total);
    first = Math.min(first, at);
    last = Math.max(last, at);
  }
  return last > first ? (ok.length - 1) / ((last - first) / 1000) : overStep;
}
