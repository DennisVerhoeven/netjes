import assert from 'node:assert/strict';
import { test } from 'node:test';
import { percentile, summarize, summarizeStep } from '../lib/stats.mjs';

test('percentile takes the nearest rank', () => {
  const values = Array.from({ length: 100 }, (_, i) => i + 1);
  assert.equal(percentile(values, 50), 50);
  assert.equal(percentile(values, 95), 95);
  assert.equal(percentile(values, 99), 99);
  assert.equal(percentile(values, 100), 100);
  assert.equal(percentile([7], 99), 7);
  // 10 values: p95 is the 10th (ceil(9.5)), not an interpolation between the 9th and 10th.
  assert.equal(percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 95), 10);
  assert.equal(percentile([], 50), null);
});

test('summarize sorts and reports count, min, mean, percentiles and max', () => {
  const summary = summarize([30, 10, 20, 40]);
  assert.deepEqual(summary, { count: 4, min: 10, mean: 25, p50: 20, p95: 40, p99: 40, max: 40 });
});

test('summarize of nothing is all nulls', () => {
  assert.deepEqual(summarize([]), { count: 0, min: null, mean: null, p50: null, p95: null, p99: null, max: null });
});

/** @param {Partial<import('../lib/load.mjs').Sample>} sample */
const sample = (sample) => ({ scheduled: 0, sent: 0, ttfb: 1, total: 2, status: 200, bytes: 10, error: null, ...sample });

test('a step summary counts the expected status as ok and everything else as an error, by kind', () => {
  const summary = summarizeStep({
    samples: [
      sample({ ttfb: 1, total: 4 }),
      sample({ ttfb: 3, total: 8 }),
      sample({ status: 500, ttfb: 0.5, total: 0.6 }),
      sample({ status: null, ttfb: null, total: null, error: 'timeout' }),
      sample({ status: 200, total: null, error: 'aborted' }),
    ],
    elapsedMs: 500,
    maxInFlight: 3,
    generatorBusy: 0.25,
  });
  assert.equal(summary.requests, 5);
  assert.equal(summary.ok, 2);
  assert.equal(summary.errors, 3);
  assert.deepEqual(summary.errorKinds, { 500: 1, timeout: 1, aborted: 1 });
  // Latencies are those of the ok responses only: a fast 500 must not make the page look quick.
  assert.equal(summary.ttfb.count, 2);
  assert.equal(summary.ttfb.max, 3);
  assert.equal(summary.total.p50, 4);
  assert.equal(summary.maxInFlight, 3);
  assert.equal(summary.generatorBusy, 0.25);
});

test('a step summary takes the route’s expected status, and reports how late requests went out', () => {
  const summary = summarizeStep(
    {
      samples: [sample({ status: 404, scheduled: 10, sent: 10.5 }), sample({ status: 200, scheduled: 20, sent: 22 })],
      elapsedMs: 1000,
      maxInFlight: 1,
      generatorBusy: 0.1,
    },
    { status: 404 },
  );
  assert.equal(summary.ok, 1);
  assert.deepEqual(summary.errorKinds, { 200: 1 });
  assert.equal(summary.sendLag.max, 2);
  assert.equal(summary.sendLag.count, 2);
});

test('throughput is good answers per second over the time they arrived in, without the first answer’s latency', () => {
  // One request every 10 ms, each answered in 50 ms: the server keeps up with 100 a second. The step lasted 140 ms
  // from its start to the last answer, but 10 answers in 140 ms would undercount it.
  const samples = Array.from({ length: 10 }, (_, i) => sample({ scheduled: i * 10, sent: i * 10, total: 50 }));
  samples.push(sample({ scheduled: 45, sent: 45, status: 500, total: 1 }));
  const step = summarizeStep({ samples, elapsedMs: 140, maxInFlight: 5, generatorBusy: 0 });
  assert.ok(Math.abs(step.throughput - 100) < 1e-9, `throughput ${step.throughput}`);
  // A single answer has no span: it counts over the whole step.
  const one = summarizeStep({ samples: [sample({ total: 50 })], elapsedMs: 500, maxInFlight: 1, generatorBusy: 0 });
  assert.equal(one.throughput, 2);
});
