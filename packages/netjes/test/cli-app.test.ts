// @vitest-environment node
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { run } from '../cli/main.mjs';
import { fakeApp, fakeDll, recordingIo } from './cli-io.ts';

let temp = '';
beforeEach(async () => {
  temp = await mkdtemp(path.join(tmpdir(), 'netjes-cli-app-'));
});
afterEach(async () => {
  await rm(temp, { recursive: true, force: true });
});

const APP_COMMANDS = [['dev'], ['build'], ['start'], ['generate'], ['routes'], ['export'], ['export', '--no-build']];

describe('a command that runs the app', () => {
  it.each(APP_COMMANDS)('%s says there is no app here instead of starting dotnet', async (...args) => {
    const { io, calls, out, err } = recordingIo(temp);

    expect(await run(args, io)).toBe(1);

    expect(calls).toEqual([]);
    expect(out()).toBe('');
    expect(err().trimEnd().split('\n')).toHaveLength(1);
    expect(err()).toContain(`netjes ${args[0]}: no .csproj in ${temp}`);
    expect(err()).toContain('--cwd');
    expect(err()).toContain('netjes new');
  });

  it('start --dll needs no project', async () => {
    const { io, calls } = recordingIo(temp);
    await fakeDll(temp, 'App.dll');

    expect(await run(['start', '--dll', 'App.dll'], io)).toBe(0);

    expect(calls).toHaveLength(1);
  });
});

describe('start --dll', () => {
  it.each([
    ['a folder that does not exist', 'bin/pubish/App.dll'],
    ['a file that does not exist in a folder that does', 'App.dll'],
  ])('says so for %s, instead of starting dotnet', async (_name, dll) => {
    const { io, calls, out, err } = recordingIo(temp);

    expect(await run(['start', '--dll', dll], io)).toBe(1);

    expect(calls).toEqual([]);
    expect(out()).toBe('');
    expect(err()).toBe(`netjes start: --dll ${dll} is not a file (${path.join(temp, dll)})\n`);
  });

  it("runs the dll from the dll's own folder, so the published copies are the app's content root", async () => {
    const { io, calls } = recordingIo(temp);
    await fakeDll(temp, 'bin/publish/App.dll');

    expect(await run(['start', '--dll', 'bin/publish/App.dll'], io)).toBe(0);

    expect(calls[0]?.command).toBe('dotnet');
    expect(calls[0]?.args).toEqual([path.join(temp, 'bin', 'publish', 'App.dll')]);
    expect(calls[0]?.cwd).toBe(path.join(temp, 'bin', 'publish'));
  });

  it('resolves a relative dll against --cwd', async () => {
    await fakeDll(temp, 'app/out/App.dll');
    const { io, calls } = recordingIo(temp);

    expect(await run(['start', '--dll', 'out/App.dll', '--cwd', 'app'], io)).toBe(0);

    expect(calls[0]?.args).toEqual([path.join(temp, 'app', 'out', 'App.dll')]);
    expect(calls[0]?.cwd).toBe(path.join(temp, 'app', 'out'));
  });

  it('keeps an absolute dll where it is', async () => {
    const { io, calls } = recordingIo(temp);
    const dll = await fakeDll(temp, 'elsewhere/App.dll');

    await run(['start', '--dll', dll], io);

    expect(calls[0]?.args).toEqual([dll]);
    expect(calls[0]?.cwd).toBe(path.join(temp, 'elsewhere'));
  });
});

describe('--cwd', () => {
  it.each([['info'], ['generate'], ['dev'], ['routes']])('%s fails on a directory that does not exist', async (command) => {
    const { io, calls, out, err } = recordingIo(temp);

    expect(await run([command, '--cwd', 'nope'], io)).toBe(1);

    expect(calls).toEqual([]);
    expect(out()).toBe('');
    expect(err()).toBe(`netjes ${command}: --cwd nope is not a directory (${path.join(temp, 'nope')})\n`);
  });

  it('fails on a file', async () => {
    await fakeApp(temp);
    const { io, err } = recordingIo(temp);

    expect(await run(['generate', '--cwd', 'App.csproj'], io)).toBe(1);

    expect(err()).toContain('is not a directory');
  });
});

describe('netjes dev', () => {
  it('asks for npm install when the packages are missing, instead of failing in the app', async () => {
    await fakeApp(temp, { installed: false });
    const { io, calls, err } = recordingIo(temp);

    expect(await run(['dev'], io)).toBe(1);

    expect(calls).toEqual([]);
    expect(err()).toBe('netjes dev: netjes and vite are not installed in this project. Run `npm install` first.\n');
  });
});

