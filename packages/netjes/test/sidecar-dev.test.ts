// @vitest-environment node
import { type ChildProcess, spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdir, mkdtemp, rename, rm, symlink, writeFile } from 'node:fs/promises';
import { type AddressInfo, createServer } from 'node:net';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';
import { normalizePath } from 'vite';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { INSTALLED, LINKED, type PackagesApp, UNLISTED, createPackagesApp } from './page-packages.ts';

// The sidecar in development: it hosts a Vite dev server for an app in a temporary folder, with the fixture page
// packages installed and this workspace's node_modules above it (test/page-packages.ts). Vite's watcher is off, so Vite never learns of a file the test adds: what .NET routes as soon as its own
// watcher sees the file, Vite reports later, much later (a file it missed, once something else in its folder changes)
// or never (a folder it ignores).
const SIDECAR = fileURLToPath(new URL('../sidecar/server.mjs', import.meta.url));
const NODE_MODULES = fileURLToPath(new URL('../../../node_modules', import.meta.url));
const TOKEN = 'test-token';

let app: PackagesApp | undefined;
let root = '';
/** The spawned process, kept from the moment it starts: `sidecar` is unset when startup times out, and the child must still die. */
let child: ChildProcess | undefined;
let sidecar: { port: number; vitePort: number };
/** What the sidecar has written to stdout and stderr since it started. */
let output = '';

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
  spawned.stderr.setEncoding('utf8').on('data', (chunk: string) => (output += chunk));
  return new Promise((resolve, reject) => {
    let vitePort = 0;
    createInterface({ input: spawned.stdout }).on('line', (line) => {
      output += `${line}\n`;
      if (line.startsWith('VITE_PORT ')) vitePort = Number(line.slice(10));
      if (line.startsWith('NETJES_READY ')) resolve({ port: JSON.parse(line.slice(12)).port, vitePort });
    });
    spawned.on('exit', (code) => reject(new Error(`sidecar exited with ${code}`)));
  });
}

