// A load step on a worker thread (see workers.mjs): runs its share and posts the samples back as columns.
import { performance } from 'node:perf_hooks';
import { parentPort, workerData } from 'node:worker_threads';
import { closedLoop, openLoop } from './load.mjs';
import { packSamples } from './workers.mjs';

const { kind, request, options, originEpoch } = workerData;
const spec = { ...request, url: new URL(request.url) };
// The threads share a start time on the wall clock; each turns it into its own performance.now() time.
const origin = originEpoch - performance.timeOrigin;
const result = kind === 'open' ? await openLoop(spec, { ...options, origin }) : await closedLoop(spec, { ...options, origin });
const packed = packSamples(result.samples);
/** @type {import('node:worker_threads').MessagePort} */ (parentPort).postMessage(
  { ...result, samples: packed },
  /** @type {ArrayBuffer[]} */ ([packed.scheduled, packed.sent, packed.ttfb, packed.total, packed.status, packed.bytes].map((c) => c.buffer)),
);
