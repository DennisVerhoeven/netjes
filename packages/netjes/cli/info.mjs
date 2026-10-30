// @ts-check
// netjes info: the tools and the project setup, one line each, with how to fix each problem it finds.
import { existsSync, statSync } from 'node:fs';
import { readFile, readdir, stat } from 'node:fs/promises';
import path from 'node:path';
import { findPackage } from './packages.mjs';
import { paint } from './terminal.mjs';

/**
 * ok and warn lines leave the exit code at 0; a fail line makes it 1. warn and fail lines say how to fix them.
 * @typedef {{ status: 'ok' | 'warn' | 'fail'; text: string; fix?: string }} Check
 * @typedef {import('./main.mjs').CliIo} CliIo
 */

/**
 * The Node.js releases Netjes and the app template's tools run on: the intersection of the engines of the package and
 * of everything the template installs (vite, vitest, jsdom and their dependencies). The package's and the template's
 * package.json declare the same range, which a test checks.
 */
export const NODE_RANGE = '^22.22.2 || ^24.15.0 || >=26.0.0';
export const NODE_DOWNLOAD = 'https://nodejs.org';
/** The SDK major version Netjes itself needs (it targets net10.0); an app that targets a later one needs that. */
export const MIN_DOTNET = 10;
export const DOTNET_DOWNLOAD = 'https://dotnet.microsoft.com/download';
/** The npm packages a Netjes app runs on. */
const PACKAGES = ['netjes', 'vite', 'react', 'react-dom'];
/** Folders that hold no C# source of the app's own. */
const SKIPPED = new Set(['bin', 'obj', 'node_modules', 'dist', 'out']);
/** Files written within this long of each other, as a checkout or `netjes new` does, count as written together. */
const SAME_TIME_MS = 2000;
/** The Next.js pages router's special files, at the root of pages/, by the App Router file that takes their place. */
const SPECIAL_FILES = new Map([
  ['_app', 'layout'],
  ['_document', 'layout'],
  ['_error', 'error'],
  ['404', 'not-found'],
  ['500', 'error'],
]);
/** How many files a check names before it counts the rest, besides the special files, which it always names. */
const LISTED = 3;

/** @param {string} text @returns {Check} */
const ok = (text) => ({ status: 'ok', text });
/** @param {string} text @param {string} fix @returns {Check} */
const fail = (text, fix) => ({ status: 'fail', text, fix });
/** @param {string} text @param {string} fix @returns {Check} */
const warn = (text, fix) => ({ status: 'warn', text, fix });

/** @param {string | undefined} version @returns {[number, number, number] | null} */
function parseVersion(version) {
  const match = /^v?(\d+)\.(\d+)\.(\d+)/.exec(version ?? '');
  return match ? [Number(match[1]), Number(match[2]), Number(match[3])] : null;
}

/** @param {[number, number, number]} a @param {[number, number, number]} b */
const compare = (a, b) => a[0] - b[0] || a[1] - b[1] || a[2] - b[2];

/**
 * Whether `version` is in `range`, for the forms package.json ranges usually take (^1.2.3, ~1.2.3, >=1, 1.2.3, *,
 * and || between them). A form it does not know passes: only a definite mismatch is reported.
 * @param {string | undefined} version
 * @param {string} range
 */
export function satisfies(version, range) {
  const have = parseVersion(version);
  if (!have) return true;
  return range.split('||').some((part) => {
    const match = /^\s*(\^|~|>=|=)?\s*v?(\d+)(?:\.(\d+))?(?:\.(\d+))?\s*$/.exec(part);
    if (!match) return true;
    /** @type {[number, number, number]} */
    const want = [Number(match[2]), Number(match[3] ?? 0), Number(match[4] ?? 0)];
    const atLeast = compare(have, want) >= 0;
    switch (match[1]) {
      case '>=':
        return atLeast;
      case '^':
        return atLeast && (want[0] > 0 ? have[0] === want[0] : have[1] === want[1]);
      case '~':
        return atLeast && have[0] === want[0] && have[1] === want[1];
      default:
        return match[3] === undefined ? have[0] === want[0] : compare(have, want) === 0;
    }
  });
}

/** @param {string} file @returns {Promise<any>} */
async function readJson(file) {
  try {
    return JSON.parse(await readFile(file, 'utf8'));
  } catch {
    return null;
  }
}

/**
 * @param {string} version
 * @returns {Check}
 */
