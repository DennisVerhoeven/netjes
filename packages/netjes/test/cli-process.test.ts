// @vitest-environment node
// The real process side of the CLI: these tests start node processes.
import { execFile, spawn } from 'node:child_process';
import { createServer, type AddressInfo } from 'node:net';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { findOnWindowsPath, processIo, windowsCommandLine } from '../cli/process-io.mjs';
import { colorSupport } from '../cli/terminal.mjs';

// A file: URL, which import() takes on every system; on Windows it refuses an absolute path such as D:\a\process-io.mjs.
const processIoModule = new URL('../cli/process-io.mjs', import.meta.url).href;
const bin = fileURLToPath(new URL('../bin/netjes.mjs', import.meta.url));
const ESC = '\u001b[';

type Result = { code: number; stdout: string; stderr: string };

function node(args: string[], env: Record<string, string | undefined> = {}, cwd = process.cwd()): Promise<Result> {
  return new Promise((resolve) => {
    // Without a terminal: stdout and stderr are pipes here, as in CI or `netjes ... | less`.
    const clean = Object.fromEntries(Object.entries(process.env).filter(([key]) => !['NO_COLOR', 'FORCE_COLOR'].includes(key)));
    execFile(process.execPath, args, { cwd, env: { ...clean, ...env } }, (error, stdout, stderr) =>
      resolve({ code: error ? Number(error.code ?? 1) : 0, stdout, stderr }),
    );
  });
}

/** Runs processIo().spawn(call) in a node process of its own and reports what that process printed. */
function spawnInChild(call: object, env: Record<string, string> = {}): Promise<Result> {
  const script = `
    const { processIo } = await import(${JSON.stringify(processIoModule)});
    process.exitCode = await processIo().spawn(${JSON.stringify(call)});`;
  return node(['--input-type=module', '-e', script], env);
}

let temp = '';
beforeEach(async () => {
  temp = await mkdtemp(path.join(tmpdir(), 'netjes-cli-process-'));
});
afterEach(async () => {
  await rm(temp, { recursive: true, force: true });
});

describe('a command that is not installed', () => {
  it.each([
    ['dotnet', 'Install the .NET SDK, 10.0 or newer: https://dotnet.microsoft.com/download'],
    ['npm', 'Install a Node.js release that matches ^22.22.2 || ^24.15.0 || >=26.0.0, which includes npm: https://nodejs.org'],
  ])('%s exits 127 with what to install', async (command, advice) => {
    const result = await spawnInChild({ command, args: ['--version'], cwd: temp, env: { PATH: temp } });

    expect(result.code).toBe(127);
    expect(result.stdout).toBe('');
    expect(result.stderr).toBe(`netjes: ${command} is not installed, or not on PATH. ${advice}\n`);
  });
});

describe('npm and npx on Windows', () => {
  // .cmd files there, which Node starts only through cmd.exe: the CLI looks them up as cmd.exe does, so that a missing
  // one is reported as on other systems, and passes cmd.exe a command line of its own.
  const nodejs = 'C:\\Program Files\\nodejs';
  const roaming = 'C:\\Users\\me\\AppData\\Roaming\\npm';
  // Files that exist, matched without regard to case as on Windows.
  const existing = (...files: string[]) => (file: string) => files.some((f) => f.toLowerCase() === file.toLowerCase());

  it('are found in the first PATH folder that has them, with an extension from PATHEXT', () => {
    const env = { Path: `C:\\Windows\\system32;${nodejs};${roaming}`, PATHEXT: '.COM;.EXE;.BAT;.CMD;.VBS' };

    // npm without an extension is the script for Git Bash, which cmd.exe does not run.
    expect(findOnWindowsPath('npm', env, existing(`${nodejs}\\npm`, `${nodejs}\\npm.cmd`, `${roaming}\\npm.cmd`))).toBe(`${nodejs}\\npm.cmd`);
  });

  it('read PATH and PATHEXT in any case, with quoted folders and empty entries', () => {
    const env = { path: `;"${nodejs}";`, pathext: '.exe;.cmd' };

    expect(findOnWindowsPath('npx', env, existing(`${nodejs}\\npx.cmd`))).toBe(`${nodejs}\\npx.cmd`);
  });

  it("try cmd.exe's extensions when PATHEXT is not set", () => {
    expect(findOnWindowsPath('npm', { PATH: nodejs }, existing(`${nodejs}\\npm.cmd`))).toBe(`${nodejs}\\npm.cmd`);
  });

  it('are missing when no PATH folder has them', () => {
    expect(findOnWindowsPath('npm', { PATH: 'C:\\Windows\\system32' }, existing(`${nodejs}\\npm.cmd`))).toBeNull();
    expect(findOnWindowsPath('npm', {}, existing(`${nodejs}\\npm.cmd`))).toBeNull();
  });

  it('skip a PATH folder they cannot look in, as cmd.exe does', () => {
    // On Linux and macOS a name too long for the file system fails with ENAMETOOLONG, not ENOENT, as a folder the user
    // may not read fails with EPERM or EACCES on Windows.
    expect(findOnWindowsPath('npm', { PATH: 'x'.repeat(300) })).toBeNull();
  });

  it('run with the file in quotes, so that a folder with spaces stays one word', () => {
    expect(windowsCommandLine(`${nodejs}\\npm.cmd`, ['install'])).toBe(`"${nodejs}\\npm.cmd" install`);
    expect(windowsCommandLine(`${nodejs}\\npm.cmd`, ['--prefix', 'C:\\my app'])).toBe(`"${nodejs}\\npm.cmd" --prefix "C:\\my app"`);
  });
});

