import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import type { CliIo, SpawnCall } from '../cli/main.mjs';

export type Recorded = { io: CliIo; calls: SpawnCall[]; out: () => string; err: () => string };

/** A CliIo that records what the CLI prints and the processes it would start, running none of them. */
export function recordingIo(cwd: string, onSpawn: (call: SpawnCall) => number | Promise<number> = () => 0): Recorded {
  const calls: SpawnCall[] = [];
  let out = '';
  let err = '';
  return {
    calls,
    out: () => out,
    err: () => err,
    io: {
      cwd,
      env: { PATH: process.env.PATH ?? '' },
      stdout: (text) => {
        out += text;
      },
      stderr: (text) => {
        err += text;
      },
      spawn: async (call) => {
        calls.push(call);
        return onSpawn(call);
      },
    },
  };
}

/**
 * The parts of a Netjes app the CLI looks at before it starts anything: a project file and, unless `installed` is
 * false, netjes and vite in node_modules as `npm install` leaves them. Returns vite's entry point.
 */
export async function fakeApp(dir: string, { installed = true, built = false } = {}): Promise<string> {
  await mkdir(dir, { recursive: true });
  await writeFile(path.join(dir, 'App.csproj'), '<Project Sdk="Microsoft.NET.Sdk.Web" />\n');
  const vite = path.join(dir, 'node_modules', 'vite');
  if (installed) {
    await mkdir(path.join(vite, 'bin'), { recursive: true });
    await writeFile(path.join(vite, 'package.json'), JSON.stringify({ name: 'vite', version: '8.0.0', bin: { vite: 'bin/vite.js' } }));
    await writeFile(path.join(vite, 'bin', 'vite.js'), '');
    await mkdir(path.join(dir, 'node_modules', 'netjes'), { recursive: true });
    await writeFile(path.join(dir, 'node_modules', 'netjes', 'package.json'), JSON.stringify({ name: 'netjes', version: '0.0.0' }));
  }
  if (built) {
    await mkdir(path.join(dir, 'dist', 'server'), { recursive: true });
    await writeFile(path.join(dir, 'dist', 'server', 'server-entry.js'), '');
  }
  return path.join(vite, 'bin', 'vite.js');
}

/** An empty file where a published app's .dll would be, so `start --dll` finds something to run. */
export async function fakeDll(dir: string, file: string): Promise<string> {
  const dll = path.resolve(dir, file);
  await mkdir(path.dirname(dll), { recursive: true });
  await writeFile(dll, '');
  return dll;
}
