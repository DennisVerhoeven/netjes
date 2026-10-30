import { performance } from 'node:perf_hooks';
import { setTimeout as sleep } from 'node:timers/promises';
import { USER_AGENT, openLoop } from './load.mjs';
import { answerProblems } from './routes.mjs';
import { summarize } from './stats.mjs';

/**
 * What marks the document Netjes writes itself when no sidecar is ready (`ClientRenderFallback`): the comment after the
 * payload in src/Netjes/Rendering/ClientRenderShell.cs. Such a document is a 200 without the page.
 */
export const FALLBACK_SHELL_MARKER = '<!-- No Node sidecar was ready: this page renders in the browser. -->';

/**
 * @typedef {import('./stats.mjs').Summary} Summary
 * @typedef {{ killAtMs: number, restartMs: number | null, firstOkAfterKillMs: number | null, requests: number, failed: number, errorKinds: Record<string, number>, shells: Summary, before: Summary, after: Summary }} RestartResult
 */

/** The verdicts on an answer other than a failure's kind. */
const PAGE = 'page';
const SHELL = 'shell';

/**
 * What an answer during a restart was: the page (`expect` met), a fallback shell (the expected status, and the
 * marker; it may hold the page's text in its payload, so the marker is looked for first), or a failure, by its status
 * or as "<status> not as expected".
 * @param {import('./load.mjs').Answer} answer
 * @param {import('./routes.mjs').Expect} expect
 * @returns {string}
 */
export function classifyAnswer(answer, expect) {
  if (answer.status !== (expect.status ?? 200)) return String(answer.status);
  if (answer.body.includes(FALLBACK_SHELL_MARKER)) return SHELL;
  return answerProblems(answer, expect).length === 0 ? PAGE : `${answer.status} not as expected`;
}

/**
 * Open-loop load on one request; after `beforeMs` it calls `kill` (which takes the renderer down) and keeps the load
 * on for `afterMs` more, polling the health URL meanwhile. Times count from when `kill` has returned. Each answer is
 * checked against `expect`, as the route was before the run: a good answer is the page.
 *
 * - `restartMs`: from the kill until the health URL reports healthy again, after it was seen unhealthy.
 * - `firstOkAfterKillMs`: from the kill until the first good answer to a request sent after it.
 * - `shells`: total latency of the fallback shells, the documents without the page that Netjes sends while no sidecar
 *   is ready. They are neither good answers nor failures.
 * - `before`: total latency of the good answers that arrived before the kill, the baseline.
 * - `after`: total latency of the other good answers, including requests in flight at the kill that it delayed.
 * - `failed`: the rest (errors, other statuses, answers that miss `expect`), by kind in `errorKinds`.
 *
 * When `kill` throws, the load runs out its schedule and the polling stops, then the error comes back.
 * @param {import('./load.mjs').RequestSpec} request
 * @param {{ rate: number, beforeMs: number, afterMs: number, kill: () => Promise<void>, health?: { url: URL, healthy: string | null } | null, expect?: import('./routes.mjs').Expect, pollMs?: number, timeoutMs?: number }} options
 * @returns {Promise<RestartResult>}
 */
export async function restartUnderLoad(request, options) {
  const { rate, beforeMs, afterMs, kill, health = null, expect = {}, pollMs = 25, timeoutMs } = options;
  const origin = performance.now();
  const inspect = (/** @type {import('./load.mjs').Answer} */ answer) => classifyAnswer(answer, expect);
  const load = openLoop(request, { rate, durationMs: beforeMs + afterMs, origin, inspect, ...(timeoutMs ? { timeoutMs } : {}) });

  /** @type {{ at: number, ok: boolean }[]} */
  const polls = [];
  let polling = true;
  const poller = (async () => {
    while (health && polling) {
      polls.push({ at: performance.now() - origin, ok: await isHealthy(health.url, health.healthy) });
      await sleep(pollMs);
    }
  })();

  await sleep(Math.max(0, origin + beforeMs - performance.now()));
  try {
    await kill();
  } catch (error) {
    // Nothing is left running when the error comes back: the load runs out its schedule and the polling stops.
    polling = false;
    await Promise.all([load, poller]);
    throw error;
  }
  // From here the renderer is gone; until the command returned, it may still have been answering.
  const killAtMs = performance.now() - origin;
  const { samples } = await load;
  polling = false;
  await poller;

  const good = (/** @type {import('./load.mjs').Sample} */ s) => s.verdict === PAGE;
  const shells = samples.filter((s) => s.verdict === SHELL);
  const failures = samples.filter((s) => s.verdict !== PAGE && s.verdict !== SHELL);
  /** @type {Record<string, number>} */
  const errorKinds = {};
  for (const s of failures) {
    const kind = s.error ?? s.verdict ?? String(s.status);
    errorKinds[kind] = (errorKinds[kind] ?? 0) + 1;
  }
  const answeredAt = (/** @type {import('./load.mjs').Sample} */ s) => s.scheduled + /** @type {number} */ (s.total);
  const before = samples.filter((s) => good(s) && answeredAt(s) < killAtMs);
  const after = samples.filter((s) => good(s) && answeredAt(s) >= killAtMs);
  const sentAfter = after.filter((s) => s.scheduled >= killAtMs);
  const firstOk = sentAfter.length > 0 ? sentAfter.reduce((min, s) => Math.min(min, answeredAt(s)), Infinity) : null;

  const down = polls.find((p) => p.at >= killAtMs && !p.ok);
  const up = down ? polls.find((p) => p.at > down.at && p.ok) : undefined;

  return {
    killAtMs,
    restartMs: up ? up.at - killAtMs : null,
    firstOkAfterKillMs: firstOk === null ? null : firstOk - killAtMs,
    requests: samples.length,
    failed: failures.length,
    errorKinds,
    shells: summarize(shells.map((s) => /** @type {number} */ (s.total))),
    before: summarize(before.map((s) => /** @type {number} */ (s.total))),
    after: summarize(after.map((s) => /** @type {number} */ (s.total))),
  };
}

/**
 * @param {URL} url
 * @param {string | null} healthy the whole body (trimmed) of a healthy answer, beyond a 2xx status; ASP.NET Core's
 *   health checks answer 200 for Degraded too
 */
async function isHealthy(url, healthy) {
  try {
    const response = await fetch(url, { headers: { 'user-agent': USER_AGENT }, signal: AbortSignal.timeout(2000) });
    const body = await response.text();
    return response.ok && (healthy === null || body.trim() === healthy);
  } catch {
    return false;
  }
}
