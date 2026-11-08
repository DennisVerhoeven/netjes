// @vitest-environment node
import { mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { build, mergeConfig, normalizePath, type ConfigEnv, type Plugin, type PluginOption, type UserConfig } from 'vite';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import netjes from '../vite/plugin.mjs';
import { INSTALLED, LINKED, type PackagesApp, createPackagesApp } from './page-packages.ts';

/** Netjes's own Vitest setup file, which the plugin names by path, like the cleanup file, and the package does not export. */
const OWN_SETUP = normalizePath(fileURLToPath(new URL('../dist/testing-vitest.js', import.meta.url)));

async function flatten(options: PluginOption[]): Promise<Plugin[]> {
  const plugins: Plugin[] = [];
  for (const option of options) {
    const value = await option;
    if (Array.isArray(value)) plugins.push(...(await flatten(value)));
    else if (value) plugins.push(value as Plugin);
  }
  return plugins;
}

async function core(options?: Parameters<typeof netjes>[0]) {
  const plugins = await flatten(netjes(options));
  const plugin = plugins.find((p) => p.name === 'netjes');
  if (!plugin) throw new Error('netjes plugin missing');
  return { plugins, plugin };
}

const serve: ConfigEnv = { command: 'serve', mode: 'test' };

/** A root as the plugin resolves it, with / separators: on Windows, `/apps/shop` is on the current drive. */
const absolute = (root: string) => normalizePath(path.resolve(root));

function callConfig(plugin: Plugin, config: UserConfig = {}, env: ConfigEnv = serve) {
  const hook = plugin.config;
  const handler = typeof hook === 'function' ? hook : hook?.handler;
  return handler?.call({} as never, config, env) as (UserConfig & { test?: Record<string, unknown> }) | undefined;
}

/** The app's Vitest settings once Vite has merged the plugin's into them, as it does with what a config hook returns. */
function mergedTest(plugin: Plugin, config: UserConfig) {
  return (mergeConfig(config, callConfig(plugin, config) ?? {}) as { test?: Record<string, unknown> }).test;
}

async function loadVirtual(plugin: Plugin, id: string) {
  const resolveHook = plugin.resolveId;
  const resolve = typeof resolveHook === 'function' ? resolveHook : resolveHook?.handler;
  const resolved = await resolve?.call({} as never, id, undefined, { attributes: {}, isEntry: false });
  const loadHook = plugin.load;
  const load = typeof loadHook === 'function' ? loadHook : loadHook?.handler;
  return String(await load?.call({} as never, String(resolved)));
}

describe('the page module map', () => {
  it('holds the app/ convention files only, no pages/ files, tests or colocated modules', async () => {
    const { plugin } = await core();

    const code = await loadVirtual(plugin, 'virtual:netjes/pages');

    expect(code).toBe(
      'export const pages = import.meta.glob(["/app/**/{page,layout,template,loading,error,not-found,forbidden,unauthorized,global-error,default}.{tsx,jsx}"]);\n' +
        'export const appDir = "app";\n' +
        'export const packages = [];\n',
    );
  });

  it('pre-bundles dependencies starting from the app/ convention files only', async () => {
    const { plugin } = await core();

    expect(callConfig(plugin)?.optimizeDeps?.entries).toEqual([
      'app/**/{page,layout,template,loading,error,not-found,forbidden,unauthorized,global-error,default}.{tsx,jsx}',
    ]);
  });

  it('follows a custom appDir', async () => {
    const { plugin } = await core({ appDir: 'src/app/' });

    const code = await loadVirtual(plugin, 'virtual:netjes/pages');

    expect(code).toContain('"/src/app/**/{page,');
    // The folder the map's keys start with, which renderDocument needs for routes and segment ids.
    expect(code).toContain('export const appDir = "src/app";\n');
    expect(code).not.toContain('pages/');
  });
});

describe('page packages', () => {
  let app: PackagesApp;
  beforeAll(async () => {
    app = await createPackagesApp();
  });
  afterAll(async () => {
    await app?.remove();
  });

  /** The netjes plugin with these packages listed, its config hook run for the app's folder. */
  async function listing(packages: string[], options?: Parameters<typeof netjes>[0], config: UserConfig = {}, env: ConfigEnv = serve) {
    const { plugins, plugin } = await core({ ...options, packages });
    return { plugins, plugin, config: callConfig(plugin, { root: app.root, ...config }, env) };
  }

  /** The lazy imports a page module map adds to the glob, by key: `"<key>": () => import("<path>")`. */
  function imports(code: string) {
    const found: Record<string, string> = {};
    for (const [, key = '', file = ''] of code.matchAll(/("(?:[^"\\]|\\.)*"): \(\) => import\(("(?:[^"\\]|\\.)*")\)/g)) {
      found[JSON.parse(key) as string] = JSON.parse(file) as string;
    }
    return found;
  }

  it('adds their convention files to the page module map, keyed by file id and imported by their real paths', async () => {
    const { plugin } = await listing([INSTALLED, LINKED]);

    const code = await loadVirtual(plugin, 'virtual:netjes/pages');

    const installed = normalizePath(app.installed);
    const linked = normalizePath(app.linked);
    expect(imports(code)).toEqual({
      '/@netjes-test/installed/app/(center)/articles/[slug]/page.tsx': `${installed}/app/(center)/articles/[slug]/page.tsx`,
      '/@netjes-test/installed/app/layout.tsx': `${installed}/app/layout.tsx`,
      '/@netjes-test/installed/app/page.tsx': `${installed}/app/page.tsx`,
      '/@netjes-test/installed/app/styled/page.tsx': `${installed}/app/styled/page.tsx`,
      '/@netjes-test/linked/pages/(center)/articles/[slug]/page.tsx': `${linked}/pages/(center)/articles/[slug]/page.tsx`,
      '/@netjes-test/linked/pages/layout.tsx': `${linked}/pages/layout.tsx`,
      '/@netjes-test/linked/pages/page.tsx': `${linked}/pages/page.tsx`,
      '/@netjes-test/linked/pages/styled/page.tsx': `${linked}/pages/styled/page.tsx`,
    });
    // The app's own files, by the glob as before, and the packages with the folder of their pages.
    expect(code).toContain('import.meta.glob(["/app/**/{page,layout,template,loading,error,not-found,forbidden,unauthorized,global-error,default}.{tsx,jsx}"])');
    expect(code).toContain('export const appDir = "app";\n');
    expect(code).toContain(`export const packages = [{"name":"${INSTALLED}","pages":"app"},{"name":"${LINKED}","pages":"pages"}];\n`);
  });

  it('lists the files again each time the map is built, so a page added to a package is in the next one', async () => {
    const { plugin } = await listing([LINKED]);
    expect(imports(await loadVirtual(plugin, 'virtual:netjes/pages'))).not.toHaveProperty('/@netjes-test/linked/pages/added/page.tsx');

    await app.write('pages/added/page.tsx', 'export default function Added() { return null; }\n', app.linked);

    expect(imports(await loadVirtual(plugin, 'virtual:netjes/pages'))).toHaveProperty('/@netjes-test/linked/pages/added/page.tsx');
    await rm(path.join(app.linked, 'pages/added'), { recursive: true });
  });

  it('leaves out what is no convention file, and what Vite leaves out of a glob', async () => {
    const { plugin } = await listing([INSTALLED]);
    await app.write('app/_lib/helpers.tsx', 'export const x = 1;\n', app.installed);
    await app.write('app/page.test.tsx', 'export {};\n', app.installed);
    await app.write('app/node_modules/x/page.tsx', 'export default function X() { return null; }\n', app.installed);
    await app.write('app/.hidden/page.tsx', 'export default function X() { return null; }\n', app.installed);
    await app.write('app/route.ts', 'export {};\n', app.installed);

    const keys = Object.keys(imports(await loadVirtual(plugin, 'virtual:netjes/pages')));

    for (const folder of ['_lib', 'node_modules', '.hidden']) await rm(path.join(app.installed, 'app', folder), { recursive: true });
    for (const file of ['page.test.tsx', 'route.ts']) await rm(path.join(app.installed, 'app', file));
    expect(keys).toEqual([
      '/@netjes-test/installed/app/(center)/articles/[slug]/page.tsx',
      '/@netjes-test/installed/app/layout.tsx',
      '/@netjes-test/installed/app/page.tsx',
      '/@netjes-test/installed/app/styled/page.tsx',
    ]);
  });

  it('shares one copy of netjes and zod between the app and its packages, which a linked package would resolve on its own', async () => {
    expect((await listing([LINKED])).config?.resolve?.dedupe).toEqual(expect.arrayContaining(['react', 'react-dom', 'netjes', 'zod']));
    // Without packages the list is as it was.
    expect((await listing([])).config?.resolve?.dedupe).not.toContain('netjes');
    expect((await listing([])).config?.resolve?.dedupe).not.toContain('zod');
  });

  it('lets the dev server read each package by its real path, also when the app narrowed fs.allow', async () => {
    const allow = (await listing([INSTALLED, LINKED])).config?.server?.fs?.allow ?? [];
    expect(allow).toEqual(expect.arrayContaining([normalizePath(app.installed), normalizePath(app.linked)]));

    const narrowed = (await listing([LINKED], {}, { server: { fs: { allow: [path.join(app.root, 'app')] } } })).config?.server?.fs?.allow;
    expect(narrowed).toEqual([fileURLToPath(new URL('..', import.meta.url)), normalizePath(app.linked)]);
  });

  it("keeps each package's backend folders out of the dev server, wherever the package is", async () => {
    const picomatch = createRequire(import.meta.url)('picomatch') as (glob: string, options: { dot: boolean; nocase: boolean }) => (path: string) => boolean;
    const deny = (await listing([INSTALLED, LINKED])).config?.server?.fs?.deny ?? [];
    const denied = (file: string) => deny.some((glob) => picomatch(glob.includes('/') ? glob : `**/${glob}`, { dot: true, nocase: true })(normalizePath(file)));

    // The linked package lies outside the workspace root, whose backend folders the plugin denies already.
    for (const file of ['obj/project.assets.json', 'bin/Debug/net10.0/Linked.deps.json', 'dist/server/server-entry.js', 'Linked.csproj']) {
      expect(denied(path.join(app.linked, file)), file).toBe(true);
    }
    expect(denied(path.join(app.installed, 'obj/project.assets.json'))).toBe(true);
    expect(denied(path.join(app.linked, 'pages/page.tsx'))).toBe(false);
    expect(denied(path.join(app.linked, 'lib/obj.ts'))).toBe(false);
  });

  it('has Vite process the packages in development, without pre-bundling them, as it does netjes', async () => {
    const { config } = await listing([INSTALLED, LINKED]);
    expect(config?.optimizeDeps?.exclude).toEqual(['netjes', INSTALLED, LINKED]);
    expect(config?.ssr?.noExternal).toEqual(['netjes', INSTALLED, LINKED]);
    // A build bundles everything for the sidecar.
    expect((await listing([INSTALLED], {}, {}, { command: 'build', mode: 'production', isSsrBuild: true })).config?.ssr?.noExternal).toBe(true);
    // Without packages, as before.
    expect((await listing([])).config?.optimizeDeps?.exclude).toEqual(['netjes']);
  });

  it("pre-bundles dependencies starting from each package's convention files too", async () => {
    const entries = (await listing([INSTALLED, LINKED])).config?.optimizeDeps?.entries as string[];
    const files = '**/{page,layout,template,loading,error,not-found,forbidden,unauthorized,global-error,default}.{tsx,jsx}';
    // Relative to the root, as Vite globs them, with the characters a glob reads escaped.
    const glob = (dir: string) => `${normalizePath(path.relative(app.root, dir)).replace(/[()[\]{}*?|]|[!+@](?=\()/g, '\\$&')}/${files}`;
    expect(entries).toEqual([`app/${files}`, glob(path.join(app.installed, 'app')), glob(path.join(app.linked, 'pages'))]);

    // Vite's glob finds them: in node_modules, and outside the root.
    const { glob: find } = createRequire(createRequire(import.meta.url).resolve('vite'))('tinyglobby') as {
      glob: (patterns: string[], options: { cwd: string; absolute: boolean }) => Promise<string[]>;
    };
    const found = (await find(entries.slice(1), { cwd: app.root, absolute: true })).map((file) => normalizePath(file)).sort();
    expect(found).toContain(normalizePath(path.join(app.installed, 'app/(center)/articles/[slug]/page.tsx')));
    expect(found).toContain(normalizePath(path.join(app.linked, 'pages/page.tsx')));
    expect(found).toHaveLength(8);
  });

  it("pre-bundles a package's CommonJS dependencies through the package, which Vite does not pre-bundle", async () => {
    await app.write('node_modules/netjes-test-cjs/package.json', JSON.stringify({ name: 'netjes-test-cjs', version: '1.0.0', main: 'index.js' }));
    await app.write('node_modules/netjes-test-esm/package.json', JSON.stringify({ name: 'netjes-test-esm', version: '1.0.0', type: 'module' }));
    await app.write('node_modules/netjes-test-exports/package.json', JSON.stringify({ name: 'netjes-test-exports', exports: { import: './index.js', require: './index.cjs' } }));
    await app.write('node_modules/netjes-test-module/package.json', JSON.stringify({ name: 'netjes-test-module', main: 'index.cjs', module: 'index.js' }));
    await app.write(
      'node_modules/@netjes-test/deps/package.json',
      JSON.stringify({
        name: '@netjes-test/deps',
        version: '1.0.0',
        peerDependencies: { netjes: '*' },
        netjes: {},
        dependencies: { 'netjes-test-cjs': '1', 'netjes-test-esm': '1', 'netjes-test-exports': '1', 'netjes-test-module': '1', netjes: '*', 'netjes-test-gone': '1' },
      }),
    );

    const include = (await listing(['@netjes-test/deps'])).config?.optimizeDeps?.include ?? [];

    expect(include).toContain('@netjes-test/deps > netjes-test-cjs');
    expect(include.filter((entry) => entry.startsWith('@netjes-test/deps >'))).toEqual(['@netjes-test/deps > netjes-test-cjs']);
  });

  it("watches a linked package's pages and sources, and builds the page module map again when a page comes or goes", async () => {
    const { plugin } = await listing([INSTALLED, LINKED]);
    const watched: string[] = [];
    const handlers = new Map<string, (file: string) => void>();
    const invalidated: string[] = [];
    const environment = (name: string) => ({
      moduleGraph: {
        getModuleById: (id: string) => (id === '\0virtual:netjes/pages' ? { id } : undefined),
        invalidateModule: () => invalidated.push(name),
      },
    });
    const server = {
      watcher: { add: (paths: string | string[]) => watched.push(...[paths].flat()), on: (event: string, handler: (file: string) => void) => handlers.set(event, handler) },
      environments: { client: environment('client'), ssr: environment('ssr') },
    };
    const hook = plugin.configureServer;
    await (typeof hook === 'function' ? hook : hook?.handler)?.call({} as never, server as never);
    const linked = normalizePath(app.linked);
    /** Reports a file as Vite's watcher does, by its path on this system, and says whether the map was built again. */
    const report = (event: string, file: string) => {
      invalidated.length = 0;
      handlers.get(event)?.(path.join(app.linked, file));
      return [...invalidated];
    };

    // An installed package does not change while the app runs, and node_modules is not watched.
    expect(watched).toEqual([`${linked}/pages`, `${linked}/lib`]);
    expect(report('add', 'pages/new/page.tsx')).toEqual(['client', 'ssr']);
    expect(report('unlink', 'pages/(center)/articles/[slug]/page.tsx')).toEqual(['client', 'ssr']);
    expect(report('add', 'pages/new/helper.tsx')).toEqual([]);
    expect(report('add', 'lib/page.tsx')).toEqual([]);
    expect(report('change', 'pages/page.tsx')).toEqual([]);
  });

  it("gives the development sidecar each file's URL on the dev server, and the packages it read", async () => {
    const { plugin } = await listing([INSTALLED, LINKED]);
    const hook = plugin.configResolved;
    await (typeof hook === 'function' ? hook : hook?.handler)?.call({} as never, { root: normalizePath(app.root), build: { outDir: 'dist/client' } } as never);
    const api = plugin.api as {
      pageUrl(id: string): string | null;
      unlistedPackage(id: string): string | null;
      packages(): Array<{ name: string; version: string; pages: string; root: string }>;
    };

    expect(api.pageUrl('app/page.tsx')).toBe('/app/page.tsx');
    // Inside the root by its path from there; outside it by its real path.
    expect(api.pageUrl(`${INSTALLED}/app/page.tsx`)).toBe(`/node_modules/${INSTALLED}/app/page.tsx`);
    expect(api.pageUrl(`${LINKED}/pages/(center)/articles/[slug]/page.tsx`)).toBe(
      `/@fs/${normalizePath(app.linked).replace(/^\//, '')}/pages/(center)/articles/[slug]/page.tsx`,
    );
    // A file of an installed package the app does not list: the render says to list it.
    expect(api.pageUrl('@netjes-test/unlisted/app/page.tsx')).toBeNull();
    expect(api.unlistedPackage('@netjes-test/unlisted/app/page.tsx')).toBe('@netjes-test/unlisted');
    for (const id of ['app/page.tsx', `${INSTALLED}/app/page.tsx`, 'elsewhere/page.tsx', '@netjes-test/missing/app/page.tsx', '@netjes-test']) {
      expect(api.unlistedPackage(id), id).toBeNull();
    }
    expect(api.pageUrl('elsewhere/page.tsx')).toBe('/elsewhere/page.tsx');
    expect(api.packages().map(({ name, version, pages, root }) => ({ name, version, pages, root }))).toEqual([
      { name: INSTALLED, version: '1.2.0', pages: 'app', root: normalizePath(app.installed) },
      { name: LINKED, version: '0.4.0', pages: 'pages', root: normalizePath(app.linked) },
    ]);
  });
});

