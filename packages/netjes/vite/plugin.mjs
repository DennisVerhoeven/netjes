// @ts-check
import { copyFile, mkdir, readdir } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import react from '@vitejs/plugin-react';
import { normalizePath, searchForWorkspaceRoot } from 'vite';

const PACKAGE_ROOT = fileURLToPath(new URL('..', import.meta.url));
// The compiled modules the package exports (npm run build writes them), so the app runs the code it type-checked, and
// gets one copy of each module: the same files the app's own imports of netjes resolve to. Vite names a file by its path
// with forward slashes, on Windows too, so these are named that way.
const CLIENT_ENTRY = normalizePath(fileURLToPath(new URL('../dist/client-entry.js', import.meta.url)));
const SERVER_ENTRY = normalizePath(fileURLToPath(new URL('../dist/server-entry.js', import.meta.url)));
const SIDECAR = fileURLToPath(new URL('../sidecar/server.mjs', import.meta.url));
const QUERY_MODULE = normalizePath(fileURLToPath(new URL('../dist/query.js', import.meta.url)));
const TEST_SETUP = normalizePath(fileURLToPath(new URL('../dist/testing-setup.js', import.meta.url)));

const PAGES_ID = 'virtual:netjes/pages';
const RESOLVED_PAGES_ID = `\0${PAGES_ID}`;
const QUERY_ID = 'virtual:netjes/query';
const RESOLVED_QUERY_ID = `\0${QUERY_ID}`;
/** The URL the .NET dev asset resolver points browsers at. */
const DEV_CLIENT_ENTRY_URL = '/@netjes/client-entry';

/** What Vite denies by default; setting `server.fs.deny` replaces the list. */
const VITE_DENY = ['.env', '.env.*', '*.{crt,pem}', '**/.git/**'];
/** Backend files recognised by their name, wherever they are: configuration, source, build output, dev certificates, SQLite databases, credentials and deployment settings. */
const BACKEND_FILES = ['**/appsettings*.json', '**/launchSettings.json', '**/*.cs', '**/*.csproj', '**/*.pdb', '**/*.dll', '**/*.{pfx,p12,key}', '**/*.{db,sqlite,sqlite3}',
  // Feed credentials, user secrets and per-user settings, deployment files and the build's shared settings. Vite matches without regard to case.
  '**/nuget.config', '**/secrets.json', '**/*.user', '**/compose*.y*ml', '**/docker-compose*.y*ml', '**/Directory.Build.*', '**/global.json',
];
/** Backend folders, matched below the workspace root only: Vite matches absolute paths, so an app that itself lives under ~/bin must still be served. */
const BACKEND_DIRS = ['bin', 'obj', 'dist/server'];

/**
 * The ASP.NET side of the dev root (the content root): configuration, build output, source and the server bundle.
 * @param {string} workspaceRoot
 */
function backendDeny(workspaceRoot) {
  const root = normalizePath(workspaceRoot).replace(/[()[\]{}!+@*?\\]/g, '\\$&');
  return [...BACKEND_FILES, ...BACKEND_DIRS.map((dir) => `${root}/**/${dir}/**`)];
}

/** Loads @tailwindcss/vite only when Tailwind is on, so apps that turn it off need not install it. */
async function tailwind() {
  try {
    const { default: plugin } = await import('@tailwindcss/vite');
    return plugin();
  } catch (error) {
    throw new Error(
      'netjes: Tailwind CSS is on by default but @tailwindcss/vite could not be loaded. ' +
        'Install it (npm install -D tailwindcss @tailwindcss/vite) or turn it off with netjes({ tailwind: false }).',
      { cause: error },
    );
  }
}

/**
 * Whether the app (or a parent workspace) can import a package.
 * @param {string} root
 * @param {string} name
 */
function canResolve(root, name) {
  try {
    createRequire(path.join(root, 'package.json')).resolve(name);
    return true;
  } catch {
    return false;
  }
}

/**
 * Vitest settings for component tests, unless the app set them itself: a DOM (when jsdom is installed), Testing
 * Library cleanup (when it is installed), and no Playwright specs (e2e/), build output or .NET bin/obj folders.
 * @param {Record<string, unknown> | undefined} test
 * @param {string} root
 */
function testDefaults(test = {}, root) {
  /** @type {Record<string, unknown>} */
  const defaults = {};
  if (test.environment === undefined && canResolve(root, 'jsdom')) defaults.environment = 'jsdom';
  if (test.exclude === undefined) {
    defaults.exclude = ['**/node_modules/**', '**/dist/**', '**/bin/**', '**/obj/**', '**/e2e/**', '**/.git/**'];
  }
  if (test.setupFiles === undefined && canResolve(root, '@testing-library/react')) defaults.setupFiles = [TEST_SETUP];
  return defaults;
}

