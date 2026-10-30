// The RssSampler's worker thread: samples until the 'stop' message, then posts { peak, samples }.
import { parentPort, workerData } from 'node:worker_threads';
import { sampleUntilStopped } from './memory.mjs';

const port = /** @type {import('node:worker_threads').MessagePort} */ (parentPort);
const signal = { stopped: false };
port.once('message', () => (signal.stopped = true));
port.postMessage(await sampleUntilStopped(workerData.pid, workerData.intervalMs, signal));
