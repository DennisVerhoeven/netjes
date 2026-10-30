// @ts-check
// The CliIo of a real run: this process's directory, environment and streams, and child processes.
import { execFile, spawn } from 'node:child_process';
import { existsSync, statSync } from 'node:fs';
import { createServer } from 'node:net';
import { constants } from 'node:os';
import path from 'node:path';
import { createInterface } from 'node:readline/promises';
import { DOTNET_DOWNLOAD, MIN_DOTNET, NODE_DOWNLOAD, NODE_RANGE } from './info.mjs';
import { Interrupted, colorSupport, printError } from './terminal.mjs';

const windows = process.platform === 'win32';

/** What to install when a command the CLI runs is missing. */
const INSTALL = new Map([
  ['dotnet', `Install the .NET SDK, ${MIN_DOTNET}.0 or newer: ${DOTNET_DOWNLOAD}`],
  ['npm', `Install a Node.js release that matches ${NODE_RANGE}, which includes npm: ${NODE_DOWNLOAD}`],
]);

/**
 * The exit code for a process that ended with `code` or by `signal`: 128 + the signal number, as shells report it.
 * @param {number | null} code
 * @param {NodeJS.Signals | null} signal
 */
const exitCode = (code, signal) => code ?? (signal ? 128 + (constants.signals[signal] ?? 0) : 0);

/**
 * Whether `file` is a file. False as well when it cannot be looked at, as in a PATH folder the user may not read, which
 * cmd.exe skips.
 * @param {string} file
 */
function isAFile(file) {
  try {
    return statSync(file).isFile();
  } catch {
    return false;
  }
}

/**
 * Where cmd.exe would find `command` on the PATH in `env`: the first directory with `command` plus one of the PATHEXT
 * extensions. Null when no directory has it. Variable names match in any case, as on Windows.
 * @param {string} command
 * @param {Record<string, string | undefined>} env
 * @param {(file: string) => boolean} [isFile]
 * @returns {string | null}
 */
export function findOnWindowsPath(command, env, isFile = isAFile) {
  /** @param {string} name */
  const variable = (name) => Object.entries(env).find(([key]) => key.toUpperCase() === name)?.[1] ?? '';
  const extensions = (variable('PATHEXT') || '.COM;.EXE;.BAT;.CMD').split(';').filter(Boolean);
  for (const folder of variable('PATH').split(';')) {
    const directory = folder.trim().replace(/^"(.*)"$/, '$1');
    if (!directory) continue;
    for (const extension of extensions) {
      const file = path.win32.join(directory, `${command}${extension.toLowerCase()}`);
      if (isFile(file)) return file;
    }
  }
  return null;
}

/**
 * The command line that runs a .cmd file with `args` through cmd.exe: the file in quotes, and each argument as it is,
 * or in quotes when it has more than letters, digits and path characters. Node joins `args` with spaces when it starts
 * a shell, unquoted (DEP0190), so the CLI passes this one string instead. For the CLI's own arguments (npm install):
 * cmd.exe still expands %VARIABLES% in quotes.
 * @param {string} file
 * @param {string[]} args
 */
export function windowsCommandLine(file, args) {
  return [`"${file}"`, ...args.map((arg) => (/^[\w@+=:,./\\-]+$/.test(arg) ? arg : `"${arg}"`))].join(' ');
}

/** @returns {import('./main.mjs').CliIo} */
export function processIo() {
  /** @type {import('./main.mjs').CliIo} */
  const io = {
    cwd: process.cwd(),
    env: process.env,
    stdout: (text) => process.stdout.write(text),
    stderr: (text) => process.stderr.write(text),
    color: { stdout: colorSupport(process.env, process.stdout.isTTY), stderr: colorSupport(process.env, process.stderr.isTTY) },
    spawn: ({ command, args, cwd, env, stdout }) =>
      new Promise((resolve) => {
        /** @type {import('node:child_process').StdioOptions} */
        const stdio = ['inherit', stdout === 'stderr' ? 2 : 'inherit', 'inherit'];
        const notInstalled = () =>
          printError(io, `netjes: ${command} is not installed, or not on PATH. ${INSTALL.get(command) ?? ''}`.trimEnd());
        // npm and npx are .cmd files on Windows, which Node starts only through cmd.exe. cmd.exe would report a missing
        // one in a message of its own, with exit code 9009, so the CLI looks it up first, as cmd.exe does.
        const shim = windows && command.startsWith('np') ? findOnWindowsPath(command, env) : undefined;
        if (shim === null) {
          notInstalled();
          resolve(127);
          return;
        }
        const child = shim
          ? spawn(windowsCommandLine(shim, args), { cwd, env, stdio, shell: true })
          : spawn(command, args, { cwd, env, stdio });
        const forward = (/** @type {NodeJS.Signals} */ signal) => child.kill(signal);
        process.on('SIGINT', forward);
        process.on('SIGTERM', forward);
        const done = (/** @type {number} */ code) => {
          process.off('SIGINT', forward);
          process.off('SIGTERM', forward);
          resolve(code);
        };
        child.on('error', (error) => {
          if (/** @type {NodeJS.ErrnoException} */ (error).code === 'ENOENT' && existsSync(cwd)) notInstalled();
          else printError(io, `netjes: could not run ${command}: ${error.message}`);
          done(127);
        });
        child.on('exit', (code, signal) => done(exitCode(code, signal)));
      }),
    capture: ({ command, args, cwd, env }) =>
      new Promise((resolve) => {
        execFile(command, args, { cwd, env }, (error, stdout, stderr) => {
          if (!error) return resolve({ code: 0, stdout, stderr });
          const code = typeof error.code === 'number' ? error.code : error.code === 'ENOENT' ? 127 : error.signal ? exitCode(null, error.signal) : 1;
          resolve({ code, stdout, stderr });
        });
      }),
  };
  io.portFree = (port, host = '127.0.0.1') =>
    new Promise((resolve) => {
      const server = createServer();
      server.once('error', () => resolve(false));
      server.listen({ port, host, exclusive: true }, () => server.close(() => resolve(true)));
    });
  // Only with a terminal on both ends; in a script or CI a missing answer is an error instead of a hang.
  if (process.stdin.isTTY && process.stdout.isTTY) io.prompt = terminalPrompt(process.stdin, process.stdout);
  return io;
}

/**
 * A prompt on a terminal: asks on `output`, reads the answer from `input`. Resolves to null when the input ends
 * (Ctrl+D), and rejects with Interrupted on Ctrl+C.
 * @param {NodeJS.ReadableStream} input
 * @param {NodeJS.WritableStream} output
 * @returns {NonNullable<import('./main.mjs').CliIo['prompt']>}
 */
export function terminalPrompt(input, output) {
  return async (question) => {
    const readline = createInterface({ input, output, terminal: true });
    // Cancelling the question ends the line it is on, so that what comes next starts on a line of its own.
    const cancel = new AbortController();
    let interrupted = false;
    readline.once('SIGINT', () => {
      interrupted = true;
      cancel.abort();
    });
    readline.once('close', () => cancel.abort());
    try {
      return await readline.question(question, { signal: cancel.signal });
    } catch {
      if (interrupted) throw new Interrupted();
      return null;
    } finally {
      readline.close();
    }
  };
}