/**
 * Writes a value as a JavaScript literal for the generated query module. Unlike JSON it keeps Infinity (a common
 * staleTime); anything that cannot reach the browser as a literal, such as a function, is an error.
 * @param {unknown} value
 * @param {string} at
 * @returns {string}
 */
function literal(value, at) {
  if (value === null || typeof value === 'string' || typeof value === 'boolean') return JSON.stringify(value);
  if (typeof value === 'number') {
    if (Number.isNaN(value)) throw new Error(`netjes: ${at} is NaN`);
    return Number.isFinite(value) ? JSON.stringify(value) : value > 0 ? 'Infinity' : '-Infinity';
  }
  if (Array.isArray(value)) return `[${value.map((item, i) => literal(item, `${at}[${i}]`)).join(',')}]`;
  if (typeof value === 'object' && Object.getPrototypeOf(value) === Object.prototype) {
    const entries = Object.entries(/** @type {Record<string, unknown>} */ (value)).filter(([, item]) => item !== undefined);
    return `{${entries.map(([key, item]) => `${JSON.stringify(key)}:${literal(item, `${at}.${key}`)}`).join(',')}}`;
  }
  throw new Error(
    `netjes: ${at} is a ${typeof value}. netjes({ query }) options are written into the browser bundle, so only ` +
      'plain values work there; set function options per query (useQuery({ retry })) instead.',
  );
}

/**
 * The first route handler file (`route.ts`, `route.js`, ...) in the app directory, in ordinal order, as the app's route
 * table would name it, relative to the root; null when there is none. Like the route table, it leaves out tests
 * (`route.test.ts`), private folders (`_lib/`) and parallel route slots (`@modal/`), which hold no route handlers.
 * @param {string} root
 * @param {string} appDir
 * @returns {Promise<string | null>}
 */
async function findRouteHandler(root, appDir) {
  const dir = path.join(root, appDir);
  /** @type {import('node:fs').Dirent[]} */
  let entries;
  try {
    entries = await readdir(dir, { recursive: true, withFileTypes: true });
  } catch {
    return null;
  }
  const handlers = entries
    .filter((entry) => entry.isFile() && path.parse(entry.name).name === 'route')
    .filter((entry) =>
      normalizePath(path.relative(dir, entry.parentPath))
        .split('/')
        .every((folder) => !folder.startsWith('_') && !(folder.length > 1 && folder.startsWith('@'))),
    )
    .map((entry) => normalizePath(path.relative(root, path.join(entry.parentPath, entry.name))));
  // UTF-16 code unit order, as the route table's ordinal order.
  return handlers.sort((a, b) => (a < b ? -1 : a > b ? 1 : 0))[0] ?? null;
}

/**
 * Vite plugin for Netjes apps: React with Fast Refresh, Tailwind CSS, the built-in TanStack Query client, the page
 * module map, Vitest defaults, and the client + server builds (`vite build` writes dist/client with a manifest,
 * `vite build --ssr` writes dist/server with the sidecar).
 * @param {import('./plugin.d.ts').NetjesPluginOptions} [options]
 * @returns {import('vite').PluginOption[]}
 */