describe('page packages that cannot be read', () => {
  let app: PackagesApp;
  beforeAll(async () => {
    app = await createPackagesApp();
  });
  afterAll(async () => {
    await app?.remove();
  });

  /** The error of the config hook, which reads the packages, for an app with these files in its node_modules. */
  async function configError(packages: string[], files: Record<string, string> = {}, options?: Parameters<typeof netjes>[0]) {
    for (const [file, content] of Object.entries(files)) await app.write(path.join('node_modules', file), content);
    const { plugin } = await core({ ...options, packages });
    try {
      callConfig(plugin, { root: app.root });
    } catch (error) {
      return error instanceof Error ? error.message : String(error);
    }
    return 'no error';
  }

  /** A page package's package.json, with these fields over a valid one's. */
  const manifest = (name: string, fields: Record<string, unknown> = {}) =>
    JSON.stringify({ name, version: '1.0.0', peerDependencies: { netjes: '*' }, netjes: {}, ...fields });

  it('names a package that is not installed, and how to install it', async () => {
    expect(await configError(['@netjes-test/missing'])).toBe("netjes: '@netjes-test/missing' in netjes({ packages }) is not installed: npm install @netjes-test/missing");
  });

  it('names a package without a netjes field', async () => {
    expect(await configError(['plain'], { 'plain/package.json': JSON.stringify({ name: 'plain', version: '1.0.0' }) })).toBe(
      'netjes: \'plain\' in netjes({ packages }) is not a page package: its package.json has no "netjes" field',
    );
  });

  it('names a package without a netjes peer range', async () => {
    expect(await configError(['no-peer'], { 'no-peer/package.json': manifest('no-peer', { peerDependencies: {} }) })).toBe(
      "netjes: 'no-peer' has no peerDependencies.netjes in its package.json: a page package names the Netjes versions it supports there",
    );
  });

  it('refuses a path in the netjes field that leaves the package or has another spelling', async () => {
    const refused = (key: string, value: unknown) =>
      `netjes: 'paths' has "${key}": ${JSON.stringify(value)} in the "netjes" field of its package.json: ` +
      'a path there is relative and stays inside the package (no "..", no leading "/", no "\\")';
    for (const pages of ['../x', 'app/../..', '/app', 'C:/app', 'c:app', 'app\\x', '..', '.', './app', 'app/', '', 'a//b', 7]) {
      expect(await configError(['paths'], { 'paths/package.json': manifest('paths', { netjes: { pages } }) }), String(pages)).toBe(refused('pages', pages));
    }
    expect(await configError(['paths'], { 'paths/package.json': manifest('paths', { netjes: { sources: ['app', '../src'] } }) })).toBe(refused('sources', '../src'));
    expect(await configError(['paths'], { 'paths/package.json': manifest('paths', { netjes: { css: '/etc/x.css' } }) })).toBe(refused('css', '/etc/x.css'));
  });

  it('names a stylesheet or a source folder that the package does not have', async () => {
    expect(await configError(['gaps'], { 'gaps/package.json': manifest('gaps', { netjes: { css: 'styles/none.css' } }) })).toBe(
      'netjes: \'gaps\' has "css": "styles/none.css" in the "netjes" field of its package.json, which is no file in the package',
    );
    expect(await configError(['gaps'], { 'gaps/package.json': manifest('gaps', { netjes: { sources: ['lib'] } }) })).toBe(
      'netjes: \'gaps\' lists "lib" in "sources" in the "netjes" field of its package.json, which is no folder in the package',
    );
  });

  it('refuses a name that is no npm package name, a name listed twice, and one named like the app\'s folder of pages', async () => {
    expect(await configError(['../../etc'])).toBe('netjes: "../../etc" in netjes({ packages }) is not the name of an npm package');
    expect(await configError(['@scope/a/b'])).toBe('netjes: "@scope/a/b" in netjes({ packages }) is not the name of an npm package');
    expect(await configError([INSTALLED, INSTALLED])).toBe(`netjes: '${INSTALLED}' is listed twice in netjes({ packages })`);
    // Its file ids would start like the app's own: app/page.tsx, src/app/page.tsx.
    expect(await configError(['app'], { 'app/package.json': manifest('app') })).toBe(
      "netjes: 'app' in netjes({ packages }) is named like the app's folder of pages, app, so its pages would be taken for the app's",
    );
    expect(await configError(['src'], { 'src/package.json': manifest('src') }, { appDir: 'src/app' })).toBe(
      "netjes: 'src' in netjes({ packages }) is named like the app's folder of pages, src/app, so its pages would be taken for the app's",
    );
  });

  it('refuses packages with Tailwind CSS turned off, when the config loads', () => {
    expect(() => netjes({ packages: [INSTALLED], tailwind: false })).toThrow(
      'netjes: page packages style their pages with Tailwind CSS, which netjes({ tailwind: false }) turns off: turn it on, or list no packages',
    );
    expect(() => netjes({ packages: [], tailwind: false })).not.toThrow();
  });
});