describe('netjes start', () => {
  it('says to build first when there is no build, and starts the app anyway', async () => {
    await fakeApp(temp);
    const { io, calls, err } = recordingIo(temp);

    expect(await run(['start'], io)).toBe(0);

    expect(err()).toContain('netjes build');
    expect(calls).toHaveLength(1);
  });

  it('says nothing when there is a build', async () => {
    await fakeApp(temp, { built: true });
    const { io, err } = recordingIo(temp);

    await run(['start'], io);

    expect(err()).toBe('');
  });

  it('says nothing for a published dll', async () => {
    await fakeDll(temp, 'publish/App.dll');
    const { io, err } = recordingIo(temp);

    await run(['start', '--dll', 'publish/App.dll'], io);

    expect(err()).toBe('');
  });
});

describe('command mode', () => {
  // `routes` and `generate` read the report its app run wrote.
  const writeReport = async (call: { env: Record<string, string | undefined> }) => {
    if (call.env.NETJES_COMMAND === 'routes') await writeFile(call.env.NETJES_OUTPUT!, '{"routes":[],"endpoints":[]}');
    return 0;
  };

  it('keeps stdout of routes for the report, so `netjes routes --json | jq` works', async () => {
    await fakeApp(temp);
    const { io, calls, out } = recordingIo(temp, writeReport);

    expect(await run(['routes', '--json'], io)).toBe(0);

    expect(calls[0]?.stdout).toBe('stderr');
    expect(JSON.parse(out())).toEqual({ routes: [], endpoints: [] });
  });

  it.each([[['generate']], [['routes']], [['export', '--no-build']], [['build']]])(
    'quiets the host lifetime logs (a throwaway port, "Press Ctrl+C") for %j',
    async (args) => {
      await fakeApp(temp);
      const { io, calls } = recordingIo(temp, writeReport);

      await run(args, io);

      const commandRuns = calls.filter((call) => call.env.NETJES_COMMAND);
      expect(commandRuns.length).toBeGreaterThan(0);
      for (const call of commandRuns) expect(call.env['Logging__LogLevel__Microsoft.Hosting.Lifetime']).toBe('Warning');
    },
  );

  it('leaves a lifetime log level the environment sets', async () => {
    await fakeApp(temp);
    const { io, calls } = recordingIo(temp);
    io.env['Logging__LogLevel__Microsoft.Hosting.Lifetime'] = 'Information';

    await run(['generate'], io);

    expect(calls[0]?.env['Logging__LogLevel__Microsoft.Hosting.Lifetime']).toBe('Information');
  });

  it.each([[['dev']], [['start']]])('leaves the lifetime logs of %j, which print the URL', async (args) => {
    await fakeApp(temp);
    const { io, calls } = recordingIo(temp);

    await run(args, io);

    expect(calls[0]?.env['Logging__LogLevel__Microsoft.Hosting.Lifetime']).toBeUndefined();
    expect(calls[0]?.stdout).toBeUndefined();
  });
});

