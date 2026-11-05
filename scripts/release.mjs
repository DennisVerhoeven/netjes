// @ts-check
// The checks .github/workflows/release.yml runs before it publishes, and the change it makes to the package READMEs
// before it packs them, also runnable by hand from the repository root. `node scripts/release.mjs` with no arguments
// prints the usage. Tests: node --test scripts/test/*.test.mjs
import { realpathSync } from 'node:fs';
import { readdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';

// Semantic versioning 2.0.0 without build metadata, which npm drops and NuGet ignores.
const SEMVER =
  /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?$/;

/**
 * The version a release tag names: `v0.0.1` is 0.0.1, `v0.1.0-rc.1` is a prerelease.
 * @param {string} tag
 * @returns {{ version: string, prerelease: boolean }}
 */
export function parseTag(tag) {
  const match = /^v(.*)$/.exec(tag);
  const semver = match ? SEMVER.exec(match[1]) : null;
  if (!match || !semver) throw new Error(`Tag "${tag}" is not v<major>.<minor>.<patch>[-<prerelease>].`);
  return { version: match[1], prerelease: semver[4] !== undefined };
}

/**
 * The texts in the docs that pin the release's version, each with `{version}` where the version stands. This is the
 * list RELEASING.md refers to: a release changes each of them, and `check` reports one that carries another version
 * or that is gone (when a doc is reworded, change its text here).
 * @type {{ file: string, text: string }[]}
 */
export const VERSION_PINS = [
  { file: 'README.md', text: 'Netjes is at {version}' },
  { file: 'docs/getting-started.md', text: 'using the Netjes {version} packages' },
  { file: 'docs/getting-started.md', text: 'dotnet add package Netjes --version {version}' },
  { file: 'docs/getting-started.md', text: '(`netjes@{version}` with `Netjes` {version})' },
  { file: 'docs/getting-started.md', text: 'npm install netjes@{version} react' },
  { file: 'docs/reference/cli.md', text: 'netjes {version} in /home' },
  { file: 'docs/reference/cli.md', text: 'netjes {version}, vite' },
  { file: 'docs/reference/cli.md', text: 'references Netjes {version}, the version' },
  { file: 'packages/netjes/README.md', text: 'npm install netjes@{version} react' },
  { file: 'src/Netjes/README.md', text: '({version} here;' },
  { file: 'src/Netjes/README.md', text: 'dotnet add package Netjes --version {version}' },
  { file: 'src/Netjes/README.md', text: 'npm install netjes@{version} react' },
  { file: 'src/Netjes.Images.SkiaSharp/README.md', text: 'dotnet add package Netjes.Images.SkiaSharp --version {version}' },
  { file: 'src/Netjes.Images.SkiaSharp/README.md', text: '({version} above)' },
];

/**
 * What is wrong with the pinned versions in the docs: a pinned text (see VERSION_PINS) that carries a version other
 * than `version`, or that is not in its file.
 * @param {string} root
 * @param {string} version
 * @returns {Promise<string[]>}
 */
async function checkPins(root, version) {
  const semver = String.raw`(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)`;
  /** @type {Map<string, string | null>} */
  const files = new Map();
  const errors = [];
  for (const { file, text } of VERSION_PINS) {
    if (!files.has(file)) files.set(file, await readFile(path.join(root, file), 'utf8').catch(() => null));
    const content = files.get(file);
    const label = text.replaceAll('{version}', '<version>');
    if (content === null || content === undefined) {
      errors.push(`${file} has no "${label}": the file is missing.`);
      continue;
    }
    const pattern = new RegExp(
      text
        .split('{version}')
        .map((part) => part.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))
        .join(semver),
      'g',
    );
    const found = [...content.matchAll(pattern)];
    if (found.length === 0) errors.push(`${file} has no "${label}".`);
    for (const match of found) {
      const wrong = match.slice(1).filter((pinned) => pinned !== version);
      if (wrong.length > 0) errors.push(`${file} pins ${wrong.join(' and ')} in "${match[0]}", not ${version}.`);
    }
  }
  return errors;
}

/** The repository the packages are released from, as owner/name. */
const REPOSITORY = 'DennisVerhoeven/netjes';

/**
 * The READMEs the packages carry, which npm and NuGet show on each version's page. Neither resolves a relative link,
 * so these link this repository's files with absolute URLs on `main`, and the release moves those links to its tag
 * just before it packs them (`links`).
 */