describe("page packages in the app's Tailwind CSS", () => {
  let app: PackagesApp;
  beforeAll(async () => {
    app = await createPackagesApp();
  });
  afterAll(async () => {
    await app?.remove();
  });

  /** The plugin that adds the packages to a stylesheet, with the config read for the app's folder. */
  async function stylesPlugin(packages: string[]) {
    const plugins = await flatten(netjes({ packages }));
    const netjesPlugin = plugins.find((p) => p.name === 'netjes');
    if (!netjesPlugin) throw new Error('netjes plugin missing');
    callConfig(netjesPlugin, { root: app.root });
    const styles = plugins.find((p) => p.name === 'netjes:package-styles');
    if (!styles) throw new Error('netjes:package-styles plugin missing');
    return { plugins, styles };
  }

  async function transform(plugin: Plugin, code: string, id: string) {
    const hook = plugin.transform;
    const handler = typeof hook === 'function' ? hook : hook?.handler;
    const result = (await handler?.call({} as never, code, id, {} as never)) as string | { code: string } | null | undefined;
    return typeof result === 'object' && result !== null ? result.code : (result ?? code);
  }

  /** What the plugin adds after the app's import of Tailwind, for a stylesheet in app/: both packages, or the installed one's sources only. */
  function added(only?: 'installed sources') {
    const fromApp = (target: string) => normalizePath(path.relative(path.join(app.root, 'app'), target));
    const sources = [`@source "${fromApp(path.join(app.installed, 'app'))}";`, `@source "${fromApp(path.join(app.installed, 'src'))}";`];
    if (only) return sources.join(' ');
    return [
      `@import "${fromApp(path.join(app.installed, 'styles/installed.css'))}";`,
      ...sources,
      `@source "${fromApp(path.join(app.linked, 'pages'))}";`,
      `@source "${fromApp(path.join(app.linked, 'lib'))}";`,
    ].join(' ');
  }

  const globals = () => `${normalizePath(app.root)}/app/globals.css`;

  it("adds each package's stylesheet and source folders after the app's import of Tailwind, relative to the stylesheet", async () => {
    const { styles } = await stylesPlugin([INSTALLED, LINKED]);

    const css = await transform(styles, '@import "tailwindcss";\n\nbody { margin: 0; }\n', globals());

    // On the line of the import, so the lines below keep their numbers.
    expect(css).toBe(`@import "tailwindcss"; ${added()}\n\nbody { margin: 0; }\n`);
    expect(added()).toContain('"../node_modules/@netjes-test/installed/styles/installed.css"');
    expect(added()).not.toContain('\\');
    // From a stylesheet above the package, a path that starts with ./, which Tailwind does not take for a package name.
    expect(await transform(styles, '@import "tailwindcss";\n', `${normalizePath(app.root)}/globals.css`)).toContain(
      `@import "./node_modules/${INSTALLED}/styles/installed.css"; @source "./node_modules/${INSTALLED}/app";`,
    );
  });

  it("finds the import in either quotes, with Tailwind's import options, and in every variant of the stylesheet", async () => {
    const { styles } = await stylesPlugin([INSTALLED, LINKED]);

    expect(await transform(styles, "@import 'tailwindcss';\n", globals())).toBe(`@import 'tailwindcss'; ${added()}\n`);
    expect(await transform(styles, '@import "tailwindcss" source(none);\n', globals())).toBe(`@import "tailwindcss" source(none); ${added()}\n`);
    expect(await transform(styles, "@import 'tailwindcss' prefix(tw);\n", globals())).toBe(`@import 'tailwindcss' prefix(tw); ${added()}\n`);
    expect(await transform(styles, '@import url("tailwindcss");\n', globals())).toBe(`@import url("tailwindcss"); ${added()}\n`);
    expect(await transform(styles, '@import "tailwindcss"', globals())).toBe(`@import "tailwindcss"; ${added()}`);
    expect(await transform(styles, '@import "tailwindcss";\n', `${globals()}?inline`)).toBe(`@import "tailwindcss"; ${added()}\n`);
  });

  it("imports a package's stylesheet once when the app's stylesheet imports it already", async () => {
    const { styles } = await stylesPlugin([INSTALLED]);
    const sources = added('installed sources');
    const byName = `@import "tailwindcss";\n@import "${INSTALLED}/styles/installed.css";\n`;
    const byPath = `@import 'tailwindcss';\n@import '../node_modules/${INSTALLED}/styles/installed.css';\n`;

    expect(await transform(styles, byName, globals())).toBe(byName.replace('";\n', `"; ${sources}\n`));
    expect(await transform(styles, byPath, globals())).toBe(byPath.replace("';\n", `'; ${sources}\n`));
  });

  it("leaves alone a stylesheet without Tailwind's import, one in a package, and other modules", async () => {
    const { styles } = await stylesPlugin([INSTALLED]);

    expect(await transform(styles, '@import "./other.css";\n', globals())).toBe('@import "./other.css";\n');
    expect(await transform(styles, '@import "tailwindcss/theme";\n', globals())).toBe('@import "tailwindcss/theme";\n');
    expect(await transform(styles, '@import "tailwindcss";\n', `${normalizePath(app.installed)}/styles/installed.css`)).toBe('@import "tailwindcss";\n');
    expect(await transform(styles, 'import "tailwindcss";\n', `${normalizePath(app.root)}/app/page.tsx`)).toBe('import "tailwindcss";\n');
    // Without packages there is no such plugin.
    expect((await flatten(netjes())).map((p) => p.name)).not.toContain('netjes:package-styles');
  });

  it("runs before Tailwind's plugins, which run before the others too, so the order in the list decides", async () => {
    const { plugins } = await stylesPlugin([INSTALLED]);
    const names = plugins.map((p) => p.name);
    const tailwind = names.filter((name) => name.startsWith('@tailwindcss/vite'));

    expect(tailwind.length).toBeGreaterThan(0);
    expect(plugins.find((p) => p.name === 'netjes:package-styles')?.enforce).toBe('pre');
    for (const name of tailwind) expect(names.indexOf('netjes:package-styles')).toBeLessThan(names.indexOf(name));
  });
});

