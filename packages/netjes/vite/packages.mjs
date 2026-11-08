// @ts-check
// Page packages: npm packages that bring pages for the app to mount, marked by a `netjes` field in their package.json.
// The Vite plugin, the development sidecar (through the plugin's api) and the CLI read them from here.
import { readFileSync, realpathSync, statSync } from 'node:fs';
import { readdir } from 'node:fs/promises';
import path from 'node:path';
import { findPackage } from '../cli/packages.mjs';

/** The names of the convention files, which the route table routes; every other file in a pages folder is colocated code. */
export const CONVENTION_FILES = ['page', 'layout', 'template', 'loading', 'error', 'not-found', 'forbidden', 'unauthorized', 'global-error', 'default'];
/** The extensions of convention files. */
export const CONVENTION_EXTENSIONS = ['.tsx', '.jsx'];

/**
 * A page package as the app installed it.
 * @typedef {object} PagePackage
 * @property {string} name The npm name, which starts every file id of the package.
 * @property {string} version
 * @property {string} netjes Its `peerDependencies.netjes`: the Netjes versions it supports.
 * @property {string} root The real path of its folder, with `/` separators: a link (a workspace, `npm link`) resolved.
 * @property {string} pages The folder of its pages, relative to `root`.
 * @property {string[]} sources The folders Tailwind scans for its classes, relative to `root`.
 * @property {string | null} css A stylesheet the app's Tailwind build imports, relative to `root`.
 * @property {string | null} nuget The NuGet package of its C# half.
 * @property {string[]} dependencies The names in its `dependencies`.
 * @property {boolean} linked Whether `root` lies outside every node_modules folder: a workspace or `npm link` links it,
 *   and its files may change while the dev server runs.
 */

/** @param {string} file */
const slashes = (file) => file.replaceAll('\\', '/');

/** npm's rule for the name of a new package: lowercase, URL-safe, and at most one scope. */
const PACKAGE_NAME = /^(?:@[a-z0-9-~][a-z0-9-._~]*\/)?[a-z0-9-~][a-z0-9-._~]*$/;

/**
 * Whether a path from a package's `netjes` field is relative and stays inside the package: no `..` or `.`, no leading
 * `/`, no `\`, no drive letter, no empty folder. The file ids of the package are built from it, so it has one spelling.
 * @param {unknown} value
 * @returns {value is string}
 */
function isInsidePath(value) {
  return (
    typeof value === 'string' &&
    !value.includes('\\') &&
    !value.includes(':') &&
    value.split('/').every((segment) => segment !== '' && segment !== '.' && segment !== '..')
  );
}

/** @param {string} file */
function isDirectory(file) {
  try {
    return statSync(file).isDirectory();
  } catch {
    return false;
  }
}

/** @param {string} file */
function isFile(file) {
  try {
    return statSync(file).isFile();
  } catch {
    return false;
  }
}

/**
 * Reads and checks the page packages the app lists, in the order it lists them. Each error is one line that names the
 * package and what to do.
 * @param {string} root The app's folder, from which the packages resolve.
 * @param {readonly string[]} names
 * @param {string} appDir The app's folder of pages, whose file ids a package's must not look like.
 * @returns {PagePackage[]}
 */
export function resolvePagePackages(root, names, appDir) {
  /** @type {PagePackage[]} */
  const packages = [];
  const seen = new Set();
  for (const name of names) {
    if (typeof name !== 'string' || !PACKAGE_NAME.test(name)) {
      throw new Error(`netjes: ${JSON.stringify(name)} in netjes({ packages }) is not the name of an npm package`);
    }
    if (seen.has(name)) throw new Error(`netjes: '${name}' is listed twice in netjes({ packages })`);
    seen.add(name);
    // A package's file ids start with its name, and the app's with its folder of pages (`app`, `src/app`).
    if (name === appDir || appDir.startsWith(`${name}/`)) {
      throw new Error(`netjes: '${name}' in netjes({ packages }) is named like the app's folder of pages, ${appDir}, so its pages would be taken for the app's`);
    }
    packages.push(readPagePackage(root, name));
  }
  return packages;
}

/**
 * @param {string} root
 * @param {string} name
 * @returns {PagePackage}
 */
