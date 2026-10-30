import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { packSamples, runStep, unpackSamples } from '../lib/workers.mjs';
import { startServer } from './server.mjs';

test('samples survive packing into columns, nulls and errors included', () => {
  const samples = [
    { scheduled: 0, sent: 0.1, ttfb: 1.5, total: 2.25, status: 200, bytes: 1024, error: null },
    { scheduled: 5, sent: 5.2, ttfb: null, total: null, status: null, bytes: 0, error: 'ECONNREFUSED' },
    { scheduled: 10, sent: 10, ttfb: 3, total: null, status: 200, bytes: 12, error: 'aborted' },
  ];
  const packed = packSamples(samples);
  assert.ok(packed.scheduled instanceof Float64Array && packed.scheduled.length === 3);
  assert.deepEqual(unpackSamples(packed), samples);
});

const servers = [];
after(() => Promise.all(servers.map((s) => s.close())));

test('an open loop on several threads keeps one schedule: every request due once, in turn', async () => {
  const server = await startServer((_, res) => res.end('ok'), 50);
  servers.push(server);
  const result = await runStep('open', { url: server.url }, { rate: 200, durationMs: 500 }, 4);
  assert.equal(result.samples.length, 100);
  const due = result.samples.map((s) => s.scheduled).sort((a, b) => a - b);
  due.forEach((t, i) => assert.ok(Math.abs(t - i * 5) < 1e-6, `request ${i} due at ${t}`));
  assert.ok(result.samples.every((s) => s.status === 200 && s.sent >= s.scheduled));
  assert.ok(result.generatorBusy >= 0 && result.generatorBusy <= 1);
});

test('a closed loop on several threads splits its connections between them', async () => {
  let inFlight = 0;
  let most = 0;
  const server = await startServer((_, res) => {
    inFlight++;
    most = Math.max(most, inFlight);
    setTimeout(() => {
      inFlight--;
      res.end('ok');
    }, 20);
  }, 51);
  servers.push(server);
  const result = await runStep('closed', { url: server.url, headers: { 'X-A': '1' } }, { concurrency: 6, durationMs: 300 }, 4);
  assert.equal(most, 6);
  assert.ok(result.samples.length >= 30, `${result.samples.length} samples`);
  assert.equal(result.maxInFlight, 6);
});

test('one thread runs the step on this thread', async () => {
  const server = await startServer((_, res) => res.end('ok'), 52);
  servers.push(server);
  const result = await runStep('open', { url: server.url }, { rate: 100, durationMs: 100 }, 1);
  assert.equal(result.samples.length, 10);
});