describe('the netjes modules the plugin names', () => {
  // App code imports netjes through the package's exports, and the plugin names its entries by path. Both must be the
  // same files: a module under two paths is two modules, and a second router would not see the first one's context.
  // Vite names a file by its path with forward slashes, on Windows too, so the plugin must hand it those.
  const exported = (entry: string) => normalizePath(createRequire(import.meta.url).resolve(entry));

  it('builds and serves the client entry the package exports', async () => {
    const { plugin } = await core();
    const resolve = plugin.resolveId as (id: string) => string | undefined;

    expect(callConfig(plugin, {}, { command: 'build', mode: 'production' })?.build?.rollupOptions?.input).toBe(exported('netjes/client-entry'));
    expect(resolve('/@netjes/client-entry')).toBe(exported('netjes/client-entry'));
  });

  it('builds the server entry the package exports', async () => {
    const { plugin } = await core();

    expect(callConfig(plugin, {}, { command: 'build', mode: 'production', isSsrBuild: true })?.build?.ssr).toBe(exported('netjes/server-entry'));
  });

  it('takes the query shell from the modules the package exports', async () => {
    const { plugin } = await core();

    // The import names the module by a JSON string, so a Windows path has its backslashes escaped: read it as JSON.
    const literal = /from ("(?:[^"\\]|\\.)*")/.exec(await loadVirtual(plugin, 'virtual:netjes/query'))?.[1] ?? '""';
    const shell = JSON.parse(literal) as string;

    expect(path.posix.dirname(shell)).toBe(path.posix.dirname(exported('netjes')));
  });

  it('sets up Vitest with the setup file the package exports, and its own next to it', async () => {
    const { plugin } = await core();
    const cleanup = exported('netjes/testing/setup');

    expect(callConfig(plugin)?.test?.setupFiles).toEqual([cleanup, path.posix.join(path.posix.dirname(cleanup), 'testing-vitest.js')]);
  });
});

