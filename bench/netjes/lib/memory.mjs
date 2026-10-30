import { once } from 'node:events';
import { readdir, readFile, readlink } from 'node:fs/promises';
import path from 'node:path';
import { Worker } from 'node:worker_threads';

/**
 * @typedef {{ pid: number, ppid: number, name: string, rssBytes: number }} ProcessInfo
 * @typedef {Record<string, { rssBytes: number, processes: number }>} RssByName
 */

/**
 * A process and its descendants, named by their executable, with their resident memory. Linux only (/proc);
 * elsewhere, empty.
 * @param {number} pid
 * @returns {Promise<ProcessInfo[]>}
 */
export async function processTree(pid) {
  /** @type {string[]} */
  let entries;
  try {
    entries = await readdir('/proc');
  } catch {
    return [];
  }
  const all = (await Promise.all(entries.filter((e) => /^\d+$/.test(e)).map((e) => readStat(Number(e))))).filter(
    (p) => p !== null,
  );
  /** @type {Map<number, { pid: number, ppid: number, name: string }[]>} */
  const children = new Map();
  for (const p of all) children.set(p.ppid, [...(children.get(p.ppid) ?? []), p]);
  const root = all.find((p) => p.pid === pid);
  if (!root) return [];

  const tree = [root];
  for (let i = 0; i < tree.length; i++) tree.push(...(children.get(/** @type {{ pid: number }} */ (tree[i]).pid) ?? []));
  const withRss = await Promise.all(
    tree.map(async (p) => ({ ...p, name: (await executableName(p.pid)) ?? p.name, rssBytes: await readRss(p.pid) })),
  );
  // A process that exited between the two reads has no memory to report.
  return withRss.filter((p) => p.rssBytes !== null).map((p) => ({ ...p, rssBytes: /** @type {number} */ (p.rssBytes) }));
}

/**
 * Resident memory of a process tree, added up by process name (for an app: `dotnet` and its `node` sidecars).
 * @param {number} pid
 * @returns {Promise<RssByName>}
 */
export async function rssByName(pid) {
  /** @type {RssByName} */
  const byName = {};
  for (const p of await processTree(pid)) {
    const entry = (byName[p.name] ??= { rssBytes: 0, processes: 0 });
    entry.rssBytes += p.rssBytes;
    entry.processes++;
  }
  return byName;
}

/**
 * Samples a process tree's memory until stopped and keeps the highest total of each name. It samples on a worker
 * thread: a sample reads /proc for every process on the machine, which on the caller's thread would hold up the
 * requests the load generator has due.
 */
export class RssSampler {
  /**
   * @param {number} pid
   * @param {number} intervalMs
   */
  constructor(pid, intervalMs) {
    this.pid = pid;
    this.intervalMs = intervalMs;
    /** @type {Worker | null} */
    this.worker = null;
  }

  start() {
    this.worker = new Worker(new URL('./memory-worker.mjs', import.meta.url), {
      workerData: { pid: this.pid, intervalMs: this.intervalMs },
    });
  }

  /** @returns {Promise<{ peak: Record<string, number>, samples: number }>} */
  async stop() {
    const worker = this.worker;
    if (!worker) return { peak: {}, samples: 0 };
    this.worker = null;
    const result = once(worker, 'message');
    worker.postMessage('stop');
    const [message] = await result;
    await worker.terminate();
    return message;
  }
}

/**
 * Runs on the sampler's worker thread until it is told to stop; then samples once more, so a stop right after a
 * burst still sees it, and posts the peaks back.
 * @param {number} pid
 * @param {number} intervalMs
 * @param {{ stopped: boolean }} signal
 */
export async function sampleUntilStopped(pid, intervalMs, signal) {
  /** @type {Record<string, number>} */
  const peak = {};
  let samples = 0;
  const sample = async () => {
    samples++;
    for (const [name, { rssBytes }] of Object.entries(await rssByName(pid))) peak[name] = Math.max(peak[name] ?? 0, rssBytes);
  };
  while (!signal.stopped) {
    await sample();
    await new Promise((resolve) => setTimeout(resolve, intervalMs));
  }
  await sample();
  return { peak, samples };
}

/**
 * @param {number} pid
 * @returns {Promise<{ pid: number, ppid: number, name: string } | null>}
 */
async function readStat(pid) {
  try {
    const stat = await readFile(`/proc/${pid}/stat`, 'utf8');
    // "pid (name) state ppid ...": the name may itself hold spaces and parentheses.
    const open = stat.indexOf('(');
    const close = stat.lastIndexOf(')');
    const ppid = Number(stat.slice(close + 2).split(' ')[1]);
    return { pid, ppid, name: stat.slice(open + 1, close) };
  } catch {
    return null;
  }
}

/**
 * The file name of the process's executable: `dotnet`, `node`. The kernel's own name (in stat) is the main thread's,
 * which runtimes rename (Node's is `node-MainThread`).
 * @param {number} pid
 * @returns {Promise<string | null>}
 */
async function executableName(pid) {
  try {
    return path.basename(await readlink(`/proc/${pid}/exe`));
  } catch {
    return null;
  }
}

/**
 * @param {number} pid
 * @returns {Promise<number | null>}
 */
async function readRss(pid) {
  try {
    const status = await readFile(`/proc/${pid}/status`, 'utf8');
    const match = /^VmRSS:\s+(\d+) kB/m.exec(status);
    return match ? Number(match[1]) * 1024 : 0;
  } catch {
    return null;
  }
}