describe('netjes dev ports', () => {
  it('says the port is taken instead of letting the app fail to bind', async () => {
    await fakeApp(temp);
    const { io, calls, err } = recordingIo(temp);
    const asked: number[] = [];
    io.portFree = async (port) => {
      asked.push(port);
      return false;
    };

    expect(await run(['dev'], io)).toBe(1);

    expect(asked).toEqual([5080]);
    expect(calls).toEqual([]);
    expect(err()).toBe(
      'netjes dev: port 5080 is in use (http://localhost:5080); stop what is using it, or pick another port: ASPNETCORE_URLS=http://localhost:5081 netjes dev\n',
    );
  });

  it('checks the port of a URL the environment sets', async () => {
    await fakeApp(temp);
    const { io, calls } = recordingIo(temp);
    const asked: number[] = [];
    io.env.ASPNETCORE_URLS = 'http://127.0.0.1:9000';
    io.portFree = async (port) => {
      asked.push(port);
      return true;
    };

    expect(await run(['dev'], io)).toBe(0);

    expect(asked).toEqual([9000, 5173]);
    expect(calls).toHaveLength(1);
  });

  it("says Vite's port is taken instead of letting the app fail to start", async () => {
    await fakeApp(temp);
    const { io, calls, err } = recordingIo(temp);
    const asked: [number, string | undefined][] = [];
    io.portFree = async (port, host) => {
      asked.push([port, host]);
      return port !== 5173;
    };

    expect(await run(['dev'], io)).toBe(1);

    // Where Vite listens: on localhost, which may be ::1 only.
    expect(asked).toEqual([
      [5080, undefined],
      [5173, 'localhost'],
    ]);
    expect(calls).toEqual([]);
    expect(err()).toBe(
      'netjes dev: port 5173 is in use, and the Vite dev server needs it; stop what is using it, or move Vite to another port: Netjes__ViteDevPort=5174 netjes dev\n',
    );
  });

  /** The Vite port `netjes dev` checks, with every port free. */
  async function vitePortChecked(io: ReturnType<typeof recordingIo>['io']) {
    const asked: number[] = [];
    io.portFree = async (port, host) => {
      if (host === 'localhost') asked.push(port);
      return true;
    };
    expect(await run(['dev'], io)).toBe(0);
    return asked;
  }

  it.each([['Netjes__ViteDevPort'], ['NETJES__VITEDEVPORT']])('checks the Vite port that %s sets', async (name) => {
    await fakeApp(temp);
    const { io } = recordingIo(temp);
    io.env[name] = '5999';

    expect(await vitePortChecked(io)).toEqual([5999]);
  });

  it.each<[string, Record<string, string>, Record<string, string>, number]>([
    ['appsettings.json', { 'appsettings.json': '{ "Netjes": { "ViteDevPort": 5174 } }' }, {}, 5174],
    [
      'appsettings.Development.json over appsettings.json',
      { 'appsettings.json': '{ "Netjes": { "ViteDevPort": 5174 } }', 'appsettings.Development.json': '{ "Netjes": { "ViteDevPort": 5175 } }' },
      {},
      5175,
    ],
    [
      'the environment over appsettings',
      { 'appsettings.Development.json': '{ "Netjes": { "ViteDevPort": 5175 } }' },
      { Netjes__ViteDevPort: '5176' },
      5176,
    ],
    [
      'the appsettings file of the environment the app runs in',
      { 'appsettings.Development.json': '{ "Netjes": { "ViteDevPort": 5175 } }', 'appsettings.Staging.json': '{ "Netjes": { "ViteDevPort": 5177 } }' },
      { ASPNETCORE_ENVIRONMENT: 'Staging' },
      5177,
    ],
    ['keys in any case, and a string', { 'appsettings.json': '{ "netjes": { "viteDevPort": "5178" } }' }, {}, 5178],
    ['a key with a colon', { 'appsettings.json': '{ "Netjes:ViteDevPort": 5179 }' }, {}, 5179],
    [
      'comments and trailing commas, as .NET reads them',
      { 'appsettings.json': '\uFEFF{\n  // Vite\n  "Urls": "http://localhost:5080", /* the app */\n  "Netjes": { "ViteDevPort": 5180, },\n}\n' },
      {},
      5180,
    ],
  ])('checks the Vite port from %s', async (_, files, env, port) => {
    await fakeApp(temp);
    for (const [file, text] of Object.entries(files)) await writeFile(path.join(temp, file), text);
    const { io } = recordingIo(temp);
    Object.assign(io.env, env);

    expect(await vitePortChecked(io)).toEqual([port]);
  });

  it('checks the Vite port on the address NETJES_VITE_HOST binds it to', async () => {
    await fakeApp(temp);
    const { io } = recordingIo(temp);
    io.env.NETJES_VITE_HOST = '0.0.0.0';
    const hosts: (string | undefined)[] = [];
    io.portFree = async (_port, host) => {
      hosts.push(host);
      return true;
    };

    expect(await run(['dev'], io)).toBe(0);

    expect(hosts).toEqual([undefined, '0.0.0.0']);
  });

  it('leaves the Vite port to the app when an appsettings file cannot be read', async () => {
    await fakeApp(temp);
    await writeFile(path.join(temp, 'appsettings.json'), '{ "Netjes": { "ViteDevPort": 5174 ');
    const { io } = recordingIo(temp);

    expect(await vitePortChecked(io)).toEqual([]);
  });

  it.each([['http://0.0.0.0:9000'], ['http://localhost:9000;https://localhost:9001'], ['http://+:80']])(
    'leaves %s to the app',
    async (urls) => {
      await fakeApp(temp);
      const { io, calls } = recordingIo(temp);
      io.env.ASPNETCORE_URLS = urls;
      // Any port the app would bind is taken; Vite's is free.
      io.portFree = async (_port, host) => host === 'localhost';

      expect(await run(['dev'], io)).toBe(0);

      expect(calls).toHaveLength(1);
    },
  );
});