describe('netjes() defaults', () => {
  it('includes React and Tailwind CSS', async () => {
    const { plugins } = await core();

    expect(plugins.some((p) => p.name.startsWith('vite:react'))).toBe(true);
    expect(plugins.some((p) => p.name.startsWith('@tailwindcss/vite'))).toBe(true);
  });

  it('leaves Tailwind out with tailwind: false', async () => {
    const { plugins } = await core({ tailwind: false });

    expect(plugins.some((p) => p.name.startsWith('@tailwindcss/vite'))).toBe(false);
  });

  it('builds for a base path (the app mounts it with UsePathBase), and serves dev modules from the root', async () => {
    const { plugin } = await core({ basePath: '/base/' });

    expect(callConfig(plugin, {}, { command: 'build', mode: 'production' })?.base).toBe('/base/');
    expect(callConfig(plugin, {}, { command: 'build', mode: 'production', isSsrBuild: true })?.base).toBe('/base/');
    expect(callConfig(plugin)?.base).toBeUndefined();
  });

  it('pre-bundles web-vitals when the app has it, so netjes/vitals does not trigger a dev reload', async () => {
    const { plugin } = await core();

    expect(callConfig(plugin)?.optimizeDeps?.include).toContain('web-vitals');
    expect(callConfig(plugin, { root: '/nonexistent/app' })?.optimizeDeps?.include).not.toContain('web-vitals');
  });

  it('provides the TanStack Query shell and pre-bundles TanStack Query', async () => {
    const { plugin } = await core();

    expect(await loadVirtual(plugin, 'virtual:netjes/query')).toContain('createQueryShell({})');
    expect(callConfig(plugin)?.optimizeDeps?.include).toContain('@tanstack/react-query');
  });

  it('shares one copy of React and TanStack Query with a linked netjes package', async () => {
    const { plugin } = await core();

    expect(callConfig(plugin)?.resolve?.dedupe).toEqual(expect.arrayContaining(['react', 'react-dom', '@tanstack/react-query']));
  });

  it('lets Vite serve the netjes package when it is linked from outside the app', async () => {
    const { plugin } = await core();

    const allow = callConfig(plugin, { root: '/apps/shop' })?.server?.fs?.allow ?? [];

    expect(allow).toContain(fileURLToPath(new URL('..', import.meta.url)));
    expect(allow.length).toBeGreaterThan(1);
  });

  it('keeps backend files out of the dev server and repeats the patterns Vite denies by default', async () => {
    const { plugin } = await core();

    const deny = callConfig(plugin, { root: '/apps/shop' })?.server?.fs?.deny ?? [];

    expect(deny).toEqual(
      expect.arrayContaining([
        '**/appsettings*.json',
        '**/launchSettings.json',
        `${absolute('/apps/shop')}/**/bin/**`,
        `${absolute('/apps/shop')}/**/obj/**`,
        '**/*.cs',
        '**/*.csproj',
        '**/*.pdb',
        '**/*.dll',
        '**/*.{pfx,p12,key}',
        '**/*.{db,sqlite,sqlite3}',
        `${absolute('/apps/shop')}/**/dist/server/**`,
        // Vite's own defaults, which a configured list replaces.
        '.env',
        '.env.*',
        '*.{crt,pem}',
        '**/.git/**',
      ]),
    );
  });

  it('denies backend directories inside the workspace, not an app that lives under a bin or obj folder', async () => {
    const { plugin } = await core();
    const picomatch = createRequire(import.meta.url)('picomatch') as (
      glob: string,
      options: { dot: boolean; nocase: boolean },
    ) => (path: string) => boolean;
    // Vite matches the absolute path of every file it serves or loads, and prefixes slash-less patterns with **/.
    const denied = (root: string, file: string) =>
      (callConfig(plugin, { root })?.server?.fs?.deny ?? []).some((glob) =>
        picomatch(glob.includes('/') ? glob : `**/${glob}`, { dot: true, nocase: true })(`${absolute(root)}/${file}`),
      );

    expect(denied('/work/me/bin/shop', 'pages/index.tsx')).toBe(false);
    expect(denied('/work/me/obj/shop', 'logo.svg')).toBe(false);
    expect(denied('/work/me/bin/shop', 'bin/Debug/net10.0/Shop.txt')).toBe(true);
    expect(denied('/work/me/bin/shop', 'obj/project.assets.txt')).toBe(true);
    expect(denied('/work/me/bin/shop', 'dist/server/server-entry.txt')).toBe(true);
    expect(denied('/work/me/shop (v2)', 'dist/server/server-entry.txt')).toBe(true);
  });

  it('denies the files that hold credentials and deployment settings, in any case and in any folder', async () => {
    const { plugin } = await core();
    const picomatch = createRequire(import.meta.url)('picomatch') as (
      glob: string,
      options: { dot: boolean; nocase: boolean },
    ) => (path: string) => boolean;
    const denied = (file: string) =>
      (callConfig(plugin, { root: '/apps/shop' })?.server?.fs?.deny ?? []).some((glob) =>
        picomatch(glob.includes('/') ? glob : `**/${glob}`, { dot: true, nocase: true })(file),
      );

    for (const file of [
      '/apps/shop/nuget.config',
      '/apps/shop/NuGet.Config',
      '/apps/NuGet.config',
      '/apps/shop/secrets.json',
      '/apps/shop/Shop.csproj.user',
      '/apps/shop/launch.user',
      '/apps/shop/compose.yaml',
      '/apps/shop/compose.prod.yml',
      '/apps/shop/docker-compose.yml',
      '/apps/shop/docker-compose.override.yaml',
      '/apps/shop/Directory.Build.props',
      '/apps/shop/Directory.Build.targets',
      '/apps/Directory.Build.rsp',
      '/apps/shop/global.json',
    ]) {
      expect(denied(file), file).toBe(true);
    }

    for (const file of ['/apps/shop/pages/compose.tsx', '/apps/shop/pages/secrets.tsx', '/apps/shop/package.json', '/apps/shop/src/user.ts']) {
      expect(denied(file), file).toBe(false);
    }
  });

  it('does not widen an fs.allow the app set itself to the workspace root', async () => {
    const { plugin } = await core();

    const allow = callConfig(plugin, { root: '/apps/shop', server: { fs: { allow: ['/apps/shop/app'] } } })?.server?.fs?.allow ?? [];

    expect(allow).toEqual([fileURLToPath(new URL('..', import.meta.url))]);
  });

  it('passes query defaultOptions through to the QueryClient', async () => {
    const { plugin } = await core({ query: { defaultOptions: { queries: { staleTime: 5_000 } } } });

    expect(await loadVirtual(plugin, 'virtual:netjes/query')).toContain('createQueryShell({"defaultOptions":{"queries":{"staleTime":5000}}})');
  });

  it('keeps values JSON cannot carry, such as Infinity', async () => {
    const { plugin } = await core({ query: { defaultOptions: { queries: { staleTime: Number.POSITIVE_INFINITY } } } });

    expect(await loadVirtual(plugin, 'virtual:netjes/query')).toContain('{"queries":{"staleTime":Infinity}}');
  });

  it('rejects query options that cannot reach the browser, such as functions', () => {
    expect(() => netjes({ query: { defaultOptions: { queries: { retry: () => false } } } })).toThrow(/defaultOptions\.queries\.retry/);
  });

  it('shares one copy of TanStack Query with the app', async () => {
    const { plugin } = await core();

    expect(callConfig(plugin)?.resolve?.dedupe).toContain('@tanstack/react-query');
  });

  it('turns TanStack Query off with query: false', async () => {
    const { plugin } = await core({ query: false });

    expect(await loadVirtual(plugin, 'virtual:netjes/query')).toBe('export const QueryShell = undefined;\n');
    expect(callConfig(plugin)?.optimizeDeps?.include).not.toContain('@tanstack/react-query');
  });
});

