import http from 'node:http';
import https from 'node:https';
import { performance } from 'node:perf_hooks';
import { setTimeout as sleep } from 'node:timers/promises';

/**
 * `expect` is what every answer must meet to count as good (see routes.mjs). A request whose answer has the status but
 * misses the rest gets the error "<status> not as expected"; a wrong status is left for the summary to count by its
 * status.
 * @typedef {{ url: URL, method?: string, headers?: Record<string, string>, body?: string, expect?: import('./routes.mjs').Expect }} RequestSpec
 *
 * A whole answer, for an open loop's `inspect`: `headers` by lower-case name.
 * @typedef {{ status: number, headers: http.IncomingHttpHeaders, body: string }} Answer
 *
 * One request. Times are in milliseconds: `scheduled` and `sent` from the start of the step, `ttfb` (response
 * headers) and `total` (last byte) from `scheduled`, so time spent waiting to be sent counts as latency. `verdict` is
 * what the open loop's `inspect` said of the answer, when it was given one and the request got a whole answer.
 * @typedef {{ scheduled: number, sent: number, ttfb: number | null, total: number | null, status: number | null, bytes: number, error: string | null, verdict?: string }} Sample
 *
 * `generatorBusy` is the share of the step this thread spent working rather than waiting (its event loop
 * utilization): near 1, the load generator set the pace, not the server.
 * @typedef {{ samples: Sample[], elapsedMs: number, maxInFlight: number, generatorBusy: number }} StepResult
 */

export const USER_AGENT = 'netjes-bench/1';
export const NOT_AS_EXPECTED = 'not as expected';
const DEFAULT_TIMEOUT_MS = 30_000;

/**
 * Open loop: request i is due at i / rate seconds, whether or not earlier ones have been answered, and its latency
 * counts from when it was due. A server that stalls is charged for every request that waited, not only the ones in
 * flight (no coordinated omission). Requests over `maxConnections` wait for a connection, and that wait counts too.
 * `origin` (a performance.now() time, possibly ahead) lets a caller line other events up with the samples' times.
 * `stride` and `phase` share one schedule between threads: this one sends requests phase, phase + stride, ...
 * `inspect` reads each whole answer (it keeps the body until then) and says what it was; a function, so it cannot go
 * to another thread.
 * @param {RequestSpec} request
 * @param {{ rate: number, durationMs: number, maxConnections?: number, timeoutMs?: number, origin?: number, stride?: number, phase?: number, inspect?: (answer: Answer) => string }} options
 * @returns {Promise<StepResult>}
 */
export async function openLoop(request, options) {
  const { rate, durationMs, maxConnections = 256, timeoutMs = DEFAULT_TIMEOUT_MS } = options;
  const { origin = performance.now(), stride = 1, phase = 0, inspect } = options;
  const agent = createAgent(request.url, maxConnections);
  const flight = { now: 0, max: 0 };
  const count = Math.round((rate * durationMs) / 1000);
  const interval = 1000 / rate;
  /** @type {Promise<Sample>[]} */
  const pending = [];
  await until(origin + phase * interval);
  const busy = performance.eventLoopUtilization();
  for (let i = phase; i < count; i += stride) {
    const scheduled = i * interval;
    await until(origin + scheduled);
    pending.push(send(request, agent, origin, scheduled, timeoutMs, flight, inspect));
  }
  const samples = await Promise.all(pending);
  const elapsedMs = performance.now() - origin;
  const generatorBusy = performance.eventLoopUtilization(busy).utilization;
  agent.destroy();
  return { samples, elapsedMs, maxInFlight: flight.max, generatorBusy };
}

/**
 * Closed loop: `concurrency` connections, each sending its next request when the previous one is answered, until
 * `durationMs` has passed. It measures throughput at that concurrency; its latencies hide queueing (each connection
 * waits for the server before it sends again), so latency comes from the open loop.
 * @param {RequestSpec} request
 * @param {{ concurrency: number, durationMs: number, timeoutMs?: number, origin?: number }} options
 * @returns {Promise<StepResult>}
 */
export async function closedLoop(request, { concurrency, durationMs, timeoutMs = DEFAULT_TIMEOUT_MS, origin = performance.now() }) {
  const agent = createAgent(request.url, concurrency);
  const flight = { now: 0, max: 0 };
  /** @type {Sample[]} */
  const samples = [];
  await until(origin);
  const busy = performance.eventLoopUtilization();
  const deadline = origin + durationMs;
  await Promise.all(
    Array.from({ length: concurrency }, async () => {
      while (performance.now() < deadline) {
        samples.push(await send(request, agent, origin, performance.now() - origin, timeoutMs, flight));
      }
    }),
  );
  const elapsedMs = performance.now() - origin;
  const generatorBusy = performance.eventLoopUtilization(busy).utilization;
  agent.destroy();
  return { samples, elapsedMs, maxInFlight: flight.max, generatorBusy };
}

