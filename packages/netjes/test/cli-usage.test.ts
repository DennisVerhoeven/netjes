// @vitest-environment node
import { existsSync } from 'node:fs';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { run } from '../cli/main.mjs';
import { fakeDll, recordingIo } from './cli-io.ts';

let temp = '';
beforeEach(async () => {
  temp = await mkdtemp(path.join(tmpdir(), 'netjes-cli-usage-'));
});
afterEach(async () => {
  await rm(temp, { recursive: true, force: true });
});

const version = JSON.parse(await readFile(new URL('../package.json', import.meta.url), 'utf8')).version as string;

// Every command, its usage line and its own options, as `netjes help <command>` must show them.
const COMMANDS: [string, string, string[]][] = [
  ['new', 'netjes new <dir> [options]', ['--name <Name>', '--packages', '--skip-install']],
  ['dev', 'netjes dev [options]', ['--no-watch']],
  ['build', 'netjes build [options]', []],
  ['start', 'netjes start [options]', ['--dll <path>']],
  ['generate', 'netjes generate [options]', ['--check']],
  ['routes', 'netjes routes [options]', ['--json']],
  ['export', 'netjes export [dir] [options]', ['--no-build']],
  ['info', 'netjes info [options]', []],
];

describe('netjes help', () => {
  it('opens the overview with the version and what Netjes is for', async () => {
    const { io, out } = recordingIo(temp);

    expect(await run(['help'], io)).toBe(0);

    expect(out().split('\n')[0]).toBe(`netjes ${version}: React apps and sites on ASP.NET Core`);
  });

  it.each(COMMANDS)('shows the usage of %s, every option with its default, and an example', async (command, usage, options) => {
    const { io, out, err, calls } = recordingIo(temp);

    expect(await run(['help', command], io)).toBe(0);

    const text = out();
    expect(err()).toBe('');
    expect(calls).toEqual([]);
    expect(text).toContain(`Usage: ${usage}`);
    for (const option of [...options, '--cwd <dir>']) {
      const line = text.split('\n').find((l) => l.trimStart().startsWith(`${option} `));
      expect(line, option).toMatch(/\. Default: .+$/);
    }
    expect(text).toMatch(new RegExp(`Examples:\\n(  netjes ${command}.*\\n)+`));
  });

  it('never joins two parenthesised remarks in the help of any command', async () => {
    for (const [command] of [...COMMANDS, ['agents']]) {
      const { io, out } = recordingIo(temp);
      await run(['help', command as string], io);
      expect(out().split('\n').filter((line) => /\)\s*\(/.test(line)), command).toEqual([]);
    }
    const { io, out } = recordingIo(temp);
    await run(['help'], io);
    expect(out()).not.toMatch(/\)\s*\(/);
  });

  it.each([['--help'], ['-h']])('is what %s after a command prints', async (flag) => {
    const viaHelp = recordingIo(temp);
    await run(['help', 'generate'], viaHelp.io);
    const viaFlag = recordingIo(temp);

    expect(await run(['generate', flag], viaFlag.io)).toBe(0);

    expect(viaFlag.out()).toBe(viaHelp.out());
    expect(viaFlag.calls).toEqual([]);
  });

  it.each([[[]], [['help']], [['--help']], [['-h']], [['help', 'help']]])('lists every command for %j', async (args) => {
    const { io, out } = recordingIo(temp);

    expect(await run(args, io)).toBe(0);

    expect(out()).toContain('Usage: netjes <command> [options]');
    for (const [command] of COMMANDS) expect(out()).toMatch(new RegExp(`^  ${command}\\b`, 'm'));
    expect(out()).toContain('netjes help <command>');
  });
});

describe('netjes --version', () => {
  it.each([['--version'], ['-v']])('prints the package version for %s', async (flag) => {
    const { io, out } = recordingIo(temp);

    expect(await run([flag], io)).toBe(0);

    expect(out()).toBe(`${version}\n`);
  });
});

/** What a usage error must look like: exit 1, one line on stderr, nothing on stdout, nothing started. */
async function usageError(args: string[]) {
  const recorded = recordingIo(temp);
  const code = await run(args, recorded.io);
  expect(code, 'exit code').toBe(1);
  expect(recorded.out(), 'stdout').toBe('');
  expect(recorded.calls, 'processes started').toEqual([]);
  expect(recorded.err().endsWith('\n')).toBe(true);
  expect(recorded.err().trimEnd().split('\n'), 'one line').toHaveLength(1);
  return recorded.err().trimEnd();
}

describe('an unknown command', () => {
  it.each([
    ['nwe', 'new'],
    ['buidl', 'build'],
    ['genrate', 'generate'],
    ['gen', 'generate'],
    ['NEW', 'new'],
    ['doctor', 'info'],
    ['serve', 'start'],
  ])('%s fails with a suggestion of %s', async (typed, meant) => {
    const message = await usageError([typed]);

    expect(message).toContain(`unknown command '${typed}'`);
    expect(message).toContain(`did you mean \`netjes ${meant}\`?`);
  });

  it('version suggests --version', async () => {
    expect(await usageError(['version'])).toContain('did you mean `netjes --version`?');
  });

  it('without a close match points to the list of commands', async () => {
    const message = await usageError(['launch']);

    expect(message).toBe("netjes: unknown command 'launch'; run `netjes --help` for the commands");
  });

  it('fails the same way after help', async () => {
    expect(await usageError(['help', 'nwe'])).toContain("unknown command 'nwe'; did you mean `netjes new`?");
  });
});

