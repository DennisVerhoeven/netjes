import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { createRequire, syncBuiltinESMExports } from 'node:module';
import { setTimeout as sleep } from 'node:timers/promises';
import { after, before, test } from 'node:test';
import { RssSampler, processTree, rssByName } from '../lib/memory.mjs';

const require = createRequire(import.meta.url);
const linux = process.platform === 'linux';
/** @type {import('node:child_process').ChildProcess} */
let child;

before(async () => {
  child = spawn(process.execPath, ['-e', 'console.log("up"); setInterval(() => {}, 1000)'], { stdio: ['ignore', 'pipe', 'inherit'] });
  await once(/** @type {import('node:stream').Readable} */ (child.stdout), 'data');
});
after(() => child.kill());

test('a process tree holds the process, its children and their memory', { skip: !linux && 'needs /proc' }, async () => {
  const tree = await processTree(process.pid);
  const self = tree.find((p) => p.pid === process.pid);
  const kid = tree.find((p) => p.pid === child.pid);
  assert.ok(self && kid, JSON.stringify(tree));
  assert.equal(kid.ppid, process.pid);
  assert.equal(kid.name, 'node');
  assert.ok(kid.rssBytes > 1024 * 1024, `rss ${kid.rssBytes}`);
});

test('memory adds up by process name', { skip: !linux && 'needs /proc' }, async () => {
  const byName = await rssByName(process.pid);
  assert.ok((byName.node?.processes ?? 0) >= 2, JSON.stringify(byName));
  const tree = await processTree(process.pid);
  const nodeTotal = tree.filter((p) => p.name === 'node').reduce((sum, p) => sum + p.rssBytes, 0);
  // Both read /proc at slightly different moments; the totals are within a few megabytes.
  assert.ok(Math.abs((byName.node?.rssBytes ?? 0) - nodeTotal) < 8 * 1024 * 1024);
});

test('the sampler keeps the peak of each name while it runs', { skip: !linux && 'needs /proc' }, async () => {
  const sampler = new RssSampler(process.pid, 10);
  sampler.start();
  // Hold on to ~64 MB and stop while holding it: stopping samples once more, so the peak has seen it however
  // busy the machine is.
  const ballast = Buffer.alloc(64 * 1024 * 1024, 1);
  const during = (await rssByName(process.pid)).node?.rssBytes ?? 0;
  const { peak, samples } = await sampler.stop();
  assert.ok(ballast.length > 0);
  // The first sample and the one on stop, whatever happened in between.
  assert.ok(samples >= 2, `${samples} samples`);
  assert.ok((peak.node ?? 0) >= during - 8 * 1024 * 1024, `peak ${peak.node}, during ${during}`);
});

test('a process that does not exist has an empty tree', async () => {
  assert.deepEqual(await processTree(2 ** 22 + 12345), []);
});

test('the sampler leaves the caller’s event loop free for the load generator', { skip: !linux && 'needs /proc' }, async () => {
  // Every sample reads /proc for each process on the machine; done on this thread, it would delay requests due then.
  // Count the work instead of timing it: no /proc directory listing may happen on this thread while the sampler runs.
  // The worker has its own module instances, so its reads are not seen here; an on-thread sampler reads on its
  // first, synchronous sample.
  const fsp = require('node:fs/promises');
  const original = fsp.readdir;
  let procListings = 0;
  fsp.readdir = (target, ...rest) => {
    if (String(target) === '/proc') procListings++;
    return original(target, ...rest);
  };
  syncBuiltinESMExports();
  try {
    const sampler = new RssSampler(process.pid, 5);
    sampler.start();
    for (let tick = 0; tick < 50; tick++) await sleep(10);
    const { samples } = await sampler.stop();
    assert.ok(samples >= 2, `${samples} samples`);
    assert.equal(procListings, 0, 'the sampler listed /proc on the caller’s thread');
  } finally {
    fsp.readdir = original;
    syncBuiltinESMExports();
  }
});
