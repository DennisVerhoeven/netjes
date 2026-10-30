// @vitest-environment node
import { existsSync } from 'node:fs';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { type SpawnCall, run } from '../cli/main.mjs';
import { fakeApp, fakeDll, recordingIo } from './cli-io.ts';

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url));

let temp = '';
beforeEach(async () => {
  temp = await mkdtemp(path.join(tmpdir(), 'netjes-cli-'));
});
afterEach(async () => {
  await rm(temp, { recursive: true, force: true });
});

describe('netjes new', () => {
  it('scaffolds an app router app with the default stack, named after the directory', async () => {
    const { io } = recordingIo(temp);

    const code = await run(['new', 'my-shop', '--skip-install'], io);

    const app = path.join(temp, 'my-shop');
    expect(code).toBe(0);
    for (const file of [
      'MyShop.csproj',
      'Program.cs',
      'Properties/launchSettings.json',
      'Features/Home/HomePage.cs',
      'app/layout.tsx',
      'app/page.tsx',
      'app/about/page.tsx',
      'app/not-found.tsx',
      'app/error.tsx',
      'components/Greeting.tsx',
      'components/Greeting.test.tsx',
      'components/HelloForm.tsx',
      'components/HelloForm.test.tsx',
      'styles/global.css',
      'public/favicon.svg',
      'generated/netjes.ts',
      'vite.config.ts',
      'tsconfig.json',
      'package.json',
      'Dockerfile',
      'docker-compose.yml',
      'docker-compose.override.yml',
      'docker-compose.prod.yml',
      '.dockerignore',
      '.gitignore',
      'README.md',
    ]) {
      expect(existsSync(path.join(app, file)), file).toBe(true);
    }
    expect(existsSync(path.join(app, 'pages'))).toBe(false);
    expect(JSON.parse(await readFile(path.join(app, 'tsconfig.json'), 'utf8')).include).toContain('app');
    expect(JSON.parse(await readFile(path.join(app, 'tsconfig.json'), 'utf8')).include).not.toContain('pages');
    expect(await readFile(path.join(app, 'Program.cs'), 'utf8')).toContain('using MyShop;');
    expect(JSON.parse(await readFile(path.join(app, 'package.json'), 'utf8')).name).toBe('my-shop');
    expect(await readFile(path.join(app, 'Dockerfile'), 'utf8')).toContain('MyShop.dll');
    expect(await readFile(path.join(app, 'styles/global.css'), 'utf8')).toContain("@import 'tailwindcss';");
    // So a checkout with core.autocrlf does not make `netjes generate --check` fail.
    expect(await readFile(path.join(app, '.gitattributes'), 'utf8')).toContain('generated/** text eol=lf');
  });

  it('writes an AGENTS.md for coding agents, which CLAUDE.md imports', async () => {
    const { io } = recordingIo(temp);

    await run(['new', 'my-shop', '--skip-install'], io);

    const app = path.join(temp, 'my-shop');
    const agents = await readFile(path.join(app, 'AGENTS.md'), 'utf8');
    expect(agents).toMatch(/^# My Shop\n/);
    expect(agents).toContain('https://raw.githubusercontent.com/DennisVerhoeven/netjes/main/llms.txt');
    expect(agents).toContain('namespace MyShop.Features');
    expect(agents).toContain('npx netjes generate');
    expect(agents).not.toMatch(/__[A-Za-z]+__/);
    expect(await readFile(path.join(app, 'CLAUDE.md'), 'utf8')).toBe('@AGENTS.md\n');
  });

  it("keeps the template's agent files under names that agents working on Netjes do not load", async () => {
    // Agents apply the nearest AGENTS.md or CLAUDE.md to the files below it: the app's guide would steer changes
    // to the template itself.
    const template = path.join(repoRoot, 'packages', 'netjes', 'template');

    for (const name of ['AGENTS.md', 'CLAUDE.md']) {
      expect(existsSync(path.join(template, name)), name).toBe(false);
      expect(existsSync(path.join(template, `_${name}`)), `_${name}`).toBe(true);
    }
  });

  it('copies the Netjes skill for coding agents into .claude/skills, as the npm package ships it', async () => {
    const { io } = recordingIo(temp);

    await run(['new', 'app', '--skip-install'], io);

    const shipped = path.join(repoRoot, 'packages', 'netjes', 'skills', 'netjes');
    const copied = path.join(temp, 'app', '.claude', 'skills', 'netjes');
    const files = ['SKILL.md', ...['routing', 'data-and-actions', 'caching-and-streaming', 'testing', 'deployment'].map((r) => `references/${r}.md`)];
    for (const file of files) {
      expect(await readFile(path.join(copied, file), 'utf8'), file).toBe(await readFile(path.join(shipped, file), 'utf8'));
    }
    expect(await readFile(path.join(copied, 'SKILL.md'), 'utf8')).toMatch(/^---\nname: netjes\ndescription: \S/);
    const manifest = JSON.parse(await readFile(path.join(repoRoot, 'packages', 'netjes', 'package.json'), 'utf8'));
    expect(manifest.files).toContain('skills');
  });

  it('references this checkout when run from one', async () => {
    const { io } = recordingIo(temp);

    await run(['new', 'app', '--skip-install'], io);

    const app = path.join(temp, 'app');
    const csproj = await readFile(path.join(app, 'App.csproj'), 'utf8');
    const relative = path.relative(app, repoRoot).split(path.sep).join('/');
    expect(csproj).toContain(`<ProjectReference Include="${relative}/src/Netjes/Netjes.csproj" />`);
    expect(csproj).toContain(`<Import Project="${relative}/src/Netjes/build/Netjes.targets" />`);
    const manifest = JSON.parse(await readFile(path.join(app, 'package.json'), 'utf8'));
    expect(manifest.dependencies.netjes).toBe(`file:${relative}/packages/netjes`);
  });

  it('uses package references with --packages, pinned to this exact version on both sides', async () => {
    const { io } = recordingIo(temp);

    await run(['new', 'app', '--skip-install', '--packages'], io);

    // The C# and npm halves speak one payload protocol, and a NuGet minimum version would accept any later
    // package with that id, so both are pinned.
    const app = path.join(temp, 'app');
    const version = JSON.parse(await readFile(path.join(repoRoot, 'packages', 'netjes', 'package.json'), 'utf8')).version;
    expect(await readFile(path.join(app, 'App.csproj'), 'utf8')).toContain(`<PackageReference Include="Netjes" Version="[${version}]" />`);
    expect(JSON.parse(await readFile(path.join(app, 'package.json'), 'utf8')).dependencies.netjes).toBe(version);
  });

  it('installs npm dependencies unless told not to', async () => {
    const { io, calls } = recordingIo(temp);

    await run(['new', 'app'], io);

    expect(calls).toEqual([expect.objectContaining({ command: 'npm', args: ['install'], cwd: path.join(temp, 'app') })]);
  });

  it('refuses a directory that is not empty', async () => {
    await mkdir(path.join(temp, 'taken'));
    await writeFile(path.join(temp, 'taken', 'file.txt'), 'x');
    const { io, err } = recordingIo(temp);

    expect(await run(['new', 'taken', '--skip-install'], io)).toBe(1);
    expect(err()).toContain('is not empty');
  });
});

describe('netjes agents', () => {
  const shipped = path.join(repoRoot, 'packages', 'netjes', 'skills', 'netjes');
  const skill = (dir: string) => path.join(dir, '.claude', 'skills', 'netjes');

  it('adds AGENTS.md, CLAUDE.md and the Netjes skill to an existing app, named after its project file', async () => {
    await writeFile(path.join(temp, 'Shop.Web.csproj'), '<Project Sdk="Microsoft.NET.Sdk.Web" />\n');
    const { io, out } = recordingIo(temp);

    expect(await run(['agents'], io)).toBe(0);

    const agents = await readFile(path.join(temp, 'AGENTS.md'), 'utf8');
    expect(agents).toMatch(/^# Shop Web\n/);
    expect(agents).toContain('namespace Shop.Web.Features');
    expect(await readFile(path.join(temp, 'CLAUDE.md'), 'utf8')).toBe('@AGENTS.md\n');
    for (const file of ['SKILL.md', 'references/routing.md', 'references/testing.md']) {
      expect(await readFile(path.join(skill(temp), file), 'utf8'), file).toBe(await readFile(path.join(shipped, file), 'utf8'));
    }
    // Where the files went, once, then what happened to each.
    expect(out().startsWith(`In ${temp}:\n`), out()).toBe(true);
    expect(out()).toContain('  wrote AGENTS.md\n');
    expect(out()).toContain('  wrote .claude/skills/netjes/SKILL.md\n');
    expect(out()).not.toContain('kept');
  });

  it('writes into the directory it is given, like export', async () => {
    const app = path.join(temp, 'my-app');
    await mkdir(app);
    await writeFile(path.join(app, 'MyApp.csproj'), '<Project Sdk="Microsoft.NET.Sdk.Web" />\n');
    const { io, out } = recordingIo(temp);

    expect(await run(['agents', 'my-app'], io)).toBe(0);

    expect(await readFile(path.join(app, 'AGENTS.md'), 'utf8')).toMatch(/^# My App\n/);
    expect(await readFile(path.join(app, 'CLAUDE.md'), 'utf8')).toBe('@AGENTS.md\n');
    expect(existsSync(path.join(skill(app), 'SKILL.md'))).toBe(true);
    expect(existsSync(path.join(temp, 'AGENTS.md'))).toBe(false);
    expect(existsSync(path.join(temp, '.claude'))).toBe(false);
    expect(out()).toContain(`In ${app}:\n`);
  });

  it('names the app after its directory when it has no project file, or several', async () => {
    const lone = path.join(temp, 'corner-shop');
    const several = path.join(temp, 'shop-suite');
    await mkdir(lone);
    await mkdir(several);
    await writeFile(path.join(several, 'Shop.Api.csproj'), '<Project Sdk="Microsoft.NET.Sdk.Web" />\n');
    await writeFile(path.join(several, 'Shop.Web.csproj'), '<Project Sdk="Microsoft.NET.Sdk.Web" />\n');
    const { io } = recordingIo(temp);

    expect(await run(['agents', 'corner-shop'], io)).toBe(0);
    expect(await run(['agents', 'shop-suite'], io)).toBe(0);

    const lonely = await readFile(path.join(lone, 'AGENTS.md'), 'utf8');
    expect(lonely).toMatch(/^# Corner Shop\n/);
    expect(lonely).toContain('namespace CornerShop.Features');
    const suite = await readFile(path.join(several, 'AGENTS.md'), 'utf8');
    expect(suite).toMatch(/^# Shop Suite\n/);
    expect(suite).toContain('namespace ShopSuite.Features');
  });

  it('warns on stderr, and still writes, when the folder has no project file or several', async () => {
    const lone = path.join(temp, 'corner-shop');
    const several = path.join(temp, 'shop-suite');
    const named = path.join(temp, 'named-shop');
    await mkdir(lone);
    await mkdir(several);
    await mkdir(named);
    await writeFile(path.join(several, 'Shop.Api.csproj'), '<Project Sdk="Microsoft.NET.Sdk.Web" />\n');
    await writeFile(path.join(several, 'Shop.Web.csproj'), '<Project Sdk="Microsoft.NET.Sdk.Web" />\n');

    const none = recordingIo(temp);
    expect(await run(['agents', 'corner-shop'], none.io)).toBe(0);
    expect(none.err()).toContain(`netjes agents: no .csproj in ${lone}`);
    expect(none.err()).toContain('"corner-shop"');
    expect(none.err()).toContain('--name');
    expect(existsSync(path.join(lone, 'AGENTS.md'))).toBe(true);

    const many = recordingIo(temp);
    expect(await run(['agents', 'shop-suite'], many.io)).toBe(0);
    expect(many.err()).toContain(`netjes agents: more than one .csproj in ${several}`);
    expect(existsSync(path.join(several, 'AGENTS.md'))).toBe(true);

    const given = recordingIo(temp);
    expect(await run(['agents', 'named-shop', '--name', 'Shop'], given.io)).toBe(0);
    expect(given.err()).toBe('');
  });

  it('resolves that directory against --cwd, and fails when it does not exist', async () => {
    await mkdir(path.join(temp, 'apps', 'shop'), { recursive: true });
    const { io } = recordingIo(temp);

    expect(await run(['agents', 'shop', '--cwd', 'apps'], io)).toBe(0);
    expect(existsSync(path.join(temp, 'apps', 'shop', 'AGENTS.md'))).toBe(true);

    const missing = recordingIo(temp);
    expect(await run(['agents', 'nowhere'], missing.io)).toBe(1);
    expect(missing.err()).toContain(path.join(temp, 'nowhere'));
  });

  it('keeps the files that exist and says how to replace them', async () => {
    await writeFile(path.join(temp, 'AGENTS.md'), 'Our own rules.\n');
    await writeFile(path.join(temp, 'CLAUDE.md'), 'Our own Claude notes.\n');
    await mkdir(skill(temp), { recursive: true });
    await writeFile(path.join(skill(temp), 'SKILL.md'), 'An older skill.\n');
    const { io, out } = recordingIo(temp);

    expect(await run(['agents'], io)).toBe(0);

    expect(await readFile(path.join(temp, 'AGENTS.md'), 'utf8')).toBe('Our own rules.\n');
    expect(await readFile(path.join(temp, 'CLAUDE.md'), 'utf8')).toBe('Our own Claude notes.\n');
    expect(await readFile(path.join(skill(temp), 'SKILL.md'), 'utf8')).toBe('An older skill.\n');
    expect(await readFile(path.join(skill(temp), 'references', 'routing.md'), 'utf8')).toBe(
      await readFile(path.join(shipped, 'references', 'routing.md'), 'utf8'),
    );
    for (const file of ['AGENTS.md', 'CLAUDE.md', '.claude/skills/netjes/SKILL.md']) expect(out()).toContain(`  kept  ${file}\n`);
    expect(out()).toContain('  wrote .claude/skills/netjes/references/routing.md\n');
    expect(out()).toContain('Files that exist are kept; --force replaces them.\n');
    // Claude Code reads CLAUDE.md, not AGENTS.md.
    expect(out()).toContain('Add a line `@AGENTS.md` to CLAUDE.md');
  });

  it('replaces them with --force', async () => {
    await writeFile(path.join(temp, 'AGENTS.md'), 'Our own rules.\n');
    await writeFile(path.join(temp, 'CLAUDE.md'), 'Our own Claude notes.\n');
    await mkdir(skill(temp), { recursive: true });
    await writeFile(path.join(skill(temp), 'SKILL.md'), 'An older skill.\n');
    const { io, out } = recordingIo(temp);

    expect(await run(['agents', '--force', '--name', 'Corner Shop'], io)).toBe(0);

    expect(await readFile(path.join(temp, 'AGENTS.md'), 'utf8')).toMatch(/^# Corner Shop\n/);
    expect(await readFile(path.join(temp, 'CLAUDE.md'), 'utf8')).toBe('@AGENTS.md\n');
    expect(await readFile(path.join(skill(temp), 'SKILL.md'), 'utf8')).toBe(await readFile(path.join(shipped, 'SKILL.md'), 'utf8'));
    expect(out()).toContain('  wrote AGENTS.md\n');
    expect(out()).not.toContain('kept');
  });

  it('says nothing about CLAUDE.md when it already imports AGENTS.md', async () => {
    await writeFile(path.join(temp, 'CLAUDE.md'), '# Notes\n\n@AGENTS.md\n');
    const { io, out } = recordingIo(temp);

    await run(['agents'], io);

    expect(out()).toContain('  kept  CLAUDE.md\n');
    expect(out()).not.toContain('Add a line');
  });

  it('fails for a directory that does not exist', async () => {
    const { io, err } = recordingIo(temp);

    expect(await run(['agents', '--cwd', 'missing'], io)).toBe(1);

    expect(err()).toContain(path.join(temp, 'missing'));
    expect(existsSync(path.join(temp, 'missing'))).toBe(false);
  });
});

describe('inherited NETJES_ variables', () => {
  // `routes` reads the report its app run wrote.
  const writeReport = async (call: SpawnCall) => {
    if (call.env.NETJES_OUTPUT && call.env.NETJES_COMMAND === 'routes') await writeFile(call.env.NETJES_OUTPUT, '{}');
    return 0;
  };

  it.each([
    [['dev'], []],
    [['start'], []],
    [['start', '--dll', 'App.dll'], []],
    // build runs `generate` first.
    [['build'], ['generate']],
    [['generate'], ['generate']],
    [['routes', '--json'], ['routes']],
    [['export', '--no-build'], ['export']],
  ])('do not turn the processes %j starts into another command', async (args, ownCommands) => {
    await fakeApp(temp);
    await fakeDll(temp, 'App.dll');
    const { io, calls } = recordingIo(temp, writeReport);
    // Left over in a CI job or shell, e.g. from an earlier `netjes export`.
    io.env = { ...io.env, NETJES_COMMAND: 'export', netjes_command: 'export', NETJES_OUTPUT: '/', NETJES_CHECK: '1' };

    await run(args, io);

    expect(calls.length).toBeGreaterThan(0);
    const commands = calls.map((call) => call.env.NETJES_COMMAND).filter((command) => command !== undefined);
    expect(commands).toEqual(ownCommands);
    for (const call of calls) {
      expect(call.env.netjes_command).toBeUndefined();
      expect(call.env.NETJES_CHECK).toBeUndefined();
      expect(call.env.NETJES_OUTPUT).not.toBe('/');
    }
  });

  it.each([
    [['dev']],
    [['start']],
    [['start', '--dll', 'App.dll']],
    [['build']],
    [['generate']],
    [['routes', '--json']],
    [['export', '--no-build']],
  ])('leave user settings and app configuration to the processes %j starts', async (args) => {
    await fakeApp(temp);
    await fakeDll(temp, 'App.dll');
    const { io, calls } = recordingIo(temp, writeReport);
    io.env = {
      ...io.env,
      Netjes__ViteDevPort: '5999',
      NETJES__MODE: 'Production',
      NETJES_VITE_HOST: '0.0.0.0',
      NETJES_VITE_ORIGIN: 'http://example.test:5173',
    };

    await run(args, io);

    expect(calls.length).toBeGreaterThan(0);
    for (const call of calls) {
      expect(call.env.Netjes__ViteDevPort).toBe('5999');
      expect(call.env.NETJES__MODE).toBe('Production');
      expect(call.env.NETJES_VITE_HOST).toBe('0.0.0.0');
      expect(call.env.NETJES_VITE_ORIGIN).toBe('http://example.test:5173');
    }
  });
});

describe('commands that run the app', () => {
  beforeEach(async () => {
    await fakeApp(temp);
  });

  it('generate runs the app in command mode without binding a real port', async () => {
    const { io, calls } = recordingIo(temp);

    expect(await run(['generate'], io)).toBe(0);

    expect(calls).toHaveLength(1);
    expect(calls[0]).toMatchObject({ command: 'dotnet', args: ['run', '--no-launch-profile'], cwd: temp });
    expect(calls[0]?.env).toMatchObject({
      NETJES_COMMAND: 'generate',
      ASPNETCORE_URLS: 'http://127.0.0.1:0',
      ASPNETCORE_ENVIRONMENT: 'Development',
    });
    expect(calls[0]?.env.NETJES_CHECK).toBeUndefined();
  });

  it('generate --check passes the check flag and the exit code through', async () => {
    const { io, calls } = recordingIo(temp, () => 1);

    expect(await run(['generate', '--check'], io)).toBe(1);

    expect(calls[0]?.env.NETJES_CHECK).toBe('1');
  });

  it('dev runs dotnet watch in Development', async () => {
    const { io, calls } = recordingIo(temp);

    await run(['dev'], io);

    expect(calls[0]).toMatchObject({ command: 'dotnet', args: ['watch', 'run', '--no-launch-profile'] });
    expect(calls[0]?.env).toMatchObject({ ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: 'http://localhost:5080' });
  });

  it('dev keeps a URL the environment already sets', async () => {
    const { io, calls } = recordingIo(temp);
    io.env.ASPNETCORE_URLS = 'http://localhost:9000';

    await run(['dev', '--no-watch'], io);

    expect(calls[0]).toMatchObject({ command: 'dotnet', args: ['run', '--no-launch-profile'] });
    expect(calls[0]?.env.ASPNETCORE_URLS).toBe('http://localhost:9000');
  });

  it('build generates the types, then the client and server bundles, and stops at the first failure', async () => {
    const vite = await fakeApp(temp);
    const { io, calls } = recordingIo(temp);

    expect(await run(['build'], io)).toBe(0);
    expect(calls.map((c) => [c.command, ...c.args].join(' '))).toEqual([
      'dotnet run --no-launch-profile',
      `${process.execPath} ${vite} build`,
      `${process.execPath} ${vite} build --ssr`,
    ]);

    const failing = recordingIo(temp, (call) => (call.command === process.execPath ? 2 : 0));
    expect(await run(['build'], failing.io)).toBe(2);
    expect(failing.calls).toHaveLength(2);
  });

  it("build runs the app's own vite and never lets npx fetch one when it is missing", async () => {
    await rm(path.join(temp, 'node_modules'), { recursive: true });
    const { io, calls, err } = recordingIo(temp);

    expect(await run(['build'], io)).toBe(1);

    expect(calls).toEqual([]);
    expect(err()).toMatch(/vite.*npm install/s);
  });

  it('names no command in the missing-vite message, since export builds too', async () => {
    await rm(path.join(temp, 'node_modules'), { recursive: true });
    const { io, err } = recordingIo(temp);

    await run(['export'], io);

    expect(err()).toMatch(/^netjes: vite is not installed/);
  });

  it('start runs the app in Production, or a published dll', async () => {
    const { io, calls } = recordingIo(temp);
    await fakeDll(temp, 'out/App.dll');

    await run(['start'], io);
    await run(['start', '--dll', 'out/App.dll'], io);

    expect(calls[0]).toMatchObject({ command: 'dotnet', args: ['run', '--no-launch-profile', '-c', 'Release'] });
    expect(calls[0]?.env.ASPNETCORE_ENVIRONMENT).toBe('Production');
    expect(calls[1]).toMatchObject({ command: 'dotnet', args: [path.join(temp, 'out', 'App.dll')] });
  });

  it('routes prints the route table the app reports', async () => {
    const { io, out } = recordingIo(temp, async (call) => {
      await writeFile(
        call.env.NETJES_OUTPUT!,
        JSON.stringify({
          routes: [
            { pattern: '/', template: '/', file: 'app/page.tsx', layouts: [], loader: true, action: false, props: 'HomeProps', cached: false, authorized: false },
            { pattern: '/blog/[slug]', template: '/blog/{slug}', file: 'app/blog/[slug]/page.tsx', layouts: ['app/layout.tsx'], loader: true, action: true, props: 'PostProps', cached: true, authorized: true },
          ],
          endpoints: [{ method: 'GET', route: '/api/likes', name: 'HTTP: GET /api/likes' }],
        }),
      );
      return 0;
    });

    expect(await run(['routes'], io)).toBe(0);

    const lines = out().split('\n');
    expect(lines[0]).toMatch(/^Pattern\s+File\s+Loader\s+Action\s+Props\s+Cached\s+Auth$/);
    expect(lines.find((l) => l.startsWith('/blog/[slug]'))).toMatch(/app\/blog\/\[slug\]\/page\.tsx\s+yes\s+yes\s+PostProps\s+yes\s+yes/);
    expect(out()).toContain('GET  /api/likes');
  });

  it('routes prints the warnings the app reports after the table', async () => {
    const overlap = 'app/[id]/page.tsx wins over app/[...rest]/page.tsx for URLs like /[id]: both match them.';
    const binding = "The loader of app/blog/[slug]/page.tsx binds 'id' from the query string.";
    const { io, out } = recordingIo(temp, async (call) => {
      await writeFile(
        call.env.NETJES_OUTPUT!,
        JSON.stringify({
          routes: [{ pattern: '/[id]', template: '/{id}', file: 'app/[id]/page.tsx', layouts: [], loader: false, action: false, props: null, cached: false, authorized: false }],
          endpoints: [],
          warnings: [
            { kind: 'query-binding', message: binding, files: ['app/blog/[slug]/page.tsx'] },
            { kind: 'overlap', message: overlap, files: ['app/[id]/page.tsx', 'app/[...rest]/page.tsx'] },
          ],
        }),
      );
      return 0;
    });

    // Warnings are not failures.
    expect(await run(['routes'], io)).toBe(0);

    const lines = out().split('\n');
    const heading = lines.indexOf('Warnings');
    expect(heading).toBeGreaterThan(lines.findIndex((l) => l.startsWith('/[id]')));
    expect(lines.slice(heading + 1, heading + 3)).toEqual([`  ${binding}`, `  ${overlap}`]);
  });

  it('routes prints no warnings section when there are none', async () => {
    const { io, out } = recordingIo(temp, async (call) => {
      await writeFile(call.env.NETJES_OUTPUT!, JSON.stringify({ routes: [], endpoints: [], warnings: [] }));
      return 0;
    });

    expect(await run(['routes'], io)).toBe(0);
    expect(out()).not.toContain('Warnings');
  });

  it('export builds, then runs the app in Production to write the static site', async () => {
    const vite = await fakeApp(temp);
    const { io, calls } = recordingIo(temp);

    expect(await run(['export'], io)).toBe(0);

    expect(calls.map((c) => [c.command, ...c.args].join(' '))).toEqual([
      'dotnet run --no-launch-profile',
      `${process.execPath} ${vite} build`,
      `${process.execPath} ${vite} build --ssr`,
      'dotnet run --no-launch-profile -c Release',
    ]);
    expect(calls[3]?.env).toMatchObject({
      NETJES_COMMAND: 'export',
      NETJES_OUTPUT: path.join(temp, 'out'),
      ASPNETCORE_ENVIRONMENT: 'Production',
      ASPNETCORE_URLS: 'http://127.0.0.1:0',
    });
  });

  it('export --no-build writes to the given directory and passes the exit code through', async () => {
    const { io, calls } = recordingIo(temp, () => 1);

    expect(await run(['export', 'site', '--no-build'], io)).toBe(1);

    expect(calls).toHaveLength(1);
    expect(calls[0]?.env.NETJES_OUTPUT).toBe(path.join(temp, 'site'));
  });

  it('runs in the directory given with --cwd', async () => {
    await fakeApp(path.join(temp, 'sub'));
    const { io, calls } = recordingIo(temp);

    await run(['generate', '--cwd', 'sub'], io);

    expect(calls[0]?.cwd).toBe(path.join(temp, 'sub'));
  });
});

describe('netjes info', () => {
  it('flags what is missing', async () => {
    const { io, out } = recordingIo(temp);

    expect(await run(['info'], io)).toBe(1);

    expect(out()).toMatch(/✗ No \.csproj/);
    expect(out()).toMatch(/✗ No vite\.config/);
    expect(out()).toMatch(/✗ No app\/ directory/);
  });

  it('flags page files in pages/, which Netjes does not route', async () => {
    await run(['new', 'app', '--skip-install'], recordingIo(temp).io);
    await mkdir(path.join(temp, 'app', 'pages', 'blog'), { recursive: true });
    await writeFile(path.join(temp, 'app', 'pages', 'blog', '[slug].tsx'), 'export default () => null;\n');
    const { io, out } = recordingIo(path.join(temp, 'app'), () => 0);

    expect(await run(['info'], io)).toBe(1);

    expect(out()).toContain(
      '✗ pages/ has pages router files, but Netjes routes app/ only\n' +
        '  Move them to their App Router names: pages/blog/[slug].tsx to app/blog/[slug]/page.tsx\n',
    );
  });

  it('flags page files in pages/ whatever the case of their extension', async () => {
    await mkdir(path.join(temp, 'pages'));
    await writeFile(path.join(temp, 'pages', 'About.TSX'), 'export default () => null;\n');
    const { io, out } = recordingIo(temp);

    await run(['info'], io);

    expect(out()).toContain('Move them to their App Router names: pages/About.TSX to app/About/page.TSX\n');
  });

  // The Next.js pages router's special files, at the root of pages/ only.
  it.each([
    ['index.tsx', 'app/page.tsx'],
    ['blog/index.jsx', 'app/blog/page.jsx'],
    ['_app.tsx', 'app/layout.tsx'],
    ['_document.jsx', 'app/layout.jsx'],
    ['404.tsx', 'app/not-found.tsx'],
    ['500.tsx', 'app/error.tsx'],
    ['_error.tsx', 'app/error.tsx'],
    ['blog/404.tsx', 'app/blog/404/page.tsx'],
    // JavaScript pages, as create-next-app writes them: app/ routes .jsx, not .js.
    ['index.js', 'app/page.jsx'],
    ['blog/[slug].js', 'app/blog/[slug]/page.jsx'],
    ['_app.js', 'app/layout.jsx'],
    ['_document.js', 'app/layout.jsx'],
    ['404.js', 'app/not-found.jsx'],
    ['About.JS', 'app/About/page.JSX'],
  ])('names the App Router file of pages/%s', async (file, appFile) => {
    await mkdir(path.dirname(path.join(temp, 'pages', file)), { recursive: true });
    await writeFile(path.join(temp, 'pages', file), 'export default () => null;\n');
    const { io, out } = recordingIo(temp);

    await run(['info'], io);

    expect(out()).toContain(`Move them to their App Router names: pages/${file} to ${appFile}`);
  });

  it('names every special file first, and counts the pages after the first three', async () => {
    // [locale]/ sorts before _app.tsx; the special files do not move the same way as pages, so each is named.
    await mkdir(path.join(temp, 'pages', '[locale]'), { recursive: true });
    for (const file of ['_app.tsx', '_document.tsx', '404.tsx', '500.tsx', ...[1, 2, 3, 4].map((i) => `[locale]/p${i}.tsx`)]) {
      await writeFile(path.join(temp, 'pages', file), 'export default () => null;\n');
    }
    const { io, out } = recordingIo(temp);

    await run(['info'], io);

    expect(out()).toContain(
      'Move them to their App Router names: pages/404.tsx to app/not-found.tsx, pages/500.tsx to app/error.tsx, ' +
        'pages/_app.tsx to app/layout.tsx, pages/_document.tsx to app/layout.tsx, ' +
        'pages/[locale]/p1.tsx to app/[locale]/p1/page.tsx, pages/[locale]/p2.tsx to app/[locale]/p2/page.tsx, ' +
        'pages/[locale]/p3.tsx to app/[locale]/p3/page.tsx, and 1 more the same way',
    );
  });

  it.each([
    ['_app.tsx', true],
    ['_document.tsx', true],
    ['about.tsx', false],
  ])('says what the root layout does for pages/%s: %s', async (file, says) => {
    await mkdir(path.join(temp, 'pages'));
    await writeFile(path.join(temp, 'pages', file), 'export default () => null;\n');
    const { io, out } = recordingIo(temp);

    await run(['info'], io);

    expect(out().includes('. The root layout wraps every page; Netjes renders <html>, <head> and <body> around it\n')).toBe(says);
  });

  it.each(['api/hello.ts', 'api/users/[id].js', 'api/index.tsx', 'api/auth/[...nextauth].JSX'])(
    'flags pages/%s as an API route, not a page',
    async (file) => {
      await mkdir(path.dirname(path.join(temp, 'pages', file)), { recursive: true });
      await writeFile(path.join(temp, 'pages', file), 'export default () => null;\n');
      const { io, out } = recordingIo(temp);

      expect(await run(['info'], io)).toBe(1);

      expect(out()).toContain(
        `✗ pages/api/ has API routes (pages/${file}), which Netjes does not serve\n` +
          '  Map them as C# minimal API endpoints instead, with app.MapGet or app.MapPost\n',
      );
      expect(out()).not.toContain('app/api');
    },
  );

  it('counts the API routes after the first three', async () => {
    await mkdir(path.join(temp, 'pages', 'api'), { recursive: true });
    for (const i of [1, 2, 3, 4, 5]) await writeFile(path.join(temp, 'pages', 'api', `a${i}.ts`), 'export default () => null;\n');
    const { io, out } = recordingIo(temp);

    await run(['info'], io);

    expect(out()).toContain('(pages/api/a1.ts, pages/api/a2.ts, pages/api/a3.ts, and 2 more), which Netjes does not serve');
  });

  it('leaves pages/ alone when it holds nothing the pages router served', async () => {
    await mkdir(path.join(temp, 'pages', '_components'), { recursive: true });
    await mkdir(path.join(temp, 'pages', 'blog'), { recursive: true });
    await mkdir(path.join(temp, 'pages', 'api', '_lib'), { recursive: true });
    for (const file of [
      '_layout.tsx',
      '_404.tsx',
      'blog/_app.tsx',
      '_components/Nav.tsx',
      '_components/_app.tsx',
      'index.test.tsx',
      'blog.test.js',
      'notes.md',
      'lib.ts',
      'api/hello.test.ts',
      'api/types.d.ts',
      'api/_db.ts',
      'api/_lib/db.ts',
      'api/schema.json',
    ]) {
      await writeFile(path.join(temp, 'pages', file), 'export default () => null;\n');
    }
    const { io, out } = recordingIo(temp);

    await run(['info'], io);

    expect(out()).not.toContain('pages/');
  });

  it('does not mistake a file called pages for the folder', async () => {
    await writeFile(path.join(temp, 'pages'), 'notes\n');
    const { io, out } = recordingIo(temp);

    expect(await run(['info'], io)).toBe(1);

    expect(out()).toMatch(/✗ No app\/ directory/);
    expect(out()).not.toContain('pages/');
  });
});

describe('netjes', () => {
  it('prints help for no command and fails for an unknown one', async () => {
    const help = recordingIo(temp);
    expect(await run([], help.io)).toBe(0);
    expect(help.out()).toContain('Usage: netjes <command>');
    expect(help.out()).toMatch(/^ {2}agents \[dir\] +Add AGENTS\.md/m);

    const agentsHelp = recordingIo(temp);
    expect(await run(['help', 'agents'], agentsHelp.io)).toBe(0);
    expect(agentsHelp.out()).toContain('Usage: netjes agents [dir] [options]');
    expect(agentsHelp.out()).toMatch(/^ {2}--name <Name> .*\n {2}--force /m);

    const unknown = recordingIo(temp);
    expect(await run(['launch'], unknown.io)).toBe(1);
    expect(unknown.err()).toContain("unknown command 'launch'");
  });
});