describe('Vitest defaults', () => {
  it('runs tests in jsdom with the netjes test setup, leaving Playwright specs and build output out', async () => {
    const { plugin } = await core();

    const test = callConfig(plugin)?.test;

    expect(test?.environment).toBe('jsdom');
    expect(test?.include).toBeUndefined();
    expect(test?.exclude).toEqual(
      expect.arrayContaining(['**/node_modules/**', '**/dist/**', '**/bin/**', '**/obj/**', '**/e2e/**']),
    );
    expect(String((test?.setupFiles as string[])[0])).toMatch(/dist[\\/]testing-setup\.js$/);
    expect(String((test?.setupFiles as string[])[1])).toMatch(/dist[\\/]testing-vitest\.js$/);
  });

  it("adds Netjes's own setup file to every app's, after the app's own", async () => {
    const { plugin } = await core();

    expect(mergedTest(plugin, {})?.setupFiles).toEqual([expect.stringMatching(/dist[\\/]testing-setup\.js$/), OWN_SETUP]);
    expect(mergedTest(plugin, { test: { setupFiles: ['./mine.ts'] } })?.setupFiles).toEqual(['./mine.ts', OWN_SETUP]);
    expect(mergedTest(plugin, { test: { setupFiles: './mine.ts' } })?.setupFiles).toEqual(['./mine.ts', OWN_SETUP]);
    expect(mergedTest(plugin, { test: { setupFiles: [] } })?.setupFiles).toEqual([OWN_SETUP]);
    expect(mergedTest(plugin, { root: '/nonexistent/app' })?.setupFiles).toEqual([OWN_SETUP]);
    expect(mergedTest((await core({ test: false })).plugin, { test: { setupFiles: ['./mine.ts'] } })?.setupFiles).toEqual(['./mine.ts']);
  });

  it('shares the app copy of Testing Library with the setup file, under Vitest only', async () => {
    const { plugin } = await core();

    expect(callConfig(plugin)?.resolve?.dedupe).toContain('@testing-library/react');
    expect(callConfig(plugin, {}, { command: 'serve', mode: 'development' })?.resolve?.dedupe).not.toContain('@testing-library/react');
  });

  it("leaves settings the app configured itself alone, adding only its own setup file to the app's", async () => {
    const { plugin } = await core();

    const test = callConfig(plugin, { test: { environment: 'happy-dom', exclude: ['x/**'], setupFiles: [] } } as UserConfig)?.test;

    expect(test?.environment).toBeUndefined();
    expect(test?.exclude).toBeUndefined();
    expect(test?.setupFiles).toEqual([OWN_SETUP]);
  });

  it('only uses jsdom and Testing Library when the app has them installed', async () => {
    const { plugin } = await core();

    const test = callConfig(plugin, { root: '/nonexistent/app' })?.test;

    expect(test?.environment).toBeUndefined();
    expect(test?.setupFiles).toEqual([OWN_SETUP]);
  });

  it("provides the app's query defaultOptions to the tests, next to the app's own provide", async () => {
    const defaultOptions = { queries: { staleTime: Number.POSITIVE_INFINITY, retry: 1 }, mutations: { retry: 2 } };

    expect(callConfig((await core({ query: { defaultOptions } })).plugin)?.test?.provide).toEqual({ 'netjes:query': defaultOptions });
    expect(callConfig((await core()).plugin)?.test?.provide).toEqual({ 'netjes:query': {} });
    expect(callConfig((await core({ query: {} })).plugin)?.test?.provide).toEqual({ 'netjes:query': {} });
    expect(callConfig((await core({ query: false })).plugin)?.test?.provide).toBeUndefined();

    // A provide key of the app's own, which the app would declare in Vitest's ProvidedContext.
    const mine = { test: { provide: { mine: [1, 2] } } } as unknown as UserConfig;
    expect(callConfig((await core()).plugin, mine)?.test?.provide).toEqual({ 'netjes:query': {} });
    expect(mergedTest((await core()).plugin, mine)?.provide).toEqual({ mine: [1, 2], 'netjes:query': {} });
  });

  it('adds nothing with test: false', async () => {
    const { plugin } = await core({ test: false });

    expect(callConfig(plugin)?.test).toBeUndefined();
  });

  it('does not add test settings to builds', async () => {
    const { plugin } = await core();

    expect(callConfig(plugin, {}, { command: 'build', mode: 'production' })?.test).toBeUndefined();
  });
});