/** The payload .NET sends for a page under the root layout, or under these segments. */
function render(page: string, segments: Array<{ id: string; layout?: string }> = [{ id: 'app', layout: 'app/layout.tsx' }]) {
  return fetch(`http://127.0.0.1:${sidecar.port}/render`, {
    method: 'POST',
    headers: { 'X-Netjes-Token': TOKEN, 'Content-Type': 'application/json' },
    body: JSON.stringify({
      kind: 'page',
      route: null,
      page,
      segments,
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

/** The `<style data-vite-dev-id>` elements of a document, by id, with their text: what Vite's client adopts. */
function devStyles(html: string) {
  const styles = new Map<string, string>();
  for (const [, id = '', css = ''] of html.matchAll(/<style data-vite-dev-id="([^"]*)"[^>]*>([\s\S]*?)<\/style>/g)) styles.set(id, css);
  return styles;
}

/**
 * A string that a CSS module, as the browser gets it, declares on a line of its own: `const <name> = <JSON>`. Vite's
 * client finds the style element by the `__vite__id` and sets its text to the `__vite__css`. Read by lines, not with a
 * regular expression: JSON.stringify leaves U+2028 and U+2029 in a string, and a regular expression's `.` stops at both.
 */
function moduleString(cssModule: string, name: string) {
  const prefix = `const ${name} = `;
  const line = cssModule.split('\n').find((text) => text.startsWith(prefix));
  if (line === undefined) throw new Error(`the module declares no ${name}:\n${cssModule}`);
  return JSON.parse(line.slice(prefix.length)) as string;
}

/** Tells Vite that a file changed, as its watcher would, and returns the type of each message Vite sent the browsers. */
async function edit(file: string) {
  const response = await fetch(new URL(`/__netjes-test/edit?file=${encodeURIComponent(file)}`, `http://localhost:${sidecar.vitePort}`));
  expect(response.status).toBe(200);
  return (await response.json()) as string[];
}

/** The page module map the browser loads with a page: the one the client entry, as served now, imports. */
async function browserPages() {
  const entry = await browserModule('/@netjes/client-entry');
  const url = /from\s*["']([^"']*virtual:netjes\/pages[^"']*)["']/.exec(entry)?.[1];
  if (!url) throw new Error(`the client entry does not import the page module map:\n${entry}`);
  return browserModule(url);
}

beforeAll(async () => {
  // By their real paths, as Vite resolves the modules in them.
  app = await createPackagesApp();
  root = app.root;
  await write(
    'vite.config.mjs',
    [
      "import path from 'node:path';",
      "import netjes from 'netjes/vite';",
      // The port Vite listens on, for the requests a browser makes.
      "const port = { name: 'port', configureServer(server) { server.httpServer.once('listening', () => console.log(`VITE_PORT ${server.httpServer.address().port}`)); } };",
      // An edit, which the watcher would report: GET /__netjes-test/edit?file=<path> answers once Vite has handled it
      // in every environment (hotUpdateEnvironments, below), with the type of each message Vite sent the browsers. A
      // full reload that names no file is the dependency optimizer's, which can come at any time, and is left out.
      'let handled;',
      "const edits = { name: 'edits', configureServer(server) {",
      '  const sent = [];',
      '  const send = server.ws.send.bind(server.ws);',
      "  server.ws.send = (payload, ...rest) => { if (payload.type !== 'full-reload' || payload.triggeredBy) sent.push(payload.type ?? payload); return send(payload, ...rest); };",
      "  server.middlewares.use('/__netjes-test/edit', (req, res) => {",
      '    sent.length = 0;',
      "    handled = () => { handled = undefined; res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify(sent)); };",
      "    server.watcher.emit('change', path.join(server.config.root, new URL(req.url, 'http://localhost').searchParams.get('file')));",
      '  });',
      '} };',
      'const hotUpdateEnvironments = async (server, hmr) => { await Promise.all(Object.values(server.environments).map((environment) => hmr(environment))); handled?.(); };',
      // Each Vite environment builds a CSS file on its own, and a plugin can build it differently in each (Tailwind CSS
      // keeps its classes per environment): this one says in the CSS which environment built it.
      "const environmentMark = { name: 'environment-mark', transform(code, id) { if (id.split('?')[0].endsWith('.css')) return `${code}/* built for ${this.environment.name} */`; } };",
      // Source maps for development CSS, which an app can turn on: only the module the browser imports for a CSS file
      // carries one, at the end of its text. No watcher: nothing tells Vite about files added after it started. Its
      // caches stay in this folder. The fixture page packages are listed, except one (@netjes-test/unlisted).
      `export default { plugins: [netjes({ packages: ${JSON.stringify([INSTALLED, LINKED])} }), port, environmentMark, edits], css: { devSourcemap: true }, cacheDir: '.vite', logLevel: 'error', server: { watch: null, hotUpdateEnvironments } };`,
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
  await app?.remove();
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

  it("inlines a page's CSS as the browser's module sets it, from the client environment's build", async () => {
    await write('app/styled/styles.css', 'h1{color:red}\n');
    await write('app/styled/page.tsx', "import './styles.css';\nexport default function Styled() { return <h1>Styled</h1>; }\n");

    const response = await render('app/styled/page.tsx');

    expect(response.status).toBe(200);
    const styles = devStyles(await response.text());
    // The CSS module the page's import loads in the browser, which sets the text of the element with its id.
    const cssModule = await browserModule('/app/styled/styles.css');
    const id = moduleString(cssModule, '__vite__id');
    expect([...styles.keys()]).toEqual([id]);
    expect(styles.get(id)).toBe(moduleString(cssModule, '__vite__css'));
    expect(styles.get(id)).toContain('built for client');
    // With css.devSourcemap on, the text ends with the source map that only the browser's build of the file has.
    expect(styles.get(id)).toMatch(/\/\*# sourceMappingURL=data:application\/json;base64,[^*]+ \*\/$/);
  });

  it("inlines the client's build of a stylesheet that contains U+2028", async () => {
    // A line separator, which JSON.stringify leaves as it is on the line of the CSS module that holds the text.
    await write('app/separator/styles.css', '.a::after{content:"a\u2028b"}\n');
    await write('app/separator/page.tsx', "import './styles.css';\nexport default function Separator() { return <h1 className=\"a\">Separator</h1>; }\n");

    const response = await render('app/separator/page.tsx');

    expect(response.status).toBe(200);
    const [css] = devStyles(await response.text()).values();
    expect(css).toContain('content:"a\u2028b"');
    expect(css).toContain('built for client');
    // The warning the sidecar logs when it inlines the SSR build instead names the line it could not read.
    expect(output).not.toContain('__vite__css');
  });

  it('sends the browsers nothing for an edit to a CSS Modules file of a page that no browser has loaded', async () => {
    // The module Vite serves for a CSS Modules file does not accept its own updates: the page that imports it does.
    await write('app/modules/styles.module.css', '.title{color:red}\n');
    await write('app/modules/page.tsx', "import styles from './styles.module.css';\nexport default function Modules() { return <h1 className={styles.title}>Modules</h1>; }\n");
    const before = devStyles(await (await render('app/modules/page.tsx')).text());
    expect([...before.values()]).toEqual([expect.stringContaining('color:red')]);

    await write('app/modules/styles.module.css', '.title{color:blue}\n');

    // No page that is open has the file's styles: there is nothing to update, and no reason to reload any page.
    expect(await edit('app/modules/styles.module.css')).toEqual([]);
    const after = devStyles(await (await render('app/modules/page.tsx')).text());
    expect([...after.values()]).toEqual([expect.stringContaining('color:blue')]);
  });

  it('updates a page that a browser loads after the sidecar has read its CSS Modules file', async () => {
    await write('app/loaded/styles.module.css', '.title{color:red}\n');
    await write('app/loaded/page.tsx', "import styles from './styles.module.css';\nexport default function Loaded() { return <h1 className={styles.title}>Loaded</h1>; }\n");
    expect((await render('app/loaded/page.tsx')).status).toBe(200);
    // The browser loads the page's module, and the stylesheet's with it.
    const pageModule = await browserModule('/app/loaded/page.tsx');
    const stylesheet = /from\s*["']([^"']*styles\.module\.css[^"']*)["']/.exec(pageModule)?.[1];
    if (!stylesheet) throw new Error(`the page module does not import its stylesheet:\n${pageModule}`);
    await browserModule(stylesheet);

    await write('app/loaded/styles.module.css', '.title{color:blue}\n');

    // The page takes the update, as Fast Refresh accepts it.
    expect(await edit('app/loaded/styles.module.css')).toEqual(['update']);
  });
});

describe.each([
  { name: INSTALLED, pages: 'app', dir: () => app?.installed ?? '' },
  { name: LINKED, pages: 'pages', dir: () => app?.linked ?? '' },
])('the development sidecar with the page package $name', ({ name, pages, dir }) => {
  const id = (file: string) => `${name}/${pages}/${file}`;
  /** Under the app's root layout and the package's own, as the app mounts it at /help. */
  const mounted = (file: string) =>
    render(id(file), [
      { id: 'app', layout: 'app/layout.tsx' },
      { id: 'app/help', layout: id('layout.tsx') },
    ]);
  /** The marker id a page renders, which names the instance of the package's module it got. */
  const markerIn = (html: string) => /(?:installed|linked)-[a-z0-9]+/.exec(html)?.[0];

  // First in this block, so the app's page imports the package before any of the package's pages has loaded it: once one
  // has, Vite hands the import the instance it has, and only the other order shows a second one.
  it("shares one module instance between the package's page and an app page that imports the package by name", async () => {
    await write(`app/by-name/${pages}/page.tsx`, `import { marker } from '${name}';\nexport default function ByName() { return <p>by name {marker.id}</p>; }\n`);

    const byName = markerIn(await (await render(`app/by-name/${pages}/page.tsx`)).text());
    const byPath = markerIn(await (await mounted('page.tsx')).text());

    expect(byName).toBeDefined();
    expect(byName).toBe(byPath);
    // In the browser too: the app's page imports the package's own module, which no pre-bundled copy stands in for.
    const pageModule = await browserModule(`/app/by-name/${pages}/page.tsx`);
    const imported = /from\s*["']([^"']*)["']/g;
    const urls = [...pageModule.matchAll(imported)].map(([, url = '']) => url.split('?')[0]);
    expect(urls).not.toContainEqual(expect.stringContaining('.vite/deps/@netjes-test'));
    const own = normalizePath(path.join(dir(), pages === 'app' ? 'src/index.js' : 'lib/index.ts'));
    expect(urls).toContain(own.startsWith(`${normalizePath(root)}/`) ? own.slice(normalizePath(root).length) : `/@fs/${own.replace(/^\//, '')}`);
  });

  it("renders the package's page inside its layout, with netjes and zod as the app has them", async () => {
    const response = await mounted('page.tsx');

    expect(response.status).toBe(200);
    const html = await response.text();
    expect(html).toMatch(new RegExp(`<main><section data-package="${pages === 'app' ? 'installed' : 'linked'}"><div`));
    expect(html).toContain('home');
    // The package's Link reads the router that the server entry provides: one copy of netjes.
    expect(html).toContain('<a href="/"');
  });

  it("gives the browser a page module map that loads the package's pages from the dev server", async () => {
    const map = await browserPages();

    // The key is the file id, and the import the URL of the file's real path, which the dev server serves.
    const entry = new RegExp(`"/${id('page.tsx').replace(/[.*+?^${}()|[\]\\/]/g, '\\$&')}":\\s*\\(\\)\\s*=>\\s*import\\("([^"]+)"\\)`).exec(map);
    expect(entry, map).not.toBeNull();
    const own = normalizePath(path.join(dir(), pages, 'page.tsx'));
    expect(entry?.[1]?.split('?')[0]).toBe(own.startsWith(`${normalizePath(root)}/`) ? own.slice(normalizePath(root).length) : `/@fs/${own.replace(/^\//, '')}`);
    expect(await browserModule(entry?.[1] ?? '')).toContain(pages === 'app' ? 'InstalledHome' : 'LinkedHome');
  });

  it("inlines the CSS that the package's page imports", async () => {
    const response = await mounted('styled/page.tsx');

    expect(response.status).toBe(200);
    const styles = [...devStyles(await response.text()).values()];
    expect(styles).toEqual([expect.stringMatching(pages === 'app' ? /\.installed-styled\s*\{\s*color:\s*teal/ : /\.linked-styled\s*\{\s*color:\s*olive/)]);
  });
});

describe('the development sidecar with a linked page package', () => {
  it('renders a page added to the package after the page module map was built, without a restart', async () => {
    const linked = app?.linked ?? '';
    expect((await render(`${LINKED}/pages/page.tsx`)).status).toBe(200);

    await app?.write('pages/fresh/page.tsx', 'export default function Fresh() { return <h1>Fresh package page</h1>; }\n', linked);
    const response = await render(`${LINKED}/pages/fresh/page.tsx`);

    expect(await response.text()).toContain('<h1>Fresh package page</h1>');
    expect(response.status).toBe(200);
  });

  it('refuses the C# build output and project of a package linked from outside the workspace root', async () => {
    const linked = normalizePath(app?.linked ?? '');
    await app?.write('obj/project.assets.json', '{"secret":true}\n', linked);
    await app?.write('bin/Debug/net10.0/Linked.deps.json', '{"secret":true}\n', linked);
    await app?.write('Linked.csproj', '<Project />\n', linked);
    const status = async (url: string) => (await fetch(new URL(url, `http://localhost:${sidecar.vitePort}`))).status;
    const fs = `/@fs/${linked.replace(/^\//, '')}`;
    const throughLink = `/node_modules/${LINKED}`;

    // Its files are served, one that no page has imported yet too: the dev server may read the package.
    await app?.write('lib/later.ts', 'export const later = 1;\n', linked);
    expect(await status(`${fs}/lib/later.ts`)).toBe(200);
    expect(await status(`${fs}/pages/page.tsx`)).toBe(200);
    for (const file of ['obj/project.assets.json', 'bin/Debug/net10.0/Linked.deps.json', 'Linked.csproj']) {
      for (const url of [`${fs}/${file}`, `${fs}/${file}?raw`, `${fs}/${file}?import`, `${fs}/${file}?url`, `${throughLink}/${file}`, `${throughLink}/${file}?raw`]) {
        expect(await status(url), url).toBe(403);
      }
      // A file name in another case, and a folder climbed back out of, are not served either.
      expect(await status(`${fs}/${file.toUpperCase()}`), file.toUpperCase()).not.toBe(200);
      expect(await status(`${fs}/pages/%2e%2e/${file}`), `pages/%2e%2e/${file}`).not.toBe(200);
      // Nor by a doubled slash, or a slash or backslash that is encoded (a separator on Windows).
      for (const url of [`${fs}//${file}`, `/@fs//${linked.replace(/^\//, '')}/${file}`, `${throughLink}//${file}`, `${fs}/${file.replace('/', '%2F')}`, `${fs}/${file.replace('/', '%5C')}`]) {
        expect(await status(url), url).not.toBe(200);
      }
    }
  });

  it("names the fix for a page of an installed page package that netjes({ packages }) leaves out", async () => {
    const response = await render(`${UNLISTED}/app/page.tsx`);

    expect(response.status).toBe(500);
    expect(((await response.json()) as { error: { message: string } }).error.message).toBe(
      `netjes: no module for ${UNLISTED}/app/page.tsx in this build: add '${UNLISTED}' to netjes({ packages }) in vite.config.mjs`,
    );
    expect((await render('app/page.tsx')).status).toBe(200);
  });
});
