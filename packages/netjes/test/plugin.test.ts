// @vitest-environment node
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { build, normalizePath, type ConfigEnv, type Plugin, type PluginOption, type UserConfig } from 'vite';
import { describe, expect, it } from 'vitest';
import netjes from '../vite/plugin.mjs';

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
      'export const pages = import.meta.glob(["/app/**/{page,layout,template,loading,error,not-found,forbidden,unauthorized,global-error,default}.{tsx,jsx}"]);\n',
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
    expect(code).not.toContain('pages/');
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

  it('sets up Vitest with the setup file the package exports', async () => {
    const { plugin } = await core();

    expect(callConfig(plugin)?.test?.setupFiles).toEqual([exported('netjes/testing/setup')]);
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

    expect(denied('/home/me/bin/shop', 'pages/index.tsx')).toBe(false);
    expect(denied('/home/me/obj/shop', 'logo.svg')).toBe(false);
    expect(denied('/home/me/bin/shop', 'bin/Debug/net10.0/Shop.txt')).toBe(true);
    expect(denied('/home/me/bin/shop', 'obj/project.assets.txt')).toBe(true);
    expect(denied('/home/me/bin/shop', 'dist/server/server-entry.txt')).toBe(true);
    expect(denied('/home/me/shop (v2)', 'dist/server/server-entry.txt')).toBe(true);
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
  });

  it('shares the app copy of Testing Library with the setup file, under Vitest only', async () => {
    const { plugin } = await core();

    expect(callConfig(plugin)?.resolve?.dedupe).toContain('@testing-library/react');
    expect(callConfig(plugin, {}, { command: 'serve', mode: 'development' })?.resolve?.dedupe).not.toContain('@testing-library/react');
  });

  it('leaves settings the app configured itself alone', async () => {
    const { plugin } = await core();

    const test = callConfig(plugin, { test: { environment: 'happy-dom', exclude: ['x/**'], setupFiles: [] } } as UserConfig)?.test;

    expect(test?.environment).toBeUndefined();
    expect(test?.exclude).toBeUndefined();
    expect(test?.setupFiles).toBeUndefined();
  });

  it('only uses jsdom and Testing Library when the app has them installed', async () => {
    const { plugin } = await core();

    const test = callConfig(plugin, { root: '/nonexistent/app' })?.test;

    expect(test?.environment).toBeUndefined();
    expect(test?.setupFiles).toBeUndefined();
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