describe('npm', () => {
  // As `netjes new` runs npm install. On Windows this is the lookup on PATH with PATHEXT, the command line and cmd.exe
  // from start to finish; elsewhere a plain spawn. npm can take seconds to start on a busy Windows machine.
  it('runs from PATH with its arguments', async () => {
    const result = await spawnInChild({ command: 'npm', args: ['--version'], cwd: temp, env: { ...process.env } });

    expect(result.code, result.stderr).toBe(0);
    expect(result.stdout).toMatch(/^\d+\.\d+\.\d+\s*$/);
  }, 30_000);
});

describe('a process that ends', () => {
  it('passes its exit code through', async () => {
    const result = await spawnInChild({ command: process.execPath, args: ['-e', 'process.exit(3)'], cwd: temp, env: {} });

    expect(result.code).toBe(3);
  });

  it.skipIf(process.platform === 'win32')('exits 128 + the signal number when a signal ended it', async () => {
    const result = await spawnInChild({
      command: process.execPath,
      args: ['-e', 'process.kill(process.pid, "SIGTERM")'],
      cwd: temp,
      env: {},
    });

    expect(result.code).toBe(143);
  });

  it("sends the process's stdout to stderr when asked", async () => {
    const result = await spawnInChild({
      command: process.execPath,
      args: ['-e', 'console.log("from the app")'],
      cwd: temp,
      env: {},
      stdout: 'stderr',
    });

    expect(result.stdout).toBe('');
    expect(result.stderr).toBe('from the app\n');
  });
});

describe('capture', () => {
  it('returns the exit code, stdout and stderr', async () => {
    const io = processIo();

    const result = await io.capture?.({
      command: process.execPath,
      args: ['-e', 'console.log("out"); console.error("err"); process.exit(3)'],
      cwd: temp,
      env: { PATH: process.env.PATH },
    });

    expect(result).toEqual({ code: 3, stdout: 'out\n', stderr: 'err\n' });
  });

  it('reports a command that is not installed', async () => {
    const result = await processIo().capture?.({ command: 'dotnet', args: ['--version'], cwd: temp, env: { PATH: temp } });

    expect(result?.code).toBe(127);
  });
});

describe('colour', () => {
  it.each([
    [{}, true, true],
    [{}, false, false],
    [{ NO_COLOR: '1' }, true, false],
    [{ NO_COLOR: '' }, true, true],
    [{ TERM: 'dumb' }, true, false],
    [{ FORCE_COLOR: '1' }, false, true],
    [{ FORCE_COLOR: '0' }, true, false],
    [{ FORCE_COLOR: 'false' }, true, false],
    [{ FORCE_COLOR: '1', NO_COLOR: '1' }, true, false],
  ])('with %j and a terminal: %s, colour is %s', (env, tty, expected) => {
    expect(colorSupport(env, tty)).toBe(expected);
  });

  it('is off in piped output', async () => {
    const result = await node([bin, 'nwe']);

    expect(result.code).toBe(1);
    expect(result.stderr).toContain("unknown command 'nwe'");
    expect(result.stderr).not.toContain(ESC);
  });

  it('is on with FORCE_COLOR, and NO_COLOR wins', async () => {
    expect((await node([bin, 'nwe'], { FORCE_COLOR: '1' })).stderr).toContain(ESC);
    expect((await node([bin, 'nwe'], { FORCE_COLOR: '1', NO_COLOR: '1' })).stderr).not.toContain(ESC);
  });
});

describe('the netjes command', () => {
  it('prints the version on stdout', async () => {
    const result = await node([bin, '--version']);

    expect(result).toMatchObject({ code: 0, stderr: '' });
    expect(result.stdout).toMatch(/^\d+\.\d+\.\d+\S*\n$/);
  });

  it('prints help on stdout and usage errors on stderr', async () => {
    const help = await node([bin, 'help', 'new']);
    expect(help).toMatchObject({ code: 0, stderr: '' });
    expect(help.stdout).toContain('Usage: netjes new <dir> [options]');

    const error = await node([bin, 'new', 'app', '--bogus'], {}, temp);
    expect(error).toMatchObject({ code: 1, stdout: '' });
    expect(error.stderr).toContain("unknown option '--bogus'");
  });

  it('stops quietly when what reads its output goes away, as with `netjes help | head -1`', async () => {
    const child = spawn(process.execPath, [bin, 'help', 'new'], { stdio: ['ignore', 'pipe', 'pipe'] });
    // Closed before netjes writes: its write to stdout fails with EPIPE.
    child.stdout.destroy();
    let stderr = '';
    child.stderr.on('data', (chunk) => (stderr += chunk));

    const code = await new Promise((resolve) => child.on('close', resolve));

    expect(stderr).toBe('');
    expect(code).toBe(0);
  });
});

describe('portFree', () => {
  it('tells a port in use from a free one', async () => {
    const io = processIo();
    const server = createServer();
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
    const { port } = server.address() as AddressInfo;
    try {
      expect(await io.portFree?.(port)).toBe(false);
    } finally {
      await new Promise((resolve) => server.close(resolve));
    }

    expect(await io.portFree?.(port)).toBe(true);
  });

  it('checks the address it is given, such as localhost, where Vite listens', async () => {
    const io = processIo();
    // localhost resolves to ::1 first on many systems, where a check on 127.0.0.1 would find the port free.
    const server = createServer();
    await new Promise<void>((resolve) => server.listen(0, 'localhost', resolve));
    const { port } = server.address() as AddressInfo;
    try {
      expect(await io.portFree?.(port, 'localhost')).toBe(false);
    } finally {
      await new Promise((resolve) => server.close(resolve));
    }

    expect(await io.portFree?.(port, 'localhost')).toBe(true);
  });
});