export function checkNode(version) {
  if (satisfies(version, NODE_RANGE)) return ok(`Node.js ${version}`);
  const major = parseVersion(version)?.[0] ?? 0;
  const fix = `Install a Node.js release that matches ${NODE_RANGE}: ${NODE_DOWNLOAD}`;
  // The odd release lines between the supported ones (23, 25) are short-lived; they may work, but are not supported.
  return major > 22 && major % 2 === 1
    ? warn(`Node.js ${version} is not a release line Netjes supports (${NODE_RANGE})`, fix)
    : fail(`Node.js ${version} is too old; Netjes needs ${NODE_RANGE}`, fix);
}

/**
 * @typedef {{ file: string; framework: string; major: number }} Target
 * @typedef {{ file: string; version: string }} GlobalJson
 */

/**
 * @param {{ code: number; stdout: string; stderr: string } | null} result `dotnet --version` in the app's folder
 * @param {Target | null} target
 * @param {GlobalJson | null} globalJson
 * @returns {Check}
 */
export function checkDotnet(result, target, globalJson) {
  const needs = Math.max(MIN_DOTNET, target?.major ?? 0);
  const install = `Install the .NET SDK, ${needs}.0 or newer: ${DOTNET_DOWNLOAD}`;
  if (!result || result.code === 127) return fail('.NET SDK not found', install);
  if (result.code !== 0) {
    const reason = `${result.stderr}\n${result.stdout}`.split('\n').map((line) => line.trim()).find(Boolean);
    return fail(
      `.NET SDK: ${reason ?? `dotnet --version exited with code ${result.code}`}`,
      globalJson
        ? `${globalJson.file} asks for SDK ${globalJson.version}: install that, or change the version there (\`dotnet --list-sdks\` shows what is installed)`
        : `Run \`dotnet --info\` to see what is wrong. ${install}`,
    );
  }
  const version = result.stdout.trim();
  const framework = target?.framework ?? `net${needs}.0`;
  if ((parseVersion(version)?.[0] ?? needs) < needs) {
    return fail(`.NET SDK ${version} cannot build ${framework} (${target?.file ?? 'Netjes'}); it needs ${needs}.0 or newer`, install);
  }
  const why = [target ? `${target.file} targets ${target.framework}` : '', globalJson ? `global.json asks for ${globalJson.version}` : '']
    .filter(Boolean)
    .join('; ');
  return ok(`.NET SDK ${version}${why ? ` (${why})` : ''}`);
}

/** @param {string} dir */
const isDirectory = (dir) => statSync(dir, { throwIfNoEntry: false })?.isDirectory() === true;

/**
 * The newest TargetFramework the project builds for.
 * @param {string} file
 * @param {string} text
 * @returns {Target | null}
 */
function targetOf(file, text) {
  const frameworks = /<TargetFrameworks?>([^<]+)<\/TargetFrameworks?>/i.exec(text)?.[1]?.split(';') ?? [];
  const targets = frameworks
    .map((framework) => ({ framework: framework.trim(), major: Number(/^net(\d+)\.\d+/.exec(framework.trim())?.[1]) }))
    .filter((target) => Number.isFinite(target.major))
    .sort((a, b) => b.major - a.major);
  return targets[0] ? { file, ...targets[0] } : null;
}

/**
 * The global.json that `dotnet` uses in `cwd`: the nearest one in it or above it, with the SDK version it asks for.
 * @param {string} cwd
 * @returns {Promise<GlobalJson | null>}
 */
async function globalJsonFor(cwd) {
  for (let dir = path.resolve(cwd); ; dir = path.dirname(dir)) {
    const file = path.join(dir, 'global.json');
    if (existsSync(file)) {
      const version = (await readJson(file))?.sdk?.version;
      return typeof version === 'string' ? { file, version } : null;
    }
    if (path.dirname(dir) === dir) return null;
  }
}

/**
 * Node.js and the .NET SDK.
 * @param {CliIo} io
 * @param {string} cwd
 * @param {Record<string, string | undefined>} env
 * @returns {Promise<Check[]>}
 */
export async function checkTools(io, cwd, env) {
  const csproj = isDirectory(cwd) ? (await readdir(cwd)).find((entry) => entry.endsWith('.csproj')) : undefined;
  const target = csproj ? targetOf(csproj, await readFile(path.join(cwd, csproj), 'utf8')) : null;
  const dotnet = io.capture ? await io.capture({ command: 'dotnet', args: ['--version'], cwd, env }) : null;
  return [checkNode(process.versions.node), checkDotnet(dotnet, target, await globalJsonFor(cwd))];
}

