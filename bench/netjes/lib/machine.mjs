import { execFile } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import os from 'node:os';
import { promisify } from 'node:util';
import { processTree } from './memory.mjs';

const run = promisify(execFile);

/**
 * @typedef {{ cpu: string, logicalCores: number, physicalCores: number | null, memoryBytes: number, os: string, kernel: string, arch: string, loadAverage: number[], node: string }} Machine
 * @typedef {{ dotnet?: string, aspnetcore?: string, node?: string }} Runtimes
 */

/** @returns {Promise<Machine>} */
export async function machineInfo() {
  const cpus = os.cpus();
  return {
    cpu: cpus[0]?.model.trim() ?? 'unknown',
    logicalCores: cpus.length,
    physicalCores: await physicalCores(),
    memoryBytes: os.totalmem(),
    os: (await osName()) ?? `${os.type()} ${os.version()}`,
    kernel: os.release(),
    arch: process.arch,
    loadAverage: os.loadavg(),
    node: process.version,
  };
}

/**
 * The .NET runtime versions a process has loaded, from its memory map.
 * @param {string} maps the text of /proc/<pid>/maps
 * @returns {Runtimes}
 */
export function parseDotnetRuntimes(maps) {
  /** @type {Runtimes} */
  const runtimes = {};
  const core = /\/shared\/Microsoft\.NETCore\.App\/([^/\s]+)\//.exec(maps);
  const aspnet = /\/shared\/Microsoft\.AspNetCore\.App\/([^/\s]+)\//.exec(maps);
  if (core) runtimes.dotnet = core[1];
  if (aspnet) runtimes.aspnetcore = aspnet[1];
  return runtimes;
}

/**
 * The runtimes a process tree runs on: the .NET runtime it loaded and the version of its Node processes.
 * @param {number} pid
 * @returns {Promise<Runtimes>}
 */
export async function runtimeVersions(pid) {
  /** @type {Runtimes} */
  const runtimes = {};
  for (const p of await processTree(pid)) {
    if (p.name === 'node' && runtimes.node === undefined) {
      try {
        const exe = `/proc/${p.pid}/exe`;
        runtimes.node = (await run(exe, ['--version'])).stdout.trim();
      } catch {
        // It exited; another node process may still answer.
      }
    }
    if (runtimes.dotnet === undefined) {
      try {
        Object.assign(runtimes, parseDotnetRuntimes(await readFile(`/proc/${p.pid}/maps`, 'utf8')));
      } catch {
        // Not ours to read, or gone.
      }
    }
  }
  return runtimes;
}

/** Distinct (package, core) pairs in /proc/cpuinfo; null where there is none. */
async function physicalCores() {
  try {
    const info = await readFile('/proc/cpuinfo', 'utf8');
    const cores = new Set();
    for (const block of info.split(/\n\s*\n/)) {
      const socket = /^physical id\s*:\s*(\d+)/m.exec(block)?.[1];
      const core = /^core id\s*:\s*(\d+)/m.exec(block)?.[1];
      if (socket !== undefined && core !== undefined) cores.add(`${socket}:${core}`);
    }
    return cores.size > 0 ? cores.size : null;
  } catch {
    return null;
  }
}

async function osName() {
  try {
    const release = await readFile('/etc/os-release', 'utf8');
    return /^PRETTY_NAME="?([^"\n]*)"?$/m.exec(release)?.[1] ?? null;
  } catch {
    return null;
  }
}
