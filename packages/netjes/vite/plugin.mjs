// @ts-check
import { readFileSync } from 'node:fs';
import { copyFile, mkdir, readdir, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import react from '@vitejs/plugin-react';
import { normalizePath, searchForWorkspaceRoot } from 'vite';
import { findPackage } from '../cli/packages.mjs';
import {
  CONVENTION_EXTENSIONS,
  CONVENTION_FILES,
  commonJsDependencies,
  fileId,
  filePath,
  isConventionFile,
  isPackageName,
  pagePackageFiles,
  resolvePagePackages,
  unsupportedNetjes,
  withPackageStyles,
} from './packages.mjs';

const PACKAGE_ROOT = fileURLToPath(new URL('..', import.meta.url));
/** The version of this netjes, the app's: what a page package's peer range has to hold. */
const VERSION = /** @type {{ version: string }} */ (JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'))).version;
// The compiled modules the package exports (npm run build writes them), so the app runs the code it type-checked, and
// gets one copy of each module: the same files the app's own imports of netjes resolve to. Vite names a file by its path
// with forward slashes, on Windows too, so these are named that way.
const CLIENT_ENTRY = normalizePath(fileURLToPath(new URL('../dist/client-entry.js', import.meta.url)));
const SERVER_ENTRY = normalizePath(fileURLToPath(new URL('../dist/server-entry.js', import.meta.url)));
const SIDECAR = fileURLToPath(new URL('../sidecar/server.mjs', import.meta.url));
const QUERY_MODULE = normalizePath(fileURLToPath(new URL('../dist/query.js', import.meta.url)));
const TEST_SETUP = normalizePath(fileURLToPath(new URL('../dist/testing-setup.js', import.meta.url)));
// What the test harness needs from Vitest: the setup file every app's tests get, after the app's own. Not exported.
const TEST_VITEST = normalizePath(fileURLToPath(new URL('../dist/testing-vitest.js', import.meta.url)));

const PAGES_ID = 'virtual:netjes/pages';
const RESOLVED_PAGES_ID = `\0${PAGES_ID}`;
const QUERY_ID = 'virtual:netjes/query';
const RESOLVED_QUERY_ID = `\0${QUERY_ID}`;
/**
 * A module @tailwindcss/vite builds, by its rule: a `.css` file, unless it is imported as text, as a URL or as a worker,
 * which by the time a plugin transforms it is a JavaScript module that exports the text.
 * @param {string} id
 */
const isTailwindStylesheet = (id) =>
  !/[?&](?:worker|sharedworker|raw|url)\b|\?commonjs-proxy/.test(id) && !id.includes('/.vite/') && (id.split('?')[0] ?? id).endsWith('.css');
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
  return [...BACKEND_FILES, ...backendDirs(workspaceRoot)];
}

/**
 * The backend folders below a folder, such as a page package's own `bin/` and `obj/`.
 * @param {string} dir
 */
function backendDirs(dir) {
  const root = normalizePath(dir).replace(/[()[\]{}!+@*?\\]/g, '\\$&');
  return BACKEND_DIRS.map((folder) => `${root}/**/${folder}/**`);
}

/**
 * A path as a glob that matches it alone: the characters a glob reads, escaped as Vite's glob (tinyglobby) escapes them.
 * @param {string} file With `/` separators.
 */
const literalGlob = (file) => file.replace(/[()[\]{}*?|]|[!+@](?=\()/g, '\\$&');

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
 * Library cleanup (when it is installed), and no Playwright specs (e2e/), build output or .NET bin/obj folders. Two
 * exceptions, which Vite merges into what the app set: Netjes's own setup file goes into every app's setupFiles,
 * after the app's own, and provide['netjes:query'] holds the app's query defaultOptions, which that file hands to
 * createTestApp. Only that key is returned, so the app's own provide keys are not merged twice. Vitest 5 builds the
 * test options of an inline test project that shares the root's Vite server from the root's options as written, before
 * this hook runs, so such a project gets none of these; with sharedViteServer: false it resolves the config, and this
 * hook, for each project.
 * @param {Record<string, unknown> | undefined} test
 * @param {string} root
 * @param {{ defaultOptions?: import('@tanstack/react-query').DefaultOptions } | null} query
 */
function testDefaults(test = {}, root, query) {
  /** @type {Record<string, unknown>} */
  const defaults = {};
  if (test.environment === undefined && canResolve(root, 'jsdom')) defaults.environment = 'jsdom';
  if (test.exclude === undefined) {
    defaults.exclude = ['**/node_modules/**', '**/dist/**', '**/bin/**', '**/obj/**', '**/e2e/**', '**/.git/**'];
  }
  const cleanup = test.setupFiles === undefined && canResolve(root, '@testing-library/react');
  defaults.setupFiles = cleanup ? [TEST_SETUP, TEST_VITEST] : [TEST_VITEST];
  if (query) defaults.provide = { 'netjes:query': query.defaultOptions ?? {} };
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
  const appFiles = `/${appDir}/**/{${CONVENTION_FILES.join(',')}}.{${CONVENTION_EXTENSIONS.map((ext) => ext.slice(1)).join(',')}}`;
  const packageNames = options.packages ?? [];
  if (packageNames.length > 0 && options.tailwind === false) {
    throw new Error('netjes: page packages style their pages with Tailwind CSS, which netjes({ tailwind: false }) turns off: turn it on, or list no packages');
  }
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
  /** The page packages the app lists, read when the config is. @type {import('./packages.mjs').PagePackage[]} */
  let packages = [];

  /**
   * Has the next render, and the next page the browser loads, build the page module map again from the files on disk,
   * as Vite does once its watcher reports a page file added or removed.
   */
  function rescanPages() {
    const timestamp = Date.now();
    for (const { moduleGraph } of Object.values(devServer?.environments ?? {})) {
      const pages = moduleGraph.getModuleById(RESOLVED_PAGES_ID);
      if (pages) moduleGraph.invalidateModule(pages, new Set(), timestamp, true);
    }
  }

  /**
   * The listed package a file id is of, with the file's path in its folder of pages.
   * @param {string} id
   */
  function packageFile(id) {
    for (const pkg of packages) {
      const prefix = `${pkg.name}/${pkg.pages}/`;
      if (id.startsWith(prefix)) return { pkg, file: id.slice(prefix.length) };
    }
    return null;
  }

  /**
   * The name of the installed package whose file a file id names, when the app does not list it in netjes({ packages }):
   * the .NET app mounts it, and the page module map does not have it.
   * @param {string} id
   * @returns {string | null}
   */
  function unlistedPackage(id) {
    if (id.startsWith(`${appDir}/`) || packageFile(id)) return null;
    const segments = id.split('/');
    const length = id.startsWith('@') ? 2 : 1;
    if (segments.length <= length) return null;
    const name = segments.slice(0, length).join('/');
    return isPackageName(name) && findPackage(rootDir, name) ? name : null;
  }

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
      packages = resolvePagePackages(root, packageNames, appDir);
      // A build stops here, and the development sidecar reports it as Vite failing to start.
      for (const pkg of packages) {
        const unsupported = unsupportedNetjes(pkg, VERSION);
        if (unsupported) throw new Error(`netjes: ${unsupported}`);
      }
      const listed = packages.map((pkg) => pkg.name);
      const test = env.command === 'serve' && options.test !== false ? { test: testDefaults(userTest, root, query) } : {};
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
            // A page package's pages import netjes and zod, which a linked package would otherwise resolve from its own
            // folder: a second copy, or none at all for one linked from outside the workspace.
            ...(packages.length > 0 ? ['netjes', ...(canResolve(root, 'zod') ? ['zod'] : [])] : []),
          ],
        },
        // netjes serves its own modules (client entry, runtime) and, for their source maps, its sources; a linked
        // package lives outside the app, and so does a page package linked from elsewhere. The workspace root keeps
        // Vite's default, but only when the app did not narrow fs.allow itself: Vite concatenates arrays. The dev root is
        // the .NET content root, so deny the backend's files, and a page package's backend folders wherever it is (its
        // C# half's build output, in a checkout linked from outside the workspace). A configured deny list replaces
        // Vite's defaults, which are repeated here.
        server: {
          fs: {
            allow: [...(config.server?.fs?.allow ? [] : [workspaceRoot]), PACKAGE_ROOT, ...packages.map((pkg) => pkg.root)],
            deny: [...VITE_DENY, ...backendDeny(workspaceRoot), ...packages.flatMap((pkg) => backendDirs(pkg.root))],
          },
        },
        optimizeDeps: {
          // There is no index.html to scan; start from the pages so dependencies are pre-bundled up front, a page
          // package's included: Vite globs these from the root, in node_modules and outside the root too.
          entries: [
            appFiles.slice(1),
            ...packages.map((pkg) => `${literalGlob(normalizePath(path.relative(root, `${pkg.root}/${pkg.pages}`)))}/${appFiles.slice(appDir.length + 2)}`),
          ],
          include: [
            'react',
            'react-dom',
            'react-dom/client',
            ...(query ? ['@tanstack/react-query'] : []),
            // Imported from inside netjes (netjes/vitals), which the dependency scan skips: found late, it would reload the page.
            ...(canResolve(root, 'web-vitals') ? ['web-vitals'] : []),
            // A page package is not pre-bundled (below), so its CommonJS dependencies are, through it, as Vite asks.
            ...packages.flatMap((pkg) => commonJsDependencies(pkg).map((dependency) => `${pkg.name} > ${dependency}`)),
          ],
          // netjes imports the virtual modules below, which pre-bundling cannot resolve, and the plugin loads its query
          // shell by path: a pre-bundled copy of netjes would hold a second router, whose contexts the app's pages miss.
          // A page package's pages are loaded by path, so a pre-bundled copy of it would be a second one of each of its
          // modules too.
          exclude: ['netjes', ...listed],
        },
        // netjes imports the plugin's virtual modules and must share its module instances with the app's pages, so Vite
        // processes it in development as well, and a page package's TypeScript sources with it; production bundles
        // everything for the sidecar.
        ssr: { noExternal: env.command === 'build' ? true : ['netjes', ...listed] },
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
      let handler = await findRouteHandler(rootDir, appDir);
      // A package's, by its file id, as the route table names it.
      for (const pkg of packages) {
        if (handler) break;
        const found = await findRouteHandler(pkg.root, pkg.pages);
        if (found) handler = `${pkg.name}/${found}`;
      }
      if (handler) {
        this.error(`'${handler}': route handlers are C# in Netjes. Map a minimal API endpoint instead (app.MapGet, app.MapPost, ...).`);
      }
    },
    configureServer(server) {
      devServer = server;
      // Vite watches the root only, so a linked package's files, which can change while the app runs, are added. The
      // map imports a package's files one by one, so a page that comes or goes builds it again, as the glob would.
      const linked = packages.filter((pkg) => pkg.linked);
      if (linked.length === 0) return;
      server.watcher.add([...new Set(linked.flatMap((pkg) => [pkg.pages, ...pkg.sources].map((dir) => `${pkg.root}/${dir}`)))]);
      /** @param {string} file */
      const pageAddedOrRemoved = (file) => {
        const name = normalizePath(file);
        if (isConventionFile(path.posix.basename(name)) && linked.some((pkg) => name.startsWith(`${pkg.root}/${pkg.pages}/`))) rescanPages();
      };
      server.watcher.on('add', pageAddedOrRemoved);
      server.watcher.on('unlink', pageAddedOrRemoved);
    },
    // For the development sidecar, which finds this plugin in its dev server's config.
    api: {
      /**
       * The URL the dev server loads a file by, from the file's id in a payload: `/app/...` for the app's, and for a
       * listed package's the URL of its real path (`/node_modules/...` inside the root, `/@fs/...` outside it). Null for
       * a file of an installed package that netjes({ packages }) does not list, which the page module map lacks.
       * @param {string} id
       * @returns {string | null}
       */
      pageUrl(id) {
        const owned = packageFile(id);
        if (!owned) return unlistedPackage(id) ? null : `/${id}`;
        const file = filePath(owned.pkg, owned.file);
        const relative = path.posix.relative(normalizePath(rootDir), file);
        return relative.startsWith('../') || path.isAbsolute(relative) ? `/@fs/${file.replace(/^\//, '')}` : `/${relative}`;
      },
      unlistedPackage,
      /** The page packages the app lists. */
      packages: () => packages,
      rescanPages,
    },
    resolveId(id) {
      if (id === PAGES_ID) return RESOLVED_PAGES_ID;
      if (id === QUERY_ID) return RESOLVED_QUERY_ID;
      if (id === DEV_CLIENT_ENTRY_URL) return CLIENT_ENTRY;
      return undefined;
    },
    async load(id) {
      if (id === RESOLVED_QUERY_ID) {
        if (!query) return 'export const QueryShell = undefined;\n';
        return `import { createQueryShell } from ${JSON.stringify(QUERY_MODULE)};\nexport const QueryShell = createQueryShell(${queryLiteral});\n`;
      }
      if (id !== RESOLVED_PAGES_ID) return undefined;
      const glob = `import.meta.glob(${JSON.stringify([appFiles])})`;
      // A package's files are imported one by one, by their real paths: a glob leaves out node_modules, and would name a
      // linked package's files by their paths through the link. Listed again each time, as the glob is.
      const imported = (
        await Promise.all(
          packages.map(async (pkg) =>
            (await pagePackageFiles(pkg)).map((file) => `${JSON.stringify(`/${fileId(pkg, file)}`)}: () => import(${JSON.stringify(filePath(pkg, file))})`),
          ),
        )
      ).flat();
      const pages = packages.length === 0 ? glob : `{ ...${glob}, ${imported.join(', ')} }`;
      // The folder the keys start with: renderDocument (netjes/testing) builds routes and segment ids from it.
      return (
        `export const pages = ${pages};\nexport const appDir = ${JSON.stringify(appDir)};\n` +
        `export const packages = ${JSON.stringify(packages.map(({ name, pages }) => ({ name, pages })))};\n`
      );
    },
    async writeBundle() {
      if (isSsrBuild) {
        await mkdir(outDir, { recursive: true });
        await copyFile(SIDECAR, path.join(outDir, 'netjes-sidecar.mjs'));
        return;
      }
      if (packages.length === 0) return;
      // Where the .NET app finds the packages in production, next to the manifest: no node_modules there to read them
      // from, and a manifest key alone does not say which package a file is of.
      const record = {
        packages: await Promise.all(
          packages.map(async (pkg) => ({
            name: pkg.name,
            version: pkg.version,
            netjes: pkg.netjes,
            pages: pkg.pages,
            // Each file's id, and its key in the manifest: its path from the root, as Vite names a chunk's source.
            files: Object.fromEntries((await pagePackageFiles(pkg)).map((file) => [fileId(pkg, file), normalizePath(path.relative(rootDir, filePath(pkg, file)))])),
            // The NuGet package of its C# half, which the app names when the two halves are different versions.
            ...(pkg.nuget === null ? {} : { nuget: pkg.nuget }),
          })),
        ),
      };
      await mkdir(path.join(outDir, '.vite'), { recursive: true });
      await writeFile(path.join(outDir, '.vite', 'netjes.json'), `${JSON.stringify(record, null, 2)}\n`);
    },
  };

  /**
   * Adds each page package's stylesheet and source folders to the app's Tailwind CSS build. Before @tailwindcss/vite,
   * whose plugins run before the others too (`enforce: 'pre'`), so the order in the list decides.
   * @type {import('vite').Plugin}
   */
  const packageStyles = {
    name: 'netjes:package-styles',
    enforce: 'pre',
    transform(code, id) {
      if (!isTailwindStylesheet(id)) return undefined;
      const css = withPackageStyles(code, id.split('?')[0] ?? id, packages);
      return css === code ? undefined : { code: css, map: null };
    },
  };

  return [react(options.react), plugin, packageNames.length > 0 ? packageStyles : null, options.tailwind === false ? null : tailwind()];
}