/**
 * The npm packages the app runs on, and whether they are versions this netjes supports.
 * @param {string} cwd
 * @param {string} cliVersion
 * @returns {Promise<{ checks: Check[]; netjes: string | undefined }>}
 */
async function checkPackages(cwd, cliVersion) {
  const found = await Promise.all(
    PACKAGES.map(async (name) => {
      const manifest = findPackage(cwd, name);
      const json = manifest ? await readJson(manifest) : null;
      return { name, installed: manifest !== null, version: typeof json?.version === 'string' ? json.version : '?', json };
    }),
  );
  const missing = found.filter((p) => !p.installed).map((p) => p.name);
  if (missing.length) {
    return { checks: [fail(`npm packages are not installed: ${missing.join(', ')}`, 'Run `npm install` in the app')], netjes: undefined };
  }

  /** @type {Check[]} */
  const checks = [ok(`npm packages: ${found.map((p) => `${p.name} ${p.version}`).join(', ')}`)];
  const netjes = found[0];
  /** @type {Record<string, string>} */
  const peers = netjes?.json?.peerDependencies ?? {};
  /** @type {Map<string, typeof found>} */
  const outside = new Map();
  for (const p of found.slice(1)) {
    const range = peers[p.name];
    if (range && !satisfies(p.version, range)) outside.set(range, [...(outside.get(range) ?? []), p]);
  }
  for (const [range, list] of outside) {
    const named = list.map((p) => `${p.name} ${p.version}`).join(' and ');
    checks.push(
      fail(
        `${named} ${list.length > 1 ? 'do' : 'does'} not match what netjes ${netjes?.version} needs (${range})`,
        `Run \`npm install ${list.map((p) => `"${p.name}@${range}"`).join(' ')}\``,
      ),
    );
  }
  if (netjes && netjes.version !== cliVersion) {
    checks.push(warn(`This is netjes ${cliVersion}; the app has netjes ${netjes.version}`, "Run the app's own with `npx netjes info`, or install one version"));
  }
  return { checks, netjes: netjes?.version };
}

/**
 * How the project file references Netjes, and whether that is the version of the npm package.
 * @param {string} cwd
 * @param {string} csproj
 * @param {string} text
 * @param {string | undefined} npmVersion
 * @param {string} cliVersion
 * @returns {Check}
 */
function checkReference(cwd, csproj, text, npmVersion, cliVersion) {
  const project = /<ProjectReference\s+[^>]*Include="([^"]*Netjes\.csproj)"/i.exec(text)?.[1];
  if (project) {
    const file = path.resolve(cwd, project.replaceAll('\\', '/'));
    return existsSync(file)
      ? ok(`${csproj} references the Netjes checkout at ${file}`)
      : fail(`${csproj} references Netjes at ${file}, which does not exist`, 'Point its ProjectReference (and the Netjes.targets import) at src/Netjes in a Netjes checkout');
  }
  const element = /<PackageReference\s+[^>]*Include="Netjes"[^>]*>/i.exec(text)?.[0];
  if (!element) {
    return warn(
      `${csproj} does not reference the Netjes package`,
      `Add <PackageReference Include="Netjes" Version="[${npmVersion ?? cliVersion}]" />, unless a Directory.Packages.props does`,
    );
  }
  const reference = /\sVersion="([^"]*)"/i.exec(element)?.[1];
  // Only an exact version, 1.2.3 or [1.2.3], is compared: a range, a floating version or an MSBuild property such as
  // $(NetjesVersion) is resolved by NuGet and MSBuild, which this does not repeat.
  const version = /^\[?(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)\]?$/.exec(reference?.trim() ?? '')?.[1];
  const shown = version ?? reference;
  if (!version || !npmVersion) return ok(`${csproj} references Netjes${shown ? ` ${shown}` : ''}`);
  return version === npmVersion
    ? ok(`${csproj} references Netjes ${version}, the version of the npm package`)
    : fail(
        `${csproj} references Netjes ${version}, but node_modules has netjes ${npmVersion}`,
        'Use one version on both sides (the C# and npm halves speak one protocol): change the PackageReference or package.json',
      );
}

/**
 * The newest .cs file of the app's own, relative to `cwd` with / separators.
 * @param {string} cwd
 * @returns {Promise<{ file: string; mtimeMs: number } | null>}
 */
