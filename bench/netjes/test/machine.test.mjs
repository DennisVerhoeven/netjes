import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import os from 'node:os';
import { after, test } from 'node:test';
import { machineInfo, parseDotnetRuntimes, runtimeVersions } from '../lib/machine.mjs';

test('the machine is described by its CPU, cores, memory, OS and Node version', async () => {
  const machine = await machineInfo();
  assert.ok(machine.cpu.length > 0);
  assert.equal(machine.logicalCores, os.cpus().length);
  if (process.platform === 'linux') {
    assert.ok(machine.physicalCores !== null && machine.physicalCores >= 1 && machine.physicalCores <= machine.logicalCores);
  }
  assert.equal(machine.memoryBytes, os.totalmem());
  assert.ok(machine.os.length > 0);
  assert.equal(machine.kernel, os.release());
  assert.equal(machine.arch, process.arch);
  assert.equal(machine.loadAverage.length, 3);
  assert.equal(machine.node, process.version);
});

test('the .NET runtimes come from the libraries a process mapped', () => {
  const maps = [
    '7f0000000000-7f0000001000 r-xp 00000000 00:1f 123 /usr/lib64/dotnet/shared/Microsoft.NETCore.App/10.0.3/libcoreclr.so',
    '7f0000002000-7f0000003000 r--p 00000000 00:1f 124 /usr/lib64/dotnet/shared/Microsoft.AspNetCore.App/10.0.3/Microsoft.AspNetCore.dll',
    '7f0000004000-7f0000005000 r--p 00000000 00:1f 125 /home/u/.dotnet/shared/Microsoft.NETCore.App/10.0.3/System.Private.CoreLib.dll',
  ].join('\n');
  assert.deepEqual(parseDotnetRuntimes(maps), { dotnet: '10.0.3', aspnetcore: '10.0.3' });
  assert.deepEqual(parseDotnetRuntimes('7f00-7f01 r-xp 0 0:0 1 /usr/bin/node'), {});
});

/** @type {import('node:child_process').ChildProcess | undefined} */
let child;
after(() => child?.kill());

test('the Node version of a process tree is that of its node processes', { skip: process.platform !== 'linux' && 'needs /proc' }, async () => {
  child = spawn(process.execPath, ['-e', 'console.log("up"); setInterval(() => {}, 1000)'], { stdio: ['ignore', 'pipe', 'inherit'] });
  await once(/** @type {import('node:stream').Readable} */ (child.stdout), 'data');
  const runtimes = await runtimeVersions(process.pid);
  assert.equal(runtimes.node, process.version);
  assert.equal(runtimes.dotnet, undefined);
});
