#!/usr/bin/env node
// @ts-check
// Writes llms.txt (an index, https://llmstxt.org) and llms-full.txt (the docs in one file) in the repository root
// from the Markdown docs, with relative links made absolute so they work wherever the text ends up. In a git checkout
// it reads only the docs git tracks, so a local or ignored file under docs/ never ends up in them.
//
//   node scripts/llms.mjs           write both files (npm run docs:llms)
//   node scripts/llms.mjs --check   exit 1 when either is out of date (npm run docs:check)
//
// Plain Node, no dependencies.
import { execFileSync } from 'node:child_process';
import { existsSync, statSync } from 'node:fs';
import { readFile, readdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const REPOSITORY = 'DennisVerhoeven/netjes';
/** Raw files on main: what an agent fetches. */
export const RAW = `https://raw.githubusercontent.com/${REPOSITORY}/main/`;
/** Directories on main, which have no raw form. */
export const TREE = `https://github.com/${REPOSITORY}/tree/main/`;

/**
 * @typedef {{ path: string; title?: string; description?: string; full?: false } | { glob: string } | { rest: true }} Entry
 * @typedef {{ title: string; entries: Entry[] }} Section
 */

/** The skill's references, listed below in the order SKILL.md introduces them. */
const SKILL = 'packages/netjes/skills/netjes/references/';

/**
 * The reading order, in one place. An entry is a file, every `*.md` in a folder (`glob`, in name order), or every doc
 * under docs/ that no other entry names (`rest`). Files that do not exist are skipped, and a file is listed once, at its
 * first entry, so a specific file can go before the glob that would also match it. A file's title is its first
 * heading unless the entry gives one. Its description is the entry's, else its line in the docs index (`INDEX`), else
 * its first sentence. `full: false` lists a file in llms.txt only, and says so. Sections without files are left out.
 * "Optional" is the llms.txt convention for links an agent may skip.
 * @type {Section[]}
 */
export const SECTIONS = [
  {
    title: 'Docs',
    entries: [
      { path: 'README.md', title: 'README', description: 'What Netjes is and why, a small example, the quick start, the features and a map of the docs' },
      { path: 'docs/getting-started.md' },
      { path: 'docs/coming-from-nextjs.md' },
    ],
  },
  {
    title: 'Guides',
    entries: [
      { path: 'docs/guides/routing.md' },
      { path: 'docs/guides/data-loading.md' },
      { path: 'docs/guides/actions-and-forms.md' },
      { path: 'docs/guides/rendering-and-streaming.md' },
      { path: 'docs/guides/caching.md' },
      { path: 'docs/guides/metadata.md' },
      { path: 'docs/guides/images.md' },
      { path: 'docs/guides/static-export.md' },
      { path: 'docs/guides/security.md' },
      { path: 'docs/guides/deployment.md' },
      { path: 'docs/guides/testing.md' },
      { path: 'docs/guides/redirects-rewrites-headers.md' },
      { path: 'docs/guides/coding-agents.md' },
      { glob: 'docs/guides' },
    ],
  },
  {
    title: 'Reference',
    entries: [
      { path: 'docs/reference/react-api.md' },
      { path: 'docs/reference/csharp-api.md' },
      { path: 'docs/reference/options.md' },
      { path: 'docs/reference/cli.md' },
      { path: 'docs/reference/vite-plugin.md' },
      { glob: 'docs/reference' },
    ],
  },
  {
    title: 'Packages',
    entries: [
      { path: 'packages/netjes/README.md', title: 'netjes (npm)', description: 'The npm package: the Vite plugin, the client router, the sidecar and the netjes command' },
      { path: 'src/Netjes/README.md', title: 'Netjes (NuGet)', description: 'The NuGet package: the ASP.NET Core half' },
      { path: 'src/Netjes.Images.SkiaSharp/README.md', title: 'Netjes.Images.SkiaSharp (NuGet)', description: 'Optional image processing for `<Image>`: resizing and WebP with SkiaSharp' },
    ],
  },
  {
    title: 'Skill for coding agents',
    entries: [
      {
        path: 'packages/netjes/skills/netjes/SKILL.md',
        title: 'The netjes skill',
        description: 'The conventions of a Netjes app in short, as instructions for an agent; `netjes new` and `netjes agents` copy it into `.claude/skills/netjes/`',
      },
      { path: `${SKILL}routing.md`, description: 'Files and folders in `app/`, loader parameters and route warnings, links, metadata, protecting sections, base path and locales' },
      { path: `${SKILL}data-and-actions.md`, description: 'Page and layout loaders, shared props, server actions and their validation, JSON endpoints with TanStack Query' },
      { path: `${SKILL}caching-and-streaming.md`, description: 'Output-cached pages, seeding TanStack Query, streaming with `Page.Defer`, static export, sitemap and robots' },
      { path: `${SKILL}testing.md`, description: 'Vitest for components and pages, C# tests for loaders and actions, Playwright, and the checks before calling a change done' },
      { path: `${SKILL}deployment.md`, description: 'Build and publish, Docker, production settings, startup work and CLI commands, static hosting' },
      { glob: 'packages/netjes/skills/netjes/references' },
    ],
  },
  // A doc under docs/ that no entry names still gets listed, here, until it is given a place above.
  { title: 'More', entries: [{ rest: true }] },
  {
    title: 'Optional',
    entries: [
      // For people who change Netjes, so not in llms-full.txt: an agent building an app does not need the internals.
      { path: 'docs/architecture.md', full: false },
      { path: 'docs/decisions/README.md', title: 'Design decisions', description: 'The index of the decision records: why Netjes is built the way it is' },
      { path: 'CHANGELOG.md' },
      { path: 'docs/roadmap.md' },
      { path: 'docs/benchmarks.md', full: false },
      { path: 'examples/Blog/README.md', title: 'Blog example', full: false },
      { path: 'examples/TaskBoard/README.md', title: 'TaskBoard example', full: false },
    ],
  },
];

/**
 * Docs under docs/ that `rest` leaves out: the decision records, which the decisions index in Optional links, and the
 * docs index, which llms.txt stands in for.
 */
export const EXCLUDE = ['docs/decisions/', 'docs/README.md'];

/**
 * The docs index. A bullet in it that is one link followed by a colon, `- [Routing](guides/routing.md): what it
 * covers.`, gives that page's line in llms.txt, so the line is written once.
 */
export const INDEX = 'docs/README.md';

const SUMMARY =
  'React apps and sites on ASP.NET Core. C# owns routing, loaders, server actions, authorization and caching; a ' +
  'supervised pool of Node sidecars renders React; the browser hydrates and navigates. Pages follow the Next.js App ' +
  'Router conventions in `app/`.';

const DETAILS = [
  'Netjes ships as the npm package `netjes` (the Vite plugin, the client router, the sidecar and the `netjes` command) ' +
    'and the NuGet packages `Netjes` and `Netjes.Images.SkiaSharp`, released together under one version. ' +
    '`npx netjes new my-app` creates an app with an AGENTS.md and a `netjes` skill for coding agents; ' +
    '`npx netjes agents` adds them to an existing app.',
  `[llms-full.txt](${RAW}llms-full.txt) has the documents below in one file, apart from those marked as not in it.`,
];

/** @typedef {{ path: string; title: string; description: string; full: boolean; text: string }} Doc */

/** @param {string} text */
const lf = (text) => text.replace(/\r\n?/g, '\n');

/**
 * The `*.md` files git tracks (posix, from the root) when `root` is the top of a git checkout, else null: no git, or
 * not a checkout (the tests' temp folders), and then every file on disk counts. The `GIT_` variables a git hook sets
 * would point git at another checkout, so they are left out.
 * @param {string} root
 * @returns {Set<string> | null}
 */
function trackedMarkdown(root) {
  const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => !key.toUpperCase().startsWith('GIT_')));
  /** @param {string[]} args */
  const git = (...args) => execFileSync('git', args, { cwd: root, env, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
  try {
    if (git('rev-parse', '--show-prefix').trim() !== '') return null;
    return new Set(git('ls-files', '-z', '--', '*.md').split('\0').filter(Boolean));
  } catch {
    return null;
  }
}

/** @param {string} root @param {string} folder @returns {Promise<string[]>} posix paths of the `*.md` files in it */
async function markdownIn(root, folder) {
  const dir = path.join(root, folder);
  if (!existsSync(dir)) return [];
  return (await readdir(dir, { withFileTypes: true }))
    .filter((e) => e.isFile() && e.name.endsWith('.md'))
    .map((e) => `${folder}/${e.name}`)
    .sort();
}

/** @param {string} root @param {string} folder @returns {Promise<string[]>} every `*.md` below it, posix, sorted */
async function markdownBelow(root, folder) {
  const dir = path.join(root, folder);
  if (!existsSync(dir)) return [];
  const entries = await readdir(dir, { recursive: true, withFileTypes: true });
  return entries
    .filter((e) => e.isFile() && e.name.endsWith('.md'))
    .map((e) => path.relative(root, path.join(e.parentPath, e.name)).split(path.sep).join('/'))
    .sort();
}

/**
 * The docs that exist, by section, in reading order, and the files on disk left out because git does not track them.
 * @param {string} root
 * @param {Section[]} sections
 * @returns {Promise<{ sections: { title: string; docs: Doc[] }[]; untracked: string[] }>}
 */
export async function collect(root, sections = SECTIONS) {
  /** @typedef {{ path: string; title?: string; description?: string; full: boolean }} Listed */
  const tracked = trackedMarkdown(root);
  /** @type {string[]} */
  const untracked = [];
  /** @param {string} file */
  const listed = (file) => {
    if (tracked === null || tracked.has(file)) return true;
    if (!untracked.includes(file)) untracked.push(file);
    return false;
  };
  const lines = existsSync(path.join(root, INDEX)) && listed(INDEX) ? await indexLines(root) : new Map();
  /** @type {Set<string>} */
  const seen = new Set();
  /** @type {Map<Entry, Listed[]>} */
  const resolved = new Map();
  for (const section of sections) {
    for (const entry of section.entries) {
      if ('rest' in entry) continue;
      /** @type {Listed[]} */
      const files =
        'glob' in entry
          ? (await markdownIn(root, entry.glob)).filter(listed).map((file) => ({ path: file, full: true }))
          : existsSync(path.join(root, entry.path)) && listed(entry.path)
            ? [{ ...entry, full: entry.full !== false }]
            : [];
      const fresh = files.filter((file) => !seen.has(file.path));
      for (const file of fresh) seen.add(file.path);
      resolved.set(entry, fresh);
    }
  }
  /** @type {Listed[]} */
  const rest = (await markdownBelow(root, 'docs'))
    .filter((file) => !seen.has(file) && !EXCLUDE.some((prefix) => file.startsWith(prefix)) && listed(file))
    .map((file) => ({ path: file, full: true }));

  const result = [];
  for (const section of sections) {
    /** @type {Doc[]} */
    const docs = [];
    for (const entry of section.entries) {
      for (const file of 'rest' in entry ? rest : (resolved.get(entry) ?? [])) {
        const text = stripFrontMatter(lf(await readFile(path.join(root, file.path), 'utf8')));
        const about = summarize(text);
        const description = file.description ?? lines.get(file.path) ?? about.description;
        docs.push({
          path: file.path,
          title: file.title ?? about.title ?? path.basename(file.path, '.md'),
          description: file.full ? description : `${description} (not in llms-full.txt)`.trimStart(),
          full: file.full,
          text,
        });
      }
    }
    if (docs.length) result.push({ title: section.title, docs });
  }
  return { sections: result, untracked };
}

/**
 * The lines the docs index gives its pages, by posix path from the root, as llms.txt writes them: capitalised, without
 * the full stop.
 * @param {string} root
 * @returns {Promise<Map<string, string>>}
 */
async function indexLines(root) {
  /** @type {Map<string, string>} */
  const lines = new Map();
  for (const match of lf(await readFile(path.join(root, INDEX), 'utf8')).matchAll(/^[-*] \[[^\]]*\]\(([^)\s#]+)\): (.+?)\.?$/gm)) {
    const [, target = '', line = ''] = match;
    lines.set(path.posix.normalize(path.posix.join(path.posix.dirname(INDEX), target)), line.charAt(0).toUpperCase() + line.slice(1));
  }
  return lines;
}

/** @param {string} text */
export function stripFrontMatter(text) {
  const match = /^---\n[\s\S]*?\n---\n+/.exec(text);
  return match ? text.slice(match[0].length) : text;
}

/**
 * A document's title (its first H1) and a one-line description (the first sentence of its first paragraph).
 * @param {string} text
 * @returns {{ title: string | undefined; description: string }}
 */
export function summarize(text) {
  const lines = text.split('\n');
  const h1 = lines.findIndex((line) => /^# /.test(line));
  const title = h1 === -1 ? undefined : lines[h1]?.slice(2).trim();
  /** @type {string[]} */
  const paragraph = [];
  let fenced = false;
  for (const line of lines.slice(h1 + 1)) {
    if (/^\s*(```|~~~)/.test(line)) fenced = !fenced;
    if (fenced) continue;
    if (!line.trim()) {
      if (paragraph.length) break;
      continue;
    }
    // Prose only: not a heading, list, quote, table, HTML or a line that closes a fence.
    if (/^\s*(#|[-*+] |\d+\. |>|\||<|```|~~~)/.test(line)) {
      if (paragraph.length) break;
      continue;
    }
    paragraph.push(line.trim());
  }
  const prose = paragraph.join(' ').replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1');
  const sentence = /^.+?[.!?](?=\s|$)/.exec(prose)?.[0] ?? prose;
  return { title, description: sentence };
}

/**
 * Rewrites a document's relative links (inline, images and reference definitions) to absolute URLs on main: files to
 * their raw text, folders to their GitHub page. Absolute URLs, anchors within the page and code are left alone.
 * @param {string} text
 * @param {string} file the document's posix path from the root
 * @param {string} root
 */
export function rewriteLinks(text, file, root) {
  /** @param {string} target */
  const absolute = (target) => {
    if (/^([a-z][a-z0-9+.-]*:|\/\/|#)/i.test(target)) return target;
    const hash = target.indexOf('#');
    const [pathPart, fragment] = hash === -1 ? [target, ''] : [target.slice(0, hash), target.slice(hash)];
    if (!pathPart) return target;
    const resolved = pathPart.startsWith('/')
      ? path.posix.normalize(pathPart.slice(1))
      : path.posix.normalize(path.posix.join(path.posix.dirname(file), pathPart));
    if (resolved.startsWith('..')) return target;
    const clean = resolved.replace(/\/$/, '');
    const folder = statSync(path.join(root, clean), { throwIfNoEntry: false })?.isDirectory() ?? false;
    return `${folder ? TREE : RAW}${clean}${fragment}`;
  };
  /** @type {string | null} */
  let fence = null;
  return text
    .split('\n')
    .map((line) => {
      const marker = /^\s*(`{3,}|~{3,})/.exec(line)?.[1];
      if (fence !== null) {
        if (marker && marker[0] === fence[0] && marker.length >= fence.length && line.trim() === marker) fence = null;
        return line;
      }
      if (marker) {
        fence = marker;
        return line;
      }
      return rewriteTargets(line, absolute);
    })
    .join('\n');
}

/**
 * Rewrites the link targets in a line of prose: inline links and images, and a reference definition. They are found
 * with the inline code blanked out, so a link whose text is code (`` [`<Link>`](…) ``) counts and code that looks like
 * a link does not; the code itself is kept as written.
 * @param {string} line
 * @param {(target: string) => string} absolute
 */
function rewriteTargets(line, absolute) {
  const prose = blankCode(line);
  /** @type {{ at: number; length: number; target: string }[]} */
  const targets = [];
  for (const match of prose.matchAll(/(!?\[[^\]]*\]\()([^)\s]+)((?:\s+"[^"]*")?\))/g)) {
    targets.push({ at: (match.index ?? 0) + (match[1]?.length ?? 0), length: match[2]?.length ?? 0, target: match[2] ?? '' });
  }
  const definition = /^(\s{0,3}\[[^\]]+\]:\s*)(\S+)/.exec(prose);
  if (definition) targets.push({ at: definition[1]?.length ?? 0, length: definition[2]?.length ?? 0, target: definition[2] ?? '' });

  let out = '';
  let last = 0;
  for (const { at, length, target } of targets.sort((a, b) => a.at - b.at)) {
    // A target that runs into code is not a link GitHub would render.
    if (target.includes('\0')) continue;
    out += line.slice(last, at) + absolute(target);
    last = at + length;
  }
  return out + line.slice(last);
}

/**
 * The line with each inline code span, backticks included, replaced by as many NUL characters, and escaped backticks
 * by two. A run of backticks without a closing run of the same length is text, as in CommonMark.
 * @param {string} line
 */
function blankCode(line) {
  let out = '';
  let i = 0;
  while (i < line.length) {
    if (line[i] === '\\' && line[i + 1] === '`') {
      out += '\0\0';
      i += 2;
      continue;
    }
    if (line[i] !== '`') {
      out += line[i];
      i++;
      continue;
    }
    let after = i;
    while (line[after] === '`') after++;
    const ticks = after - i;
    // The closing run has exactly as many backticks.
    let close = -1;
    for (let j = after; j < line.length; ) {
      if (line[j] !== '`') {
        j++;
        continue;
      }
      let end = j;
      while (line[end] === '`') end++;
      if (end - j === ticks) {
        close = end;
        break;
      }
      j = end;
    }
    if (close === -1) {
      out += line.slice(i, after);
      i = after;
      continue;
    }
    out += '\0'.repeat(close - i);
    i = close;
  }
  return out;
}

/**
 * Both files' text, and the docs left out because git does not track them.
 * @param {string} root
 * @returns {Promise<{ files: { 'llms.txt': string; 'llms-full.txt': string }; untracked: string[] }>}
 */
export async function render(root) {
  const { sections, untracked } = await collect(root);
  const index = [`# Netjes`, '', `> ${SUMMARY}`, '', ...DETAILS.flatMap((p) => [p, ''])];
  for (const section of sections) {
    index.push(`## ${section.title}`, '');
    for (const doc of section.docs) {
      index.push(`- [${doc.title}](${RAW}${doc.path})${doc.description ? `: ${doc.description}` : ''}`);
    }
    index.push('');
  }

  const full = [
    '<!-- Generated from the Markdown docs by scripts/llms.mjs (npm run docs:llms); do not edit. -->',
    '',
    `# Netjes`,
    '',
    `> ${SUMMARY}`,
    '',
  ];
  for (const doc of sections.flatMap((s) => s.docs).filter((d) => d.full)) {
    full.push(`<!-- Source: ${RAW}${doc.path} -->`, '', rewriteLinks(doc.text, doc.path, root).trimEnd(), '');
  }
  return { files: { 'llms.txt': index.join('\n'), 'llms-full.txt': full.join('\n') }, untracked };
}

/**
 * @param {string[]} argv
 * @param {{ root: string; stdout(text: string): void; stderr(text: string): void }} io
 * @returns {Promise<number>}
 */
export async function main(argv, io) {
  const { files, untracked } = await render(io.root);
  if (untracked.length) io.stderr(`Left out, as git does not track them: ${untracked.join(', ')}\n`);
  if (argv.includes('--check')) {
    /** @type {string[]} */
    const stale = [];
    for (const [name, text] of Object.entries(files)) {
      const current = existsSync(path.join(io.root, name)) ? lf(await readFile(path.join(io.root, name), 'utf8')) : null;
      if (current !== text) stale.push(current === null ? `${name} is missing` : `${name} is out of date`);
    }
    if (stale.length) {
      io.stderr(`${stale.join('\n')}\nRun \`npm run docs:llms\` and commit the result.\n`);
      return 1;
    }
    io.stdout('llms.txt and llms-full.txt are up to date\n');
    return 0;
  }
  for (const [name, text] of Object.entries(files)) await writeFile(path.join(io.root, name), text);
  io.stdout(`Wrote ${Object.keys(files).join(' and ')}\n`);
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  process.exitCode = await main(process.argv.slice(2), {
    root: fileURLToPath(new URL('..', import.meta.url)),
    stdout: (text) => process.stdout.write(text),
    stderr: (text) => process.stderr.write(text),
  });
}