async function newestSource(cwd) {
  /** @type {{ file: string; mtimeMs: number } | null} */
  let newest = null;
  /** @param {string} relative */
  async function walk(relative) {
    for (const entry of await readdir(path.join(cwd, relative), { withFileTypes: true })) {
      const file = relative ? `${relative}/${entry.name}` : entry.name;
      if (entry.isDirectory()) {
        if (!SKIPPED.has(entry.name) && !entry.name.startsWith('.')) await walk(file);
      } else if (entry.name.endsWith('.cs')) {
        const { mtimeMs } = await stat(path.join(cwd, file));
        if (!newest || mtimeMs > newest.mtimeMs) newest = { file, mtimeMs };
      }
    }
  }
  await walk('');
  return newest;
}

/** @param {string} cwd */
async function countPages(cwd) {
  const files = await readdir(path.join(cwd, 'app'), { recursive: true });
  return files.filter((file) => /^page\.(tsx|jsx|ts|js)$/.test(path.basename(file))).length;
}

/**
 * The project setup: what `netjes dev`, `build` and `generate` rely on.
 * @param {string} cwd
 * @param {string} cliVersion
 * @returns {Promise<Check[]>}
 */
export async function checkProject(cwd, cliVersion) {
  /** @type {Check[]} */
  const checks = [];
  const entries = isDirectory(cwd) ? await readdir(cwd) : [];

  const hasManifest = entries.includes('package.json');
  const packages = hasManifest ? await checkPackages(cwd, cliVersion) : { checks: [], netjes: undefined };
  checks.push(...packages.checks);

  const csproj = entries.find((e) => e.endsWith('.csproj'));
  checks.push(
    csproj
      ? checkReference(cwd, csproj, await readFile(path.join(cwd, csproj), 'utf8'), packages.netjes, cliVersion)
      : fail(`No .csproj in ${cwd}`, "Run netjes info in the app's folder or pass --cwd <dir>; `netjes new <dir>` creates an app"),
  );

  const viteConfig = entries.find((e) => /^vite\.config\.(ts|mts|js|mjs)$/.test(e));
  if (!viteConfig) {
    checks.push(fail('No vite.config.ts', "Add one: export default defineConfig({ plugins: [netjes()] }), with netjes from 'netjes/vite'"));
  } else if (!(await readFile(path.join(cwd, viteConfig), 'utf8')).includes('netjes(')) {
    checks.push(fail(`${viteConfig} does not use netjes() from 'netjes/vite'`, 'Add netjes() to its plugins'));
  } else checks.push(ok(`${viteConfig} uses netjes()`));

  if (hasManifest) {
    const manifest = await readJson(path.join(cwd, 'package.json'));
    if (!manifest) checks.push(fail('package.json is not valid JSON', 'Fix its syntax; npm and Vite read it'));
    else if (manifest.scripts?.build) checks.push(ok('package.json has a build script (dotnet publish runs it)'));
    else {
      checks.push(
        fail('package.json has no build script; dotnet publish runs `npm run build`', 'Add "build": "vite build && vite build --ssr" to its scripts'),
      );
    }
  } else checks.push(fail('No package.json', 'A Netjes app keeps its npm side next to the .csproj; `netjes new <dir>` shows the setup'));

  if (isDirectory(path.join(cwd, 'app'))) {
    const pages = await countPages(cwd);
    checks.push(pages ? ok(`app/ has ${pages} page${pages === 1 ? '' : 's'}`) : warn('app/ has no page files', 'Add app/page.tsx: it is the page at /'));
  } else checks.push(fail('No app/ directory', 'Put the pages in app/: app/page.tsx is /, app/about/page.tsx is /about'));

  // The app refuses to start for these too: Netjes routes app/ only.
  const { moves, apiRoutes } = isDirectory(path.join(cwd, 'pages'))
    ? await pagesRouterFiles(cwd, 'pages')
    : { moves: [], apiRoutes: [] };
  if (moves.length) {
    // Every special file is named, since each has its own App Router file; the pages after them are capped.
    const specials = moves.filter((move) => move.special).length;
    const named = listing(moves.map((move) => `${move.file} to ${move.target}`), ' the same way', specials);
    // Only _app and _document become a layout: what wrapped every page, and the document Netjes renders itself.
    const layout = moves.some((move) => path.basename(move.target, path.extname(move.target)) === 'layout')
      ? '. The root layout wraps every page; Netjes renders <html>, <head> and <body> around it'
      : '';
    checks.push(fail('pages/ has pages router files, but Netjes routes app/ only', `Move them to their App Router names: ${named}${layout}`));
  }
  if (apiRoutes.length) {
    checks.push(
      fail(
        `pages/api/ has API routes (${listing(apiRoutes)}), which Netjes does not serve`,
        'Map them as C# minimal API endpoints instead, with app.MapGet or app.MapPost',
      ),
    );
  }

  const types = path.join(cwd, 'generated', 'netjes.ts');
  if (!existsSync(types)) {
    checks.push(fail('generated/netjes.ts is missing', 'Run `netjes generate`; the app also writes it when it starts in Development'));
  } else {
    const newest = csproj ? await newestSource(cwd) : null;
    checks.push(
      newest && newest.mtimeMs > (await stat(types)).mtimeMs + SAME_TIME_MS
        ? warn(
            `generated/netjes.ts is older than ${newest.file}`,
            'Run `netjes generate` if the C# types changed; `netjes generate --check` tells for sure',
          )
        : ok('generated/netjes.ts is newer than the C# code'),
    );
  }
  return checks;
}