/**
 * Waits until a performance.now() time, never returning early: timers have millisecond resolution, so the last
 * fraction is waited out on the event loop.
 * @param {number} time
 */
async function until(time) {
  for (let wait = time - performance.now(); wait > 0; wait = time - performance.now()) {
    await (wait >= 1 ? sleep(Math.floor(wait)) : new Promise(setImmediate));
  }
}

/**
 * Whether an answer meets the parts of `expect` beyond its status, as cheaply as the load allows: a prefix test on the
 * content type, a lookup for the header and, only when the route names a text, a byte search of the body (no decoding,
 * no parsing).
 * @param {import('./routes.mjs').Expect | undefined} expect
 * @returns {{ keepsBody: boolean, meets: (headers: http.IncomingHttpHeaders, chunks: Buffer[]) => boolean } | null}
 *   null when there is nothing to check beyond the status
 */
function matcher(expect) {
  if (!expect) return null;
  const { contentType, contains, header } = expect;
  if (contentType === undefined && contains === undefined && header === undefined) return null;
  const text = contains === undefined ? null : Buffer.from(contains);
  const name = header?.toLowerCase();
  return {
    keepsBody: text !== null,
    meets(headers, chunks) {
      if (contentType !== undefined && !String(headers['content-type'] ?? '').startsWith(contentType)) return false;
      if (name !== undefined && headers[name] === undefined) return false;
      // A text can span two chunks, so the answer is joined first (a copy of the body: a few kilobytes).
      return text === null || Buffer.concat(chunks).includes(text);
    },
  };
}

/**
 * @param {URL} url
 * @param {number} maxSockets
 */
function createAgent(url, maxSockets) {
  const Agent = url.protocol === 'https:' ? https.Agent : http.Agent;
  return new Agent({ keepAlive: true, maxSockets, maxFreeSockets: maxSockets });
}

/**
 * @param {RequestSpec} request
 * @param {http.Agent} agent
 * @param {number} origin
 * @param {number} scheduled
 * @param {number} timeoutMs
 * @param {{ now: number, max: number }} flight
 * @param {((answer: Answer) => string) | undefined} [inspect]
 * @returns {Promise<Sample>}
 */
function send(request, agent, origin, scheduled, timeoutMs, flight, inspect) {
  return new Promise((resolve) => {
    const due = origin + scheduled;
    const sent = performance.now() - origin;
    /** @type {number | null} */
    let ttfb = null;
    /** @type {number | null} */
    let status = null;
    let bytes = 0;
    /** @type {Buffer[]} */
    const chunks = [];
    /** @type {http.IncomingHttpHeaders} */
    let headers = {};
    let done = false;
    const check = matcher(request.expect);
    const wantedStatus = request.expect?.status ?? 200;
    flight.now++;
    flight.max = Math.max(flight.max, flight.now);

    /** @param {string | null} error */
    const finish = (error) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      flight.now--;
      const total = error === null ? performance.now() - due : null;
      /** @type {Sample} */
      const sample = { scheduled, sent, ttfb, total, status, bytes, error };
      if (check && error === null && status === wantedStatus && !check.meets(headers, chunks)) {
        sample.error = `${status} ${NOT_AS_EXPECTED}`;
      }
      if (inspect && error === null && status !== null) {
        sample.verdict = inspect({ status, headers, body: Buffer.concat(chunks).toString('utf8') });
      }
      resolve(sample);
    };

    const client = request.url.protocol === 'https:' ? https : http;
    const req = client.request(request.url, {
      agent,
      method: request.method ?? 'GET',
      headers: { 'user-agent': USER_AGENT, ...request.headers },
    });
    // One deadline for the whole exchange, so a response that keeps trickling cannot run past it.
    const timer = setTimeout(() => {
      finish('timeout');
      req.destroy();
    }, timeoutMs);
    req.on('response', (res) => {
      ttfb = performance.now() - due;
      status = res.statusCode ?? null;
      headers = res.headers;
      res.on('data', (/** @type {Buffer} */ chunk) => {
        bytes += chunk.length;
        if (inspect || check?.keepsBody) chunks.push(chunk);
      });
      res.on('end', () => finish(null));
      res.on('error', (error) => finish(codeOf(error)));
      res.on('close', () => finish(res.complete ? null : 'aborted'));
    });
    req.on('error', (error) => finish(codeOf(error)));
    req.end(request.body);
  });
}

/** @param {Error & { code?: string }} error */
function codeOf(error) {
  return error.code ?? error.message;
}
