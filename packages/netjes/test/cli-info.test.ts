// @vitest-environment node
import { mkdir, mkdtemp, readFile, rm, utimes, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { NODE_RANGE, checkNode } from '../cli/info.mjs';
import { run } from '../cli/main.mjs';
import { recordingIo } from './cli-io.ts';

const root = fileURLToPath(new URL('..', import.meta.url));
const version = JSON.parse(await readFile(new URL('../package.json', import.meta.url), 'utf8')).version as string;

let temp = '';
let app = '';
beforeEach(async () => {
  temp = await mkdtemp(path.join(tmpdir(), 'netjes-cli-info-'));
  app = path.join(temp, 'app');
});
afterEach(async () => {
  await rm(temp, { recursive: true, force: true });
});

const PEERS = { react: '^19.0.0', 'react-dom': '^19.0.0', vite: '>=7' };

/** Packages in the app's node_modules, as npm install leaves them. */
async function install(dir: string, versions: Record<string, string>, peers: Record<string, string> = PEERS) {
  for (const [name, packageVersion] of Object.entries(versions)) {
    await mkdir(path.join(dir, 'node_modules', name), { recursive: true });
    const manifest = { name, version: packageVersion, ...(name === 'netjes' ? { peerDependencies: peers } : {}) };
    await writeFile(path.join(dir, 'node_modules', name, 'package.json'), JSON.stringify(manifest));
  }
}

const INSTALLED = { netjes: version, vite: '8.3.5', react: '19.3.1', 'react-dom': '19.3.1' };

/** A new app with its packages installed, and `netjes info` run in it with this `dotnet --version` result. */
async function info(dotnet: { code: number; stdout?: string; stderr?: string } = { code: 0, stdout: '10.0.403\n' }, args: string[] = []) {
  const recorded = recordingIo(app);
  recorded.io.capture = async () => ({ stdout: '', stderr: '', ...dotnet });
  const code = await run(['info', ...args], recorded.io);
  const lines = recorded.out().split('\n');
  return { code, out: recorded.out(), lines, err: recorded.err() };
}

async function newApp(...args: string[]) {
  expect(await run(['new', 'app', '--skip-install', ...args], recordingIo(temp).io)).toBe(0);
}

/** Every ✗ line is followed by an indented line that says how to fix it. */
function expectFixes(lines: string[]) {
  lines.forEach((line, index) => {
    if (line.startsWith('✗') || line.startsWith('!')) expect(lines[index + 1], `fix for: ${line}`).toMatch(/^ {2}\S/);
  });
}

describe('netjes info', () => {
  it('checks the tools and the project of a new app', async () => {
    await newApp();
    await install(app, INSTALLED);

    const { code, out, lines } = await info();

    expect(out).not.toMatch(/^[✗!]/m);
    expect(code).toBe(0);
    expect(lines[0]).toBe(`netjes ${version} in ${app}`);
    expect(out).toContain(`✓ Node.js ${process.versions.node}`);
    expect(out).toMatch(/^✓ \.NET SDK 10\.0\.403 \(App\.csproj targets net10\.0\)$/m);
    expect(out).toContain(`✓ npm packages: netjes ${version}, vite 8.3.5, react 19.3.1, react-dom 19.3.1`);
    expect(out).toMatch(/^✓ App\.csproj references the Netjes checkout/m);
    expect(out).toContain('✓ vite.config.ts uses netjes()');
    expect(out).toMatch(/^✓ app\/ has 2 pages$/m);
    expect(out).toMatch(/^✓ generated\/netjes\.ts/m);
  });

  it('checks that the NuGet and npm packages are one version', async () => {
    await newApp('--packages');
    await install(app, { ...INSTALLED, netjes: '0.0.0-other' });

    const { code, out, lines } = await info();

    expect(code).toBe(1);
    expect(out).toContain(`✗ App.csproj references Netjes ${version}, but node_modules has netjes 0.0.0-other`);
    expectFixes(lines);
  });

  it('shows the checkout a ProjectReference points to, and flags one that is not there', async () => {
    await newApp();
    await install(app, INSTALLED);
    const repoRoot = fileURLToPath(new URL('../../..', import.meta.url));

    expect((await info()).out).toContain(`✓ App.csproj references the Netjes checkout at ${path.join(repoRoot, 'src', 'Netjes', 'Netjes.csproj')}`);

    const csproj = path.join(app, 'App.csproj');
    await writeFile(csproj, (await readFile(csproj, 'utf8')).replace(/Include="[^"]*Netjes\.csproj"/, 'Include="../gone/src/Netjes/Netjes.csproj"'));
    const moved = await info();
    expect(moved.code).toBe(1);
    expect(moved.out).toContain(`✗ App.csproj references Netjes at ${path.join(temp, 'gone', 'src', 'Netjes', 'Netjes.csproj')}, which does not exist`);
    expectFixes(moved.lines);
  });

  it('passes the package versions when they match', async () => {
    await newApp('--packages');
    await install(app, INSTALLED);

    const { out } = await info();

    expect(out).toContain(`✓ App.csproj references Netjes ${version}, the version of the npm package`);
  });

  // Only an exact version can be compared with the npm package; NuGet also takes MSBuild properties, ranges and floats.
  it.each([['$(NetjesVersion)'], ['[$(NetjesVersion)]'], ['0.*'], ['[0.0.1,1.0.0)']])(
    'shows a version it cannot compare, %s, without failing',
    async (reference) => {
      await newApp('--packages');
      await install(app, INSTALLED);
      const csproj = path.join(app, 'App.csproj');
      await writeFile(csproj, (await readFile(csproj, 'utf8')).replace(/Version="[^"]*"/, `Version="${reference}"`));

      const { code, lines } = await info();

      expect(lines).toContain(`✓ App.csproj references Netjes ${reference}`);
      expect(code).toBe(0);
    },
  );

  it('says to run npm install when the packages are missing', async () => {
    await newApp();

    const { code, out, lines } = await info();

    expect(code).toBe(1);
    expect(out).toContain('✗ npm packages are not installed: netjes, vite, react, react-dom');
    expect(lines[lines.indexOf('✗ npm packages are not installed: netjes, vite, react, react-dom') + 1]).toContain('npm install');
  });

  it('flags a React that netjes does not support', async () => {
    await newApp();
    await install(app, { ...INSTALLED, react: '18.3.1', 'react-dom': '18.3.1' });

    const { code, out, lines } = await info();

    expect(code).toBe(1);
    expect(out).toContain(`✗ react 18.3.1 and react-dom 18.3.1 do not match what netjes ${version} needs (^19.0.0)`);
    expectFixes(lines);
  });

  it('flags a missing .NET SDK, with where to get one', async () => {
    await newApp();
    await install(app, INSTALLED);

    const { code, out, lines } = await info({ code: 127 });

    expect(code).toBe(1);
    expect(out).toContain('✗ .NET SDK not found');
    expect(lines[lines.indexOf('✗ .NET SDK not found') + 1]).toContain('https://dotnet.microsoft.com/download');
  });

  it('flags a .NET SDK older than the app targets', async () => {
    await newApp();
    await install(app, INSTALLED);

    const { code, out, lines } = await info({ code: 0, stdout: '8.0.404\n' });

    expect(code).toBe(1);
    expect(out).toContain('✗ .NET SDK 8.0.404 cannot build net10.0 (App.csproj); it needs 10.0 or newer');
    expectFixes(lines);
  });

  it('points at global.json when it asks for an SDK that is not installed', async () => {
    await newApp();
    await install(app, INSTALLED);
    await writeFile(path.join(temp, 'global.json'), JSON.stringify({ sdk: { version: '10.0.999' } }));

    const { code, out, lines } = await info({ code: 1, stderr: 'A compatible .NET SDK was not found.\n\nRequested SDK version: 10.0.999\n' });

    expect(code).toBe(1);
    expect(out).toContain('✗ .NET SDK: A compatible .NET SDK was not found.');
    const fix = lines[lines.indexOf('✗ .NET SDK: A compatible .NET SDK was not found.') + 1];
    expect(fix).toContain(path.join(temp, 'global.json'));
    expect(fix).toContain('10.0.999');
  });

  it('warns, without failing, when a C# file is newer than the generated types', async () => {
    await newApp();
    await install(app, INSTALLED);
    const old = new Date(Date.now() - 60_000);
    await utimes(path.join(app, 'generated', 'netjes.ts'), old, old);

    const { code, out, lines } = await info();

    expect(code).toBe(0);
    expect(out).toMatch(/^! generated\/netjes\.ts is older than (Features\/Home\/HomePage\.cs|Program\.cs|SiteProps\.cs)$/m);
    expectFixes(lines);
    expect(out).toContain('netjes generate');
  });

  it('warns when this netjes is not the one the app has installed', async () => {
    await newApp();
    await install(app, { ...INSTALLED, netjes: '0.0.0-other' });

    const { out } = await info();

    expect(out).toContain(`! This is netjes ${version}; the app has netjes 0.0.0-other`);
    expect(out).toContain('npx netjes info');
  });

  it('gives a fix for everything it flags outside an app', async () => {
    await mkdir(app);

    const { code, lines } = await info();

    expect(code).toBe(1);
    expect(lines.filter((line) => line.startsWith('✗')).length).toBeGreaterThanOrEqual(4);
    expectFixes(lines);
  });
});

describe('the Node.js check', () => {
  const range = '^22.22.2 || ^24.15.0 || >=26.0.0';

  it.each(['22.22.2', '22.99.0', '24.15.0', '24.22.1', '26.0.0', '26.12.0', '27.1.0', '30.0.0'])('Node.js %s passes', (node) => {
    const check = checkNode(node);

    expect(check).toEqual({ status: 'ok', text: `Node.js ${node}` });
  });

  it.each(['20.18.1', '21.7.3', '22.0.0', '22.12.0', '22.22.1', '24.0.0', '24.14.1'])('Node.js %s is too old', (node) => {
    const check = checkNode(node);

    expect(check.status).toBe('fail');
    expect(check.text).toBe(`Node.js ${node} is too old; Netjes needs ${range}`);
    expect(check.fix).toBe(`Install a Node.js release that matches ${range}: https://nodejs.org`);
  });

  it.each(['23.11.1', '25.9.0'])('Node.js %s, a release line between the supported ones, warns with the range', (node) => {
    const check = checkNode(node);

    expect(check.status).toBe('warn');
    expect(check.text).toBe(`Node.js ${node} is not a release line Netjes supports (${range})`);
    expect(check.fix).toBe(`Install a Node.js release that matches ${range}: https://nodejs.org`);
  });

  it('exports the range that the package and the template declare as engines', async () => {
    const read = async (file: string) => JSON.parse(await readFile(path.join(root, file), 'utf8'));

    expect(NODE_RANGE).toBe(range);
    expect((await read('package.json')).engines).toEqual({ node: NODE_RANGE });
    expect((await read('template/package.json')).engines).toEqual({ node: NODE_RANGE });
  });
});