/**
 * What the pages router served below a directory, with paths relative to `cwd`: the .tsx, .jsx and .js pages, each with
 * its App Router file, and the .ts, .tsx, .js and .jsx API routes in api/. Not .ts files outside api/, which hold no JSX
 * and are helpers far more often than pages, nor co-located tests or declaration files, files in _ folders, or _ files
 * other than the special files at the root. The same rule as the app's startup check, and its order: the special files
 * first, then by path.
 * @param {string} cwd
 * @param {string} directory
 */
async function pagesRouterFiles(cwd, directory) {
  const files = await readdir(path.join(cwd, directory), { recursive: true, withFileTypes: true });
  /** @type {{ file: string; target: string; special: boolean }[]} */
  const moves = [];
  /** @type {string[]} */
  const apiRoutes = [];
  for (const f of files.filter((f) => f.isFile())) {
    const parts = path.relative(path.join(cwd, directory), path.join(f.parentPath, f.name)).split(path.sep);
    const file = [directory, ...parts].join('/');
    const folders = parts.slice(0, -1);
    const extension = path.extname(f.name);
    const name = path.basename(f.name, extension);
    const special = folders.length === 0 && SPECIAL_FILES.has(name);
    if (/\.(test|spec|d)$/.test(name) || (name.startsWith('_') && !special) || folders.some((folder) => folder.startsWith('_'))) {
      continue;
    }
    if (folders[0] === 'api') {
      if (/^\.(ts|tsx|js|jsx)$/i.test(extension)) apiRoutes.push(file);
    } else if (/^\.(tsx|jsx|js)$/i.test(extension)) {
      // about.tsx is about/page.tsx, an index file the page of its folder, a special file the root file in its place.
      const appName = special ? SPECIAL_FILES.get(name) : name === 'index' ? 'page' : `${name}/page`;
      // app/ routes .tsx and .jsx only, so a .js page becomes a .jsx file.
      const appExtension = /^\.js$/i.test(extension) ? `${extension}${extension.endsWith('S') ? 'X' : 'x'}` : extension;
      moves.push({ file, target: ['app', ...folders, `${appName}${appExtension}`].join('/'), special });
    }
  }
  /** @param {string} a @param {string} b */
  const ordinal = (a, b) => (a < b ? -1 : a > b ? 1 : 0);
  return {
    // The special files first: the rest are counted as moving the same way, and they do not.
    moves: moves.sort((a, b) => Number(b.special) - Number(a.special) || ordinal(a.file, b.file)),
    apiRoutes: apiRoutes.sort(ordinal),
  };
}

/**
 * "a, b, c, and 2 more": the first items, and a count of the rest.
 * @param {string[]} items
 * @param {string} [rest] after the count
 * @param {number} [named] how many items to name before the capped ones
 */
function listing(items, rest = '', named = 0) {
  const listed = named + LISTED;
  return items.slice(0, listed).join(', ') + (items.length > listed ? `, and ${items.length - listed} more${rest}` : '');
}

/**
 * One line per check, marked ✓, ! or ✗, each problem followed by its fix, indented.
 * @param {Check[]} checks
 * @param {boolean} color
 */
export function formatChecks(checks, color = false) {
  const { green, yellow, red } = paint(color);
  const marks = { ok: green('✓'), warn: yellow('!'), fail: red('✗') };
  return checks.map((check) => `${marks[check.status]} ${check.text}${check.fix ? `\n  ${check.fix}` : ''}`).join('\n') + '\n';
}
