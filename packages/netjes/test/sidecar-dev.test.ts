// @vitest-environment node
import { type ChildProcess, spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdir, mkdtemp, realpath, rename, rm, symlink, writeFile } from 'node:fs/promises';
import { type AddressInfo, createServer } from 'node:net';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';

// The sidecar in development: it hosts a Vite dev server for an app in a temporary folder, whose node_modules is this
// workspace's. Vite's watcher is off, so Vite never learns of a file the test adds: what .NET routes as soon as its own
// watcher sees the file, Vite reports later, much later (a file it missed, once something else in its folder changes)
// or never (a folder it ignores).
const SIDECAR = fileURLToPath(new URL('../sidecar/server.mjs', import.meta.url));
const NODE_MODULES = fileURLToPath(new URL('../../../node_modules', import.meta.url));
const TOKEN = 'test-token';

let root = '';
/** The spawned process, kept from the moment it starts: `sidecar` is unset when startup times out, and the child must still die. */
let child: ChildProcess | undefined;
let sidecar: { port: number; vitePort: number };

async function write(file: string, content: string) {
  await mkdir(path.dirname(path.join(root, file)), { recursive: true });
  await writeFile(path.join(root, file), content);
}

/** The sidecar in development, with its Vite dev server on `vitePort` (0: a port the system picks), for the app in `appRoot`. */
function spawnSidecar(vitePort: number, appRoot = root) {
  return spawn(process.execPath, [SIDECAR], {
    env: { ...process.env, NETJES_MODE: 'development', NETJES_ROOT: appRoot, NETJES_TOKEN: TOKEN, NETJES_VITE_PORT: String(vitePort), NODE_ENV: 'development' },
    stdio: ['pipe', 'pipe', 'pipe'],
  });
}

/** Runs the sidecar until it exits, with what it wrote. */
async function runToExit(spawned: ChildProcess) {
  let stdout = '';
  let stderr = '';
  spawned.stdout?.setEncoding('utf8').on('data', (chunk: string) => (stdout += chunk));
  spawned.stderr?.setEncoding('utf8').on('data', (chunk: string) => (stderr += chunk));
  const [code] = await once(spawned, 'close');
  return { code, stdout, stderr };
}

/** Starts the sidecar, with its Vite dev server on a port the system picks (the config below reports it). */
function start(): Promise<typeof sidecar> {
  const spawned = spawnSidecar(0);
  child = spawned;
  spawned.stderr.resume();
  return new Promise((resolve, reject) => {
    let vitePort = 0;
    createInterface({ input: spawned.stdout }).on('line', (line) => {
      if (line.startsWith('VITE_PORT ')) vitePort = Number(line.slice(10));
      if (line.startsWith('NETJES_READY ')) resolve({ port: JSON.parse(line.slice(12)).port, vitePort });
    });
    spawned.on('exit', (code) => reject(new Error(`sidecar exited with ${code}`)));
  });
}

/** The payload .NET sends for a page under the root layout. */
function render(page: string) {
  return fetch(`http://127.0.0.1:${sidecar.port}/render`, {
    method: 'POST',
    headers: { 'X-Netjes-Token': TOKEN, 'Content-Type': 'application/json' },
    body: JSON.stringify({
      kind: 'page',
      route: null,
      page,
      segments: [{ id: 'app', layout: 'app/layout.tsx' }],
      props: {},
      params: {},
      shared: null,
      status: 200,
      url: '/',
      version: 'dev',
      document: { lang: 'en', assets: { stylesheets: [], modulePreloads: [], scripts: [] }, errorPage: null },
    }),
  });
}

/** A module as the browser gets it from the Vite dev server. */
async function browserModule(url: string) {
  const response = await fetch(new URL(url, `http://localhost:${sidecar.vitePort}`));
  expect(response.status).toBe(200);
  return response.text();
}

/** The page module map the browser loads with a page: the one the client entry, as served now, imports. */
async function browserPages() {
  const entry = await browserModule('/@netjes/client-entry');
  const url = /from\s*["']([^"']*virtual:netjes\/pages[^"']*)["']/.exec(entry)?.[1];
  if (!url) throw new Error(`the client entry does not import the page module map:\n${entry}`);
  return browserModule(url);
}

