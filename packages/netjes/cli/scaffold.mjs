// @ts-check
import { existsSync } from 'node:fs';
import { mkdir, readFile, readdir, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const PACKAGE_ROOT = fileURLToPath(new URL('..', import.meta.url));
const TEMPLATE = path.join(PACKAGE_ROOT, 'template');
/** Skills for coding agents, copied to `.claude/skills/` in an app as they are. */
const SKILLS = path.join(PACKAGE_ROOT, 'skills');
/**
 * Template files stored under another name: ones npm would drop or rename when publishing, and the app's guides for
 * coding agents, which agents working on Netjes would otherwise apply to the template.
 */
const RENAMED = new Map([
  ['_gitignore', '.gitignore'],
  ['_gitattributes', '.gitattributes'],
  ['_AGENTS.md', 'AGENTS.md'],
  ['_CLAUDE.md', 'CLAUDE.md'],
]);

/**
 * The ASCII words in a name, accents dropped (Café gives Cafe).
 * @param {string} value
 */
function words(value) {
  return value
    .normalize('NFD')
    .replace(/\p{M}/gu, '')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .split(/[^A-Za-z0-9]+/)
    .filter(Boolean);
}

/**
 * Names for a project, from its directory or `--name`: a C# identifier, an npm package name, a title.
 * @param {string} name
 */
export function projectNames(name) {
  const parts = words(name);
  const pascal = parts.map((w) => w[0]?.toUpperCase() + w.slice(1)).join('') || 'App';
  return {
    dotnet: /^[0-9]/.test(pascal) ? `App${pascal}` : pascal,
    npm: parts.map((w) => w.toLowerCase()).join('-') || 'app',
    title: parts.map((w) => w[0]?.toUpperCase() + w.slice(1)).join(' ') || 'App',
  };
}

/** A name `--name` accepts as it is: what the template can use as a file name, a namespace and an assembly name. */
const DOTNET_NAME = /^[A-Za-z][A-Za-z0-9]*$/;

/** The C# reserved keywords, which cannot name a namespace (CS1001). */
const CSHARP_KEYWORDS = new Set(
  ('abstract as base bool break byte case catch char checked class const continue decimal default delegate do double ' +
    'else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal ' +
    'is lock long namespace new null object operator out override params private protected public readonly ref return ' +
    'sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked ' +
    'unsafe ushort using virtual void volatile while').split(' '),
);

// Types the template's C# uses by their simple names. A root namespace of the same name hides the type from that code,
// and the build fails (CS0118, CS0234); Program is the class the top-level statements in Program.cs make (CS0101).
const TEMPLATE_TYPES = new Set([
  'Program',
  'SiteProps',
  'Page',
  'ServerAction',
  'WebApplication',
  'ValueTask',
  'DateTimeOffset',
  'TimeProvider',
  'IEndpointRouteBuilder',
  'FromFormAttribute',
]);

/**
 * The names for a new app in `target`, or a message saying why there are none: `--name` as given when it is a .NET
 * project name, else derived from the folder name; neither may clash with the template's own type names or with a
 * package the app depends on (npm refuses to install a package into one of the same name).
 * @param {string} target
 * @param {string | undefined} name
 * @returns {Promise<{ names: ReturnType<typeof projectNames> } | { problem: string }>}
 */
export async function namesFor(target, name) {
  if (name !== undefined && !DOTNET_NAME.test(name)) {
    return {
      problem: `--name '${name}' is not a valid .NET project name; use letters and digits, starting with a letter, e.g. --name ${projectNames(name).dotnet}`,
    };
  }
  if (name !== undefined && CSHARP_KEYWORDS.has(name)) {
    return { problem: `--name '${name}' is a C# keyword; choose another name, e.g. --name ${projectNames(name).dotnet}` };
  }
  if (name === undefined && words(path.basename(target)).length === 0) {
    return { problem: `cannot make a project name from the folder name '${path.basename(target)}'; pass --name <Name>` };
  }
  const names = name === undefined ? projectNames(path.basename(target)) : { ...projectNames(name), dotnet: name };
  if (TEMPLATE_TYPES.has(names.dotnet)) {
    return { problem: `'${names.dotnet}' is the name of a type the app's code uses; choose another name with --name` };
  }
  const manifest = JSON.parse(await readFile(path.join(TEMPLATE, 'package.json'), 'utf8'));
  if (Object.hasOwn({ ...manifest.dependencies, ...manifest.devDependencies }, names.npm)) {
    return { problem: `the app's npm name would be '${names.npm}', a package it depends on; choose another folder name or --name` };
  }
  return { names };
}

/** The repository root when this package runs from a Netjes checkout, else null. */
export function checkoutRoot() {
  const root = path.resolve(PACKAGE_ROOT, '..', '..');
  return existsSync(path.join(root, 'src', 'Netjes', 'Netjes.csproj')) ? root : null;
}

/** @returns {Promise<string>} */
export async function packageVersion() {
  const manifest = JSON.parse(await readFile(path.join(PACKAGE_ROOT, 'package.json'), 'utf8'));
  return String(manifest.version);
}

/**
 * Fills the name placeholders of a template file: `__NAME__` (the C# name), `__name__` (the npm name), `__TITLE__`.
 * @param {string} text
 * @param {ReturnType<typeof projectNames>} names
 */
export function fillNames(text, names) {
  return text.replaceAll('__NAME__', names.dotnet).replaceAll('__name__', names.npm).replaceAll('__TITLE__', names.title);
}

/** @param {string} file a path in the template @returns {Promise<string>} */
export const readTemplate = (file) => readFile(path.join(TEMPLATE, file), 'utf8');

/** @param {string} from @param {string} to */
const toPosix = (from, to) => path.relative(from, to).split(path.sep).join('/');

/**
 * Copies the template into `target`, filling in the names and how Netjes is referenced: the local checkout
 * (ProjectReference + a `file:` dependency) or the published packages.
 * @param {string} target
 * @param {{ name?: string; packages?: boolean }} options
 */
export async function scaffold(target, options = {}) {
  const found = await namesFor(target, options.name);
  if ('problem' in found) throw new Error(found.problem);
  const { names } = found;
  const existing = await stat(target).catch(() => null);
  if (existing && !existing.isDirectory()) throw new Error(`${target} is a file, not a folder`);
  if (existing && (await readdir(target)).length > 0) throw new Error(`${target} already exists and is not empty; choose a new folder`);

  const version = await packageVersion();
  const root = options.packages ? null : checkoutRoot();
  const references = root
    ? `  <Import Project="${toPosix(target, root)}/src/Netjes/build/Netjes.targets" />\n\n` +
      `  <ItemGroup>\n    <ProjectReference Include="${toPosix(target, root)}/src/Netjes/Netjes.csproj" />\n  </ItemGroup>\n`
    : // Exact versions: the C# and npm halves speak one payload protocol, and a bare NuGet version is a minimum.
      `  <ItemGroup>\n    <PackageReference Include="Netjes" Version="[${version}]" />\n  </ItemGroup>\n`;
  const dependency = root ? `file:${toPosix(target, path.join(root, 'packages', 'netjes'))}` : version;

  /** @param {string} text */
  const fill = (text) =>
    fillNames(text.replaceAll('<!--NETJES_REFERENCES-->\n', references).replaceAll('__NETJES_DEPENDENCY__', dependency), names);

  /** @param {string} from @param {string} to */
  async function copy(from, to) {
    await mkdir(to, { recursive: true });
    for (const entry of await readdir(from)) {
      const source = path.join(from, entry);
      const destination = path.join(to, fill(RENAMED.get(entry) ?? entry));
      if ((await stat(source)).isDirectory()) await copy(source, destination);
      else await writeFile(destination, fill(await readFile(source, 'utf8')));
    }
  }

  await copy(TEMPLATE, target);
  for (const skill of await skillFiles()) {
    await mkdir(path.dirname(path.join(target, skill.path)), { recursive: true });
    await writeFile(path.join(target, skill.path), skill.text);
  }
  return { names, mode: root ? 'checkout' : 'packages' };
}

/**
 * The skill pack's files, by their path in an app (`.claude/skills/netjes/SKILL.md`, posix).
 * @returns {Promise<{ path: string; text: string }[]>}
 */
export async function skillFiles() {
  const entries = await readdir(SKILLS, { recursive: true, withFileTypes: true });
  const files = entries.filter((entry) => entry.isFile()).map((entry) => path.join(entry.parentPath, entry.name));
  return Promise.all(
    files.sort().map(async (file) => ({
      path: ['.claude', 'skills', ...path.relative(SKILLS, file).split(path.sep)].join('/'),
      text: await readFile(file, 'utf8'),
    })),
  );
}