describe('options with help', () => {
  it('an unknown one after help is an error', async () => {
    expect(await usageError(['help', '--bogus'])).toBe("netjes help: unknown option '--bogus'; run `netjes --help` for the options");
  });

  it('an unknown one next to --help is an error', async () => {
    expect(await usageError(['generate', '--help', '--bogus'])).toBe(
      "netjes generate: unknown option '--bogus'; run `netjes help generate` for its options",
    );
  });

  it("a command's own options are fine after help, as they are with --help", async () => {
    const viaHelp = recordingIo(temp);
    const viaFlag = recordingIo(temp);

    expect(await run(['help', 'new', '--name', 'Shop'], viaHelp.io)).toBe(0);
    expect(await run(['new', '--name', 'Shop', '--help'], viaFlag.io)).toBe(0);

    expect(viaHelp.out()).toContain('Usage: netjes new <dir> [options]');
    expect(viaHelp.out()).toBe(viaFlag.out());
  });
});

describe('an unknown option', () => {
  it.each([
    [['generate', '--chek'], 'generate', '--chek', '--check'],
    [['dev', '--watch'], 'dev', '--watch', '--no-watch'],
    [['new', 'app', '--skip'], 'new', '--skip', '--skip-install'],
    [['export', '--nobuild'], 'export', '--nobuild', '--no-build'],
    [['routes', '--JSON'], 'routes', '--JSON', '--json'],
    [['info', '--cdw', '..'], 'info', '--cdw', '--cwd'],
  ])('%j fails with a suggestion', async (args, command, typed, meant) => {
    const message = await usageError(args);

    expect(message).toBe(`netjes ${command}: unknown option '${typed}'; did you mean '${meant}'?`);
  });

  it('without a close match points to the command help', async () => {
    expect(await usageError(['build', '--check'])).toBe("netjes build: unknown option '--check'; run `netjes help build` for its options");
    expect(await usageError(['dev', '-x'])).toBe("netjes dev: unknown option '-x'; run `netjes help dev` for its options");
  });

  it.each([
    [['dev', '--port', '3000'], 'dev', '--port', '3000'],
    [['dev', '-p', '4000'], 'dev', '-p', '4000'],
    [['start', '--port=8080'], 'start', '--port', '8080'],
    [['dev', '--port'], 'dev', '--port', '<port>'],
    [['dev', '-p3000', '--no-watch'], 'dev', '-p', '3000'],
  ])('%j says how to set the port instead', async (args, command, typed, port) => {
    const message = await usageError(args);

    expect(message).toContain(`netjes ${command}: unknown option '${typed}'`);
    expect(message).toContain('has no port option');
    expect(message).toContain(`set ASPNETCORE_URLS=http://localhost:${port} when you run netjes ${command}`);
    // netjes runs the app without its launch profiles, so they would not set the port.
    expect(message).not.toContain('launch profile');
    expect(message).not.toContain('\n');
  });

  it('keeps the plain message for --port on commands that serve nothing', async () => {
    expect(await usageError(['build', '--port', '3000'])).toBe(
      "netjes build: unknown option '--port'; run `netjes help build` for its options",
    );
  });

  it('is an error before the command, too', async () => {
    expect(await usageError(['--bogus'])).toBe("netjes: unknown option '--bogus'; run `netjes --help` for the options");
  });

  it('makes netjes new create nothing', async () => {
    await usageError(['new', 'app', '--bogus']);

    expect(existsSync(path.join(temp, 'app'))).toBe(false);
  });
});

describe('option values', () => {
  it.each([
    [['new', 'app', '--name']],
    [['new', 'app', '--name', '--skip-install']],
  ])('a missing value is an error for %j', async (args) => {
    expect(await usageError(args)).toBe("netjes new: option '--name' needs a value: --name <Name>");
    expect(existsSync(path.join(temp, 'app'))).toBe(false);
  });

  it('takes a value that starts with a dash after =', async () => {
    const { io, calls } = recordingIo(temp);
    await fakeDll(temp, '-x.dll');

    expect(await run(['start', '--dll=-x.dll'], io)).toBe(0);

    expect(calls[0]?.args).toEqual([path.join(temp, '-x.dll')]);
  });

  it('a value for a flag is an error', async () => {
    expect(await usageError(['generate', '--check=yes'])).toBe("netjes generate: option '--check' takes no value");
  });

  it('a missing --cwd value is an error', async () => {
    expect(await usageError(['generate', '--cwd'])).toBe("netjes generate: option '--cwd' needs a value: --cwd <dir>");
  });
});

describe('arguments', () => {
  it.each([
    [['new', 'a', 'b'], 'new', 'b'],
    [['dev', 'now'], 'dev', 'now'],
    [['export', 'out', 'more'], 'export', 'more'],
  ])('one too many is an error for %j', async (args, command, extra) => {
    expect(await usageError(args)).toBe(`netjes ${command}: unexpected argument '${extra}'; run \`netjes help ${command}\` for its usage`);
  });

  it('one too many after help is an error', async () => {
    expect(await usageError(['help', 'new', 'more'])).toContain("unexpected argument 'more'");
  });
});