beforeAll(async () => {
  // By its real path, as Vite resolves the modules in it: on macOS the temporary folder is under a symlink (/var is
  // /private/var), and on Windows it can be named by an 8.3 short name (RUNNER~1).
  root = await realpath(await mkdtemp(path.join(tmpdir(), 'netjes-sidecar-dev-')));
  // A junction on Windows, which needs no administrator rights or developer mode as a directory symlink does.
  await symlink(NODE_MODULES, path.join(root, 'node_modules'), 'junction');
  await write(
    'vite.config.mjs',
    [
      "import netjes from 'netjes/vite';",
      // The port Vite listens on, for the requests a browser makes.
      "const port = { name: 'port', configureServer(server) { server.httpServer.once('listening', () => console.log(`VITE_PORT ${server.httpServer.address().port}`)); } };",
      // No watcher: nothing tells Vite about files added after it started. Its caches stay in this folder.
      "export default { plugins: [netjes({ tailwind: false }), port], cacheDir: '.vite', logLevel: 'error', server: { watch: null } };",
      '',
    ].join('\n'),
  );
  await write('app/layout.tsx', 'export default function Layout({ children }) { return <main>{children}</main>; }\n');
  await write('app/page.tsx', 'export default function Home() { return <h1>Home</h1>; }\n');
  sidecar = await start();
}, 60_000);

afterAll(async () => {
  // On SIGTERM the sidecar closes Vite, which may write its caches in the folder until it exits.
  if (child && child.exitCode === null && child.signalCode === null) {
    const exited = once(child, 'exit');
    child.kill();
    await exited;
  }
  if (root) await rm(root, { recursive: true, force: true });
});