function readPagePackage(root, name) {
  const manifestPath = findPackage(root, name);
  if (!manifestPath) throw new Error(`netjes: '${name}' in netjes({ packages }) is not installed: npm install ${name}`);
  const manifest = /** @type {Record<string, unknown>} */ (JSON.parse(readFileSync(manifestPath, 'utf8')));
  const field = manifest.netjes;
  if (typeof field !== 'object' || field === null || Array.isArray(field)) {
    throw new Error(`netjes: '${name}' in netjes({ packages }) is not a page package: its package.json has no "netjes" field`);
  }
  const options = /** @type {Record<string, unknown>} */ (field);
  const peers = manifest.peerDependencies;
  const range = typeof peers === 'object' && peers !== null ? /** @type {Record<string, unknown>} */ (peers).netjes : undefined;
  if (typeof range !== 'string' || range.trim() === '') {
    throw new Error(`netjes: '${name}' has no peerDependencies.netjes in its package.json: a page package names the Netjes versions it supports there`);
  }
  const dir = slashes(realpathSync.native(path.dirname(manifestPath)));

  /**
   * @param {string} key
   * @param {unknown} value
   */
  const inside = (key, value) => {
    if (!isInsidePath(value)) {
      throw new Error(
        `netjes: '${name}' has "${key}": ${JSON.stringify(value)} in the "netjes" field of its package.json: ` +
          'a path there is relative and stays inside the package (no "..", no leading "/", no "\\")',
      );
    }
    return value;
  };

  const pages = inside('pages', options.pages ?? 'app');
  /** @type {string[]} */
  let sources;
  if (options.sources === undefined) {
    sources = isDirectory(path.join(dir, 'src')) && pages !== 'src' ? [pages, 'src'] : [pages];
  } else {
    if (!Array.isArray(options.sources)) {
      throw new Error(`netjes: '${name}' has "sources": ${JSON.stringify(options.sources)} in the "netjes" field of its package.json: it is a list of folders`);
    }
    sources = options.sources.map((source) => inside('sources', source));
    for (const source of sources) {
      if (!isDirectory(path.join(dir, source))) {
        throw new Error(`netjes: '${name}' lists "${source}" in "sources" in the "netjes" field of its package.json, which is no folder in the package`);
      }
    }
  }
  const css = options.css === undefined ? null : inside('css', options.css);
  if (css !== null && !isFile(path.join(dir, css))) {
    throw new Error(`netjes: '${name}' has "css": "${css}" in the "netjes" field of its package.json, which is no file in the package`);
  }
  const nuget = typeof options.nuget === 'string' ? options.nuget : null;
  const dependencies = typeof manifest.dependencies === 'object' && manifest.dependencies !== null ? Object.keys(manifest.dependencies) : [];
  return {
    name,
    version: typeof manifest.version === 'string' ? manifest.version : '',
    netjes: range,
    root: dir,
    pages,
    sources,
    css,
    nuget,
    dependencies,
    linked: !dir.split('/').includes('node_modules'),
  };
}

/**
 * The package's dependencies that are CommonJS, as Vite has to pre-bundle them for the browser: neither `"type":
 * "module"`, a `module` entry nor an `import` condition in its `exports`. A dependency that is not installed for the
 * package, and netjes, which Vite never pre-bundles, are left out.
 * @param {PagePackage} pkg
 * @returns {string[]}
 */
export function commonJsDependencies(pkg) {
  return pkg.dependencies.filter((dependency) => {
    if (dependency === 'netjes') return false;
    const manifestPath = findPackage(pkg.root, dependency);
    if (!manifestPath) return false;
    const manifest = /** @type {Record<string, unknown>} */ (JSON.parse(readFileSync(manifestPath, 'utf8')));
    if (manifest.type === 'module' || typeof manifest.module === 'string') return false;
    return !/"import"\s*:|\.mjs"/.test(JSON.stringify(manifest.exports ?? null));
  });
}

/**
 * A package's convention files, relative to its folder of pages, in ordinal order. Like the app's glob, this leaves out
 * node_modules and hidden folders.
 * @param {PagePackage} pkg
 * @returns {Promise<string[]>}
 */
export async function pagePackageFiles(pkg) {
  const dir = path.join(pkg.root, pkg.pages);
  /** @type {import('node:fs').Dirent[]} */
  let entries;
  try {
    entries = await readdir(dir, { recursive: true, withFileTypes: true });
  } catch {
    return [];
  }
  const files = entries
    .filter((entry) => entry.isFile() && isConventionFile(entry.name))
    .map((entry) => slashes(path.relative(dir, path.join(entry.parentPath, entry.name))))
    .filter((file) => file.split('/').slice(0, -1).every((folder) => folder !== 'node_modules' && !folder.startsWith('.')));
  // UTF-16 code unit order, as the route table's ordinal order.
  return files.sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
}