export default function netjes(options = {}) {
  const appDir = (options.appDir ?? 'app').replace(/^\/+|\/+$/g, '');
  // Only the convention files are routes; everything else in app/ is colocated code.
  const appFiles = `/${appDir}/**/{page,layout,template,loading,error,not-found,forbidden,unauthorized,global-error,default}.{tsx,jsx}`;
  const basePath = (options.basePath ?? '').replace(/\/+$/, '');
  const query = options.query === false ? null : options.query === true || options.query === undefined ? {} : options.query;
  // Fail when the config loads, not in the browser.
  const queryLiteral = query ? literal(query, 'query') : '';
  let isSsrBuild = false;
  let isBuild = false;
  /** @type {string} */
  let rootDir = '';
  /** @type {string} */
  let outDir = '';
  /** @type {import('vite').ViteDevServer | undefined} */
  let devServer;

  /** @type {import('vite').Plugin} */
  const plugin = {
    name: 'netjes',
    config(config, env) {
      isSsrBuild = env.isSsrBuild === true;
      isBuild = env.command === 'build';
      // `test` is Vitest's addition to the Vite config.
      const userTest = /** @type {{ test?: Record<string, unknown> }} */ (/** @type {unknown} */ (config)).test;
      const root = path.resolve(config.root ?? process.cwd());
      const workspaceRoot = searchForWorkspaceRoot(root);
      const test = env.command === 'serve' && options.test !== false ? { test: testDefaults(userTest, root) } : {};
      // Built asset URLs live under the base path; in development the browser loads modules from the Vite server's root.
      const base = env.command === 'build' && basePath ? { base: `${basePath}/` } : {};
      return {
        ...test,
        ...base,
        appType: 'custom',
        // One copy of each, even when netjes is linked (a `file:` dependency, npm link) and has its own node_modules.
        resolve: {
          dedupe: [
            'react',
            'react-dom',
            ...(query ? ['@tanstack/react-query'] : []),
            // The test setup's cleanup must unmount what the app's tests rendered.
            ...(env.mode === 'test' ? ['@testing-library/react'] : []),
          ],
        },
        // netjes serves its own modules (client entry, runtime) and, for their source maps, its sources; a linked
        // package lives outside the app. The workspace root keeps Vite's default, but only when the app did not narrow
        // fs.allow itself: Vite concatenates arrays. The dev root is the .NET content root, so deny the backend's files.
        // A configured deny list replaces Vite's defaults, which are repeated here.
        server: {
          fs: {
            allow: [...(config.server?.fs?.allow ? [] : [workspaceRoot]), PACKAGE_ROOT],
            deny: [...VITE_DENY, ...backendDeny(workspaceRoot)],
          },
        },
        optimizeDeps: {
          // There is no index.html to scan; start from the pages so dependencies are pre-bundled up front.
          entries: [appFiles.slice(1)],
          include: [
            'react',
            'react-dom',
            'react-dom/client',
            ...(query ? ['@tanstack/react-query'] : []),
            // Imported from inside netjes (netjes/vitals), which the dependency scan skips: found late, it would reload the page.
            ...(canResolve(root, 'web-vitals') ? ['web-vitals'] : []),
          ],
          // netjes imports the virtual modules below, which pre-bundling cannot resolve, and the plugin loads its query
          // shell by path: a pre-bundled copy of netjes would hold a second router, whose contexts the app's pages miss.
          exclude: ['netjes'],
        },
        // netjes imports the plugin's virtual modules and must share its module instances with the app's pages, so Vite
        // processes it in development as well; production bundles everything for the sidecar.
        ssr: { noExternal: env.command === 'build' ? true : ['netjes'] },
        build: isSsrBuild
          ? {
              outDir: 'dist/server',
              emptyOutDir: true,
              copyPublicDir: false,
              ssr: SERVER_ENTRY,
              rollupOptions: { output: { entryFileNames: 'server-entry.js', format: 'es' } },
            }
          : {
              outDir: 'dist/client',
              emptyOutDir: true,
              manifest: true,
              rollupOptions: { input: CLIENT_ENTRY },
            },
      };
    },
    configResolved(config) {
      rootDir = config.root;
      outDir = path.resolve(config.root, config.build.outDir);
    },
    // The app's route table stops it on a route handler in development, and in every command that starts it. A build
    // takes only the convention files, so it would leave one out without a word: it stops on it too, saying the same.
    async buildStart() {
      if (!isBuild) return;
      const handler = await findRouteHandler(rootDir, appDir);
      if (handler) {
        this.error(`'${handler}': route handlers are C# in Netjes. Map a minimal API endpoint instead (app.MapGet, app.MapPost, ...).`);
      }
    },
    configureServer(server) {
      devServer = server;
    },
    // For the development sidecar, which finds this plugin in its dev server's config.
    api: {
      /**
       * Has the next render, and the next page the browser loads, build the page module map again from the files on
       * disk, as Vite does once its watcher reports a page file added or removed.
       */
      rescanPages() {
        const timestamp = Date.now();
        for (const { moduleGraph } of Object.values(devServer?.environments ?? {})) {
          const pages = moduleGraph.getModuleById(RESOLVED_PAGES_ID);
          if (pages) moduleGraph.invalidateModule(pages, new Set(), timestamp, true);
        }
      },
    },
    resolveId(id) {
      if (id === PAGES_ID) return RESOLVED_PAGES_ID;
      if (id === QUERY_ID) return RESOLVED_QUERY_ID;
      if (id === DEV_CLIENT_ENTRY_URL) return CLIENT_ENTRY;
      return undefined;
    },
    load(id) {
      if (id === RESOLVED_QUERY_ID) {
        if (!query) return 'export const QueryShell = undefined;\n';
        return `import { createQueryShell } from ${JSON.stringify(QUERY_MODULE)};\nexport const QueryShell = createQueryShell(${queryLiteral});\n`;
      }
      if (id !== RESOLVED_PAGES_ID) return undefined;
      const glob = JSON.stringify([appFiles]);
      return `export const pages = import.meta.glob(${glob});\n`;
    },
    async writeBundle() {
      if (!isSsrBuild) return;
      await mkdir(outDir, { recursive: true });
      await copyFile(SIDECAR, path.join(outDir, 'netjes-sidecar.mjs'));
    },
  };

  return [react(options.react), plugin, options.tailwind === false ? null : tailwind()];
}