const PACKAGE_READMES = ['packages/netjes/README.md', 'src/Netjes/README.md', 'src/Netjes.Images.SkiaSharp/README.md'];

/**
 * A link to a file or folder of a GitHub repository at a ref: `https://github.com/<owner>/<name>/blob/<ref>/<path>`,
 * `tree/<ref>/` for a folder, or `https://raw.githubusercontent.com/<owner>/<name>/<ref>/<path>`. The groups are the
 * URL up to the ref, the repository (in the github.com or the raw form), the ref, and the rest of the URL, which ends
 * where a link in Markdown ends: at a space, a bracket, a quote or a backtick (\x60). The ref ends at a slash, ? or #,
 * or with the URL, before the punctuation that can follow a bare URL at the end of a sentence or of emphasis
 * (`…/tree/main.`, `**…/tree/main**`), which GitHub leaves out of the link too. A ref that runs into `<`, as in
 * `blob/v<version>/`, is a placeholder in text about such URLs, not a link.
 */
const REF_LINK =
  /(https?:\/\/(?:(?:www\.)?github\.com\/([\w.-]+\/[\w.-]+)\/(?:blob|tree)|raw\.githubusercontent\.com\/([\w.-]+\/[\w.-]+))\/)([^\s/?#<>()[\]"'\x60]+?)(?=[/?#]|[.,:;!?*_~]*(?:[\s>()[\]"'\x60]|$))([^\s<>()[\]"'\x60]*)/gi;

/**
 * Whether a REF_LINK match is to this repository. The owner and the name match in any case, as on GitHub.
 * @param {string | undefined} blobRepository
 * @param {string | undefined} rawRepository
 */
const isThisRepository = (blobRepository, rawRepository) =>
  (blobRepository ?? rawRepository ?? '').toLowerCase() === REPOSITORY.toLowerCase();

/**
 * The links to this repository's files and folders in `text`, with the ref each one names, as written.
 * @param {string} text
 * @returns {{ url: string, ref: string }[]}
 */
function repositoryLinks(text) {
  return [...text.matchAll(REF_LINK)]
    .filter((match) => isThisRepository(match[2], match[3]))
    .map((match) => ({ url: match[0].replace(/[.,:;!?*_~]+$/, ''), ref: match[4] ?? '' }));
}

/**
 * The package READMEs (PACKAGE_READMES) at `root`, each with its text, or null when it is missing.
 * @param {string} root
 * @returns {Promise<{ file: string, text: string | null }[]>}
 */
function readReadmes(root) {
  return Promise.all(
    PACKAGE_READMES.map(async (file) => ({ file, text: await readFile(path.join(root, file), 'utf8').catch(() => null) })),
  );
}

/**
 * What is wrong with the links in the package READMEs: each link to this repository must name `main`, which the
 * release moves to its tag.
 * @param {{ file: string, text: string | null }[]} readmes
 * @returns {string[]}
 */
function linksOffMain(readmes) {
  return readmes.flatMap(({ file, text }) =>
    repositoryLinks(text ?? '')
      .filter(({ ref }) => ref !== 'main')
      .map(({ url, ref }) => `${file} links ${ref} in ${url}: link main, which the release moves to its tag.`),
  );
}

/**
 * What is wrong with the links in the package READMEs at `root` (see linksOffMain). A README that is missing is left
 * to the pinned-version check, which names it.
 * @param {string} root
 * @returns {Promise<string[]>}
 */
async function checkReadmeLinks(root) {
  return linksOffMain(await readReadmes(root));
}

/**
 * Moves every link to this repository in the package READMEs at `root` from `main` to the tag of `version`, in
 * place: what the release does just before it packs them. Nothing is written when `version` is not a version, or
 * when a README is missing or links another ref. The files are read back afterwards, and a link that names anything
 * but the tag then is an error too.
 * @param {string} root
 * @param {string} version
 * @returns {Promise<{ errors: string[], moved: { file: string, count: number }[] }>}
 */
async function moveReadmeLinks(root, version) {
  if (!SEMVER.test(version)) {
    return { errors: [`"${version}" is not a version (<major>.<minor>.<patch>[-<prerelease>], without the tag's v).`], moved: [] };
  }
  const tag = `v${version}`;
  const readmes = await readReadmes(root);
  const errors = [...readmes.filter(({ text }) => text === null).map(({ file }) => `${file} is missing.`), ...linksOffMain(readmes)];
  if (errors.length > 0) return { errors, moved: [] };

  const moved = [];
  for (const { file, text } of readmes) {
    let count = 0;
    const linked = (text ?? '').replace(REF_LINK, (url, start, blobRepository, rawRepository, ref, rest) => {
      if (!isThisRepository(blobRepository, rawRepository) || ref !== 'main') return url;
      count++;
      return `${start}${tag}${rest}`;
    });
    if (linked !== text) await writeFile(path.join(root, file), linked);
    moved.push({ file, count });
  }

  for (const { file, text } of await readReadmes(root)) {
    for (const { url, ref } of repositoryLinks(text ?? '')) {
      if (ref !== tag) errors.push(`${file} still links ${ref} in ${url}, not ${tag}.`);
    }
  }
  return { errors, moved };
}

/**
 * What is wrong with the repository at `root` for releasing `version`: the npm package, its lockfile entry and the
 * NuGet packages' Directory.Build.props must all carry that version, the docs' pinned versions (VERSION_PINS) must
 * match it, the package READMEs must link this repository on `main` only (checkReadmeLinks), CHANGELOG.md needs an
 * entry for it, and with `repository` (owner/name, as in GITHUB_REPOSITORY) the npm package must name that GitHub
 * repository. An empty list means ready.
 * @param {string} root
 * @param {string} version
 * @param {{ repository?: string }} [options]
 * @returns {Promise<string[]>}
 */
export async function checkSources(root, version, options = {}) {
  const manifest = JSON.parse(await readFile(path.join(root, 'packages/netjes/package.json'), 'utf8'));
  const lock = JSON.parse(await readFile(path.join(root, 'package-lock.json'), 'utf8'));
  const props = await readFile(path.join(root, 'Directory.Build.props'), 'utf8');
  /** @type {[string, unknown][]} */
  const sources = [
    ['packages/netjes/package.json', manifest.version],
    ['package-lock.json (packages/netjes)', lock.packages?.['packages/netjes']?.version],
    ['Directory.Build.props', /<Version>\s*([^<]*?)\s*<\/Version>/.exec(props)?.[1]],
  ];
  const errors = [];
  for (const [source, found] of sources) {
    if (found !== version) errors.push(`${source} has version ${found ?? '(none)'}, not ${version}.`);
  }

  errors.push(...(await checkPins(root, version)));
  errors.push(...(await checkReadmeLinks(root)));

  const changelog = await readFile(path.join(root, 'CHANGELOG.md'), 'utf8').catch(() => '');
  if (releaseNotes(changelog, version) === null) errors.push(`CHANGELOG.md has no entry for ${version}.`);

  if (options.repository !== undefined) {
    const named = typeof manifest.repository === 'string' ? manifest.repository : manifest.repository?.url;
    const expected = `https://github.com/${options.repository}`;
    if (typeof named !== 'string' || githubUrl(named)?.toLowerCase() !== expected.toLowerCase()) {
      errors.push(`packages/netjes/package.json names repository ${named ?? '(none)'}, not ${expected}.`);
    }
  }
  return errors;
}

/**
 * A package.json repository as https://github.com/owner/name, or null when it is not on GitHub.
 * @param {string} url
 */
function githubUrl(url) {
  const match =
    /^(?:git\+)?(?:https?|git|ssh):\/\/(?:git@)?github\.com\/([^/]+)\/([^/]+?)(?:\.git)?\/?$/.exec(url) ??
    /^(?:github:|git@github\.com:)?([\w.-]+)\/([\w.-]+?)(?:\.git)?$/.exec(url);
  return match ? `https://github.com/${match[1]}/${match[2]}` : null;
}

/**
 * The CHANGELOG.md entry for `version` (Keep a Changelog: `## [0.0.1] - 2026-10-29`), without its heading and without
 * the link definitions for version headings; null when there is no entry or it is empty.
 * @param {string} changelog
 * @param {string} version
 * @returns {string | null}
 */
export function releaseNotes(changelog, version) {
  const lines = changelog.split(/\r?\n/);
  const escaped = version.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const heading = new RegExp(`^##\\s+(?:\\[${escaped}\\]|${escaped})(?:\\s+-\\s+.*)?\\s*$`);
  const start = lines.findIndex((line) => heading.test(line));
  if (start === -1) return null;
  const next = lines.findIndex((line, index) => index > start && /^##\s/.test(line));
  const versionLink = /^\[(?:unreleased|v?\d+\.\d+\.\d+[^\]]*)\]:\s/i;
  const body = lines
    .slice(start + 1, next === -1 ? undefined : next)
    .filter((line) => !versionLink.test(line))
    .join('\n')
    .trim();
  return body === '' ? null : body;
}

/**
 * What is wrong with the packed packages in `dir`: it must hold the npm package and both NuGet packages of `version`,
 * each with its symbol package (`.snupkg`, which `dotnet nuget push` sends along with the `.nupkg` beside it), and
 * nothing else. Both `npm pack` and `dotnet pack` name the file after the version inside the package.
 * @param {string} dir
 * @param {string} version
 * @returns {Promise<string[]>}
 */
export async function checkPackages(dir, version) {
  const expected = [
    `netjes-${version}.tgz`,
    `Netjes.${version}.nupkg`,
    `Netjes.${version}.snupkg`,
    `Netjes.Images.SkiaSharp.${version}.nupkg`,
    `Netjes.Images.SkiaSharp.${version}.snupkg`,
  ];
  const found = (await readdir(dir)).sort();
  return [
    ...expected.filter((name) => !found.includes(name)).map((name) => `Missing ${name}.`),
    ...found.filter((name) => !expected.includes(name)).map((name) => `Unexpected ${name}.`),
  ];
}

const USAGE = `Usage:
  node scripts/release.mjs check <tag> [--repository <owner/name>] [--root <dir>]
      Checks that every package source and every version the docs pin is the tag's version, that the package
      READMEs link this repository on main only, that CHANGELOG.md has its entry and, with
      --repository, that packages/netjes/package.json names that GitHub repository. Then prints version=<version>
      and prerelease=<true|false> (for GITHUB_OUTPUT).
  node scripts/release.mjs links <version> [--root <dir>]
      Moves every link to this repository in the package READMEs from main to the tag v<version>, in place, as the
      release does just before it packs them. Writes nothing when a README links another ref.
  node scripts/release.mjs packages <version> <dir>
      Checks that <dir> holds exactly the npm and NuGet packages of <version>, with the NuGet symbol packages.
  node scripts/release.mjs notes <version> [--root <dir>]
      Prints the CHANGELOG.md entry for <version>.
`;

/**
 * @param {string[]} argv
 * @returns {Promise<number>}
 */
async function main(argv) {
  const { positionals, values } = parseArgs({
    args: argv,
    allowPositionals: true,
    options: { root: { type: 'string' }, repository: { type: 'string' } },
  });
  const root = values.root ?? fileURLToPath(new URL('..', import.meta.url));
  const [command, ...rest] = positionals;
  /** @param {string[]} errors */
  const fail = (errors) => {
    process.stderr.write(errors.map((error) => `error: ${error}\n`).join(''));
    return 1;
  };

  if (command === 'check' && rest.length === 1) {
    const { version, prerelease } = parseTag(rest[0]);
    const errors = await checkSources(root, version, { repository: values.repository });
    if (errors.length > 0) return fail(errors);
    process.stdout.write(`version=${version}\nprerelease=${prerelease}\n`);
    return 0;
  }
  if (command === 'links' && rest.length === 1) {
    const { errors, moved } = await moveReadmeLinks(root, rest[0]);
    if (errors.length > 0) return fail(errors);
    process.stdout.write(
      moved.map(({ file, count }) => `${file}: ${count} ${count === 1 ? 'link' : 'links'} moved to v${rest[0]}\n`).join(''),
    );
    return 0;
  }
  if (command === 'packages' && rest.length === 2) {
    const errors = await checkPackages(rest[1], rest[0]);
    if (errors.length > 0) return fail(errors);
    process.stdout.write(`ok: ${rest[1]} holds the ${rest[0]} packages\n`);
    return 0;
  }
  if (command === 'notes' && rest.length === 1) {
    const notes = releaseNotes(await readFile(path.join(root, 'CHANGELOG.md'), 'utf8'), rest[0]);
    if (notes === null) return fail([`CHANGELOG.md has no entry for ${rest[0]}.`]);
    process.stdout.write(`${notes}\n`);
    return 0;
  }
  process.stderr.write(USAGE);
  return 2;
}

// Run as a script, not imported by the tests. import.meta.url has its symlinks resolved and process.argv[1] does not,
// so compare real paths: started through a symlinked directory, the script would otherwise do nothing and exit 0.
if (process.argv[1] && realpathSync(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main(process.argv.slice(2)).then(
    (code) => {
      process.exitCode = code;
    },
    (error) => {
      process.stderr.write(`error: ${error instanceof Error ? error.message : String(error)}\n`);
      process.exitCode = 1;
    },
  );
}