describe('vite build', () => {
  /** Builds an app made of these files, each a module without imports, and removes it again. */
  async function buildApp(files: string[], options?: Parameters<typeof netjes>[0], ssr = false) {
    const root = await mkdtemp(path.join(tmpdir(), 'netjes-plugin-'));
    try {
      for (const file of files) {
        await mkdir(path.dirname(path.join(root, file)), { recursive: true });
        await writeFile(path.join(root, file), 'export default function Page() { return null; }\n');
      }
      await build({ root, configFile: false, logLevel: 'silent', build: { ssr }, plugins: [netjes({ tailwind: false, ...options })] });
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  }

  // Development and every command that starts the app stop on it with this message; the build would leave it out.
  const routeHandler = (file: string) =>
    `'${file}': route handlers are C# in Netjes. Map a minimal API endpoint instead (app.MapGet, app.MapPost, ...).`;

  it('fails on a route handler in app/, with the message the app stops with', async () => {
    await expect(buildApp(['app/page.tsx', 'app/api/route.ts'])).rejects.toThrow(routeHandler('app/api/route.ts'));
  });

  it('fails on one in the server build too, in any extension', async () => {
    await expect(buildApp(['app/page.tsx', 'app/api/route.js'], {}, true)).rejects.toThrow(routeHandler('app/api/route.js'));
  });

  it('names the first in ordinal order, as the app does', async () => {
    await expect(buildApp(['app/page.tsx', 'app/b/route.tsx', 'app/a/route.ts'])).rejects.toThrow(routeHandler('app/a/route.ts'));
  });

  it('follows a custom appDir', async () => {
    await expect(buildApp(['src/app/page.tsx', 'src/app/api/route.ts'], { appDir: 'src/app' })).rejects.toThrow(
      routeHandler('src/app/api/route.ts'),
    );
    await expect(buildApp(['app/page.tsx', 'app/_lib/x/route.ts', 'app/api/route.ts'], { appDir: './app' })).rejects.toThrow(
      routeHandler('app/api/route.ts'),
    );
  });

  it('builds with the route files the app does not route: tests, private folders, slots and files outside app/', async () => {
    await expect(
      buildApp(['app/page.tsx', 'app/api/route.test.ts', 'app/_lib/route.ts', 'app/@modal/route.ts', 'app/api/Route.ts', 'lib/route.ts']),
    ).resolves.toBeUndefined();
  });
});

describe('vite build with page packages', () => {
  let app: PackagesApp;
  beforeAll(async () => {
    app = await createPackagesApp();
    await app.write('app/layout.tsx', "import './globals.css';\nexport default function Layout({ children }) { return <main>{children}</main>; }\n");
    await app.write('app/page.tsx', 'export default function Home() { return <h1 className="text-[#3d4e5f]">Home</h1>; }\n');
    await app.write('app/globals.css', '@import "tailwindcss";\n');
  }, 30_000);
  afterAll(async () => {
    await app?.remove();
  });

  /** Builds the app with these packages listed, as `vite build` (or `vite build --ssr`) does with its config. */
  async function buildWith(packages: string[], ssr = false) {
    await build({ root: app.root, configFile: false, logLevel: 'silent', build: { ssr }, plugins: [netjes({ packages })] });
  }

  const readJson = async (file: string) => JSON.parse(await readFile(path.join(app.root, file), 'utf8')) as unknown;

  it('records each package in dist/client/.vite/netjes.json, with the manifest key of each of its files', async () => {
    await buildWith([INSTALLED, LINKED]);

    const record = (await readJson('dist/client/.vite/netjes.json')) as { packages: Array<{ files: Record<string, string> }> };
    const linked = normalizePath(path.relative(app.root, app.linked));
    const files = (name: string, from: string, pages: string) =>
      Object.fromEntries(
        ['(center)/articles/[slug]/page.tsx', 'layout.tsx', 'page.tsx', 'styled/page.tsx'].map((file) => [`${name}/${pages}/${file}`, `${from}/${pages}/${file}`]),
      );
    expect(record).toEqual({
      packages: [
        { name: INSTALLED, version: '1.2.0', netjes: '>=0.0.1', pages: 'app', files: files(INSTALLED, `node_modules/${INSTALLED}`, 'app') },
        { name: LINKED, version: '0.4.0', netjes: '>=0.0.1', pages: 'pages', files: files(LINKED, linked, 'pages') },
      ],
    });
    // The record's value for each file is the key of its chunk in Vite's manifest, and the chunk's src.
    const manifest = (await readJson('dist/client/.vite/manifest.json')) as Record<string, { src?: string }>;
    for (const key of record.packages.flatMap((pkg) => Object.values(pkg.files))) expect(manifest[key]?.src, key).toBe(key);
    // The file ends with a newline, like the files Netjes writes.
    expect(await readFile(path.join(app.root, 'dist/client/.vite/netjes.json'), 'utf8')).toMatch(/\}\n$/);
  });

  it('writes no record without packages, and none in the server build', async () => {
    await buildWith([]);
    expect(await readdir(path.join(app.root, 'dist/client/.vite'))).toEqual(['manifest.json']);

    await buildWith([INSTALLED], true);
    expect(await readdir(path.join(app.root, 'dist/server'))).not.toContain('.vite');
  });

  it("builds the classes that only a package's sources use, and the package's stylesheet, into the app's CSS", async () => {
    await buildWith([INSTALLED, LINKED]);

    const assets = path.join(app.root, 'dist/client/assets');
    const css = (await Promise.all((await readdir(assets)).filter((file) => file.endsWith('.css')).map((file) => readFile(path.join(assets, file), 'utf8')))).join('\n');
    // The app's own class, a page's of the installed package, and one that the linked package uses outside its pages.
    expect(css).toContain('#3d4e5f');
    expect(css).toContain('#0a1b2c');
    expect(css).toContain('#1c2b3a');
    // The installed package's token, from its stylesheet, which its page uses (text-installed).
    expect(css).toContain('--color-installed:#0b1c2d');
  }, 30_000);

  it('fails on a route handler in a package, naming it by its file id', async () => {
    await app.write('app/api/route.ts', 'export {};\n', app.installed);
    try {
      await expect(buildWith([INSTALLED])).rejects.toThrow(
        `'${INSTALLED}/app/api/route.ts': route handlers are C# in Netjes. Map a minimal API endpoint instead (app.MapGet, app.MapPost, ...).`,
      );
    } finally {
      await rm(path.join(app.installed, 'app/api'), { recursive: true });
    }
  });
});