/**
 * Whether a file is a convention file, by its name.
 * @param {string} name A file's name, with its extension.
 */
export function isConventionFile(name) {
  const { name: base, ext } = path.parse(name);
  return CONVENTION_FILES.includes(base) && CONVENTION_EXTENSIONS.includes(ext);
}

/**
 * The id of a package's file: its npm name, its folder of pages and the file's path in that folder, as the payload,
 * the route table and errors name it, wherever the package is installed.
 * @param {PagePackage} pkg
 * @param {string} file Relative to the package's folder of pages.
 */
export function fileId(pkg, file) {
  return `${pkg.name}/${pkg.pages}/${file}`;
}

/**
 * The real path of a package's file, with `/` separators.
 * @param {PagePackage} pkg
 * @param {string} file Relative to the package's folder of pages.
 */
export function filePath(pkg, file) {
  return `${pkg.root}/${pkg.pages}/${file}`;
}

/** The app's import of Tailwind CSS, in either quotes or as a url(), with Tailwind's import options (`source(none)`). */
const TAILWIND_IMPORT = /@import\s+(?:url\(\s*)?(["'])tailwindcss\1\s*\)?[^;]*;?/;
/** The file each `@import` of a stylesheet names. */
const IMPORTS = /@import\s+(?:url\(\s*)?(["'])(.*?)\1/g;

/**
 * A path from a stylesheet's folder, as Tailwind CSS resolves `@import` and `@source`: relative, with `/` separators,
 * starting with `./` or `../`, so that no drive letter is in it on Windows, and nothing reads it as a package name.
 * @param {string} dir
 * @param {string} target
 */
function fromStylesheet(dir, target) {
  const relative = slashes(path.relative(dir, target));
  return relative.startsWith('../') || path.isAbsolute(relative) ? relative : `./${relative}`;
}

/**
 * Whether a stylesheet imports a package's own stylesheet already: by the package's name and the path in its `css`,
 * or by a relative path to the file.
 * @param {string} css
 * @param {string} dir The stylesheet's folder.
 * @param {PagePackage} pkg
 */
function importsPackageCss(css, dir, pkg) {
  if (pkg.css === null) return false;
  const own = `${pkg.root}/${pkg.css}`;
  for (const [, , file = ''] of css.matchAll(IMPORTS)) {
    if (file === `${pkg.name}/${pkg.css}`) return true;
    if (!file.startsWith('./') && !file.startsWith('../')) continue;
    try {
      if (slashes(realpathSync.native(path.resolve(dir, file))) === own) return true;
    } catch {
      // A file that is not there is not the package's.
    }
  }
  return false;
}

/**
 * A stylesheet of the app with the packages added to its import of Tailwind CSS: an `@import` of each package's own
 * stylesheet, unless it imports that already, and an `@source` for each of its source folders, which Tailwind would not
 * scan otherwise (its automatic detection leaves out node_modules and every folder outside the app). They go on the
 * line of the import, so the lines below keep their numbers. A stylesheet without that import, or one in a package, is
 * returned as it is.
 * @param {string} css
 * @param {string} file The stylesheet's path, with `/` separators.
 * @param {readonly PagePackage[]} packages
 */
export function withPackageStyles(css, file, packages) {
  if (packages.some((pkg) => file.startsWith(`${pkg.root}/`))) return css;
  const match = TAILWIND_IMPORT.exec(css);
  if (!match) return css;
  const dir = path.dirname(file);
  const added = packages.flatMap((pkg) => [
    ...(pkg.css !== null && !importsPackageCss(css, dir, pkg) ? [`@import ${JSON.stringify(fromStylesheet(dir, `${pkg.root}/${pkg.css}`))};`] : []),
    ...pkg.sources.map((source) => `@source ${JSON.stringify(fromStylesheet(dir, `${pkg.root}/${source}`))};`),
  ]);
  const end = match.index + match[0].length;
  const statement = match[0].endsWith(';') ? match[0] : `${match[0]};`;
  return `${css.slice(0, match.index)}${statement} ${added.join(' ')}${css.slice(end)}`;
}