describe('the development sidecar', () => {
  it('renders a page file added after the page module map was built, before Vite reports it', async () => {
    const home = await render('app/page.tsx');
    expect(home.status).toBe(200);
    expect(await home.text()).toContain('<h1>Home</h1>');

    await write('app/fresh/page.tsx', 'export default function Fresh() { return <h1>Fresh page</h1>; }\n');
    const response = await render('app/fresh/page.tsx');

    expect(await response.text()).toContain('<h1>Fresh page</h1>');
    expect(response.status).toBe(200);
  });

  it('renders a page folder at its new name', async () => {
    await write('app/before/page.tsx', 'export default function Moved() { return <h1>Moved page</h1>; }\n');
    expect((await render('app/before/page.tsx')).status).toBe(200);

    await rename(path.join(root, 'app/before'), path.join(root, 'app/after'));
    const response = await render('app/after/page.tsx');

    expect(await response.text()).toContain('<h1>Moved page</h1>');
    expect(response.status).toBe(200);
  });

  it('gives the browser a page module map with a page file it rendered after the browser loaded one', async () => {
    // A tab that loaded a page holds the map from before the file.
    const before = await browserPages();
    expect(before).toContain('/app/page.tsx');
    expect(before).not.toContain('/app/hydrated/page.tsx');

    await write('app/hydrated/page.tsx', 'export default function Hydrated() { return <h1>Hydrated page</h1>; }\n');
    expect((await render('app/hydrated/page.tsx')).status).toBe(200);

    // The document the render answers with loads the client entry, and with it the map, again.
    expect(await browserPages()).toContain('/app/hydrated/page.tsx');
  }, 30_000);

  it('still fails, at once, for a file the page module map can never hold', async () => {
    // On disk, but outside app/ (an appDir that differs from .NET's AppDirectory, say).
    await write('elsewhere/page.tsx', 'export default function Elsewhere() { return <h1>Elsewhere</h1>; }\n');

    const response = await render('elsewhere/page.tsx');

    expect(response.status).toBe(500);
    expect(((await response.json()) as { error: { message: string } }).error.message).toBe('netjes: no module for elsewhere/page.tsx in this build');
    expect((await render('app/page.tsx')).status).toBe(200);
  });

  it('reports in one line on stdout that the Vite port is taken, and exits', async () => {
    // Another app's Vite dev server, on localhost as Vite binds it.
    const other = createServer();
    await new Promise<void>((resolve) => other.listen(0, 'localhost', resolve));
    const { port } = other.address() as AddressInfo;
    try {
      const { code, stdout, stderr } = await runToExit(spawnSidecar(port));

      expect(stdout).toBe(`NETJES_FAILED {"reason":"vite-port-in-use","port":${port}}\n`);
      expect(stderr).toBe('');
      expect(code).toBe(1);
    } finally {
      await new Promise((resolve) => other.close(resolve));
    }
  }, 30_000);

  it('reports in one line on stdout that the Vite dev server did not start, with why, and exits', async () => {
    // An error in vite.config: Vite says which file it could not load, and the reason is what .NET reports.
    const broken = await mkdtemp(path.join(tmpdir(), 'netjes-sidecar-broken-'));
    try {
      await symlink(NODE_MODULES, path.join(broken, 'node_modules'), 'junction');
      await writeFile(path.join(broken, 'vite.config.mjs'), "import netjes from 'netjes/vite';\nexport default { plugins: [netjes(] };\n");

      const { code, stdout, stderr } = await runToExit(spawnSidecar(0, broken));

      const [line, ...rest] = stdout.split('\n');
      expect(rest).toEqual(['']);
      expect(line).toMatch(/^NETJES_FAILED /);
      const failed = JSON.parse((line ?? '').slice('NETJES_FAILED '.length)) as { reason: string; message: string; code?: string };
      expect(failed.reason).toBe('vite-failed');
      expect(failed.message).toContain('vite.config.mjs:2:');
      expect(failed.message).not.toContain('\u001b');
      expect(failed.code).toBeUndefined();
      // Vite's own line about the file, and no stack trace from Node.
      expect(stderr).toContain('failed to load config from');
      expect(stderr).not.toMatch(/^\s+at /m);
      expect(code).toBe(1);
    } finally {
      await rm(broken, { recursive: true, force: true });
    }
  }, 30_000);

  it('reports a package vite.config imports that is not installed by its error code', async () => {
    const missing = await mkdtemp(path.join(tmpdir(), 'netjes-sidecar-missing-'));
    try {
      await symlink(NODE_MODULES, path.join(missing, 'node_modules'), 'junction');
      await writeFile(path.join(missing, 'vite.config.mjs'), "import later from 'netjes-test-not-installed';\nexport default { plugins: [later()] };\n");

      const { code, stdout } = await runToExit(spawnSidecar(0, missing));

      const failed = JSON.parse(stdout.slice('NETJES_FAILED '.length)) as { reason: string; message: string; code?: string };
      expect(failed).toMatchObject({ reason: 'vite-failed', code: 'ERR_MODULE_NOT_FOUND' });
      expect(failed.message).toContain("Cannot find package 'netjes-test-not-installed'");
      expect(code).toBe(1);
    } finally {
      await rm(missing, { recursive: true, force: true });
    }
  }, 30_000);

  it('reports a package a CommonJS vite.config requires that is not installed by its error code', async () => {
    // Node's require has a code of its own for it, which .NET reads the same way.
    const missing = await mkdtemp(path.join(tmpdir(), 'netjes-sidecar-missing-cjs-'));
    try {
      await symlink(NODE_MODULES, path.join(missing, 'node_modules'), 'junction');
      await writeFile(path.join(missing, 'vite.config.cjs'), "const later = require('netjes-test-not-installed');\nmodule.exports = { plugins: [later()] };\n");

      const { code, stdout } = await runToExit(spawnSidecar(0, missing));

      const failed = JSON.parse(stdout.slice('NETJES_FAILED '.length)) as { reason: string; message: string; code?: string };
      expect(failed).toMatchObject({ reason: 'vite-failed', code: 'MODULE_NOT_FOUND' });
      expect(failed.message).toContain("Cannot find module 'netjes-test-not-installed'");
      expect(code).toBe(1);
    } finally {
      await rm(missing, { recursive: true, force: true });
    }
  }, 30_000);

  it("reports a page's syntax error with Vite's code frame as plain text", async () => {
    await write('app/typo/page.tsx', 'export default function Typo() {\n  return <h1>Typo</h1\n}\n');

    const response = await render('app/typo/page.tsx');

    expect(response.status).toBe(500);
    const { error } = (await response.json()) as { error: { message: string; stack: string } };
    expect(error.message).toContain('Expected `>`');
    expect(error.message).toContain('app/typo/page.tsx:3:1');
    // The code frame keeps its lines.
    expect(error.message.split('\n').length).toBeGreaterThan(4);
    expect(error.message).not.toContain('\u001b');
    expect(error.stack).not.toContain('\u001b');
  });
});
