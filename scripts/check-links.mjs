#!/usr/bin/env node
// @ts-check
// Checks every link in the Markdown files git tracks. A relative link must point at a file or folder git tracks, and
// with an #anchor at a heading of that file, by the ids GitHub gives headings. Links to this repository on GitHub
// (blob/main, tree/main and raw files on main) are checked the same way against the local tree, so the text that
// apps copy from the template and the skill stays right too. Other http(s) links are not fetched. Links in code are
// not links. Test fixtures are left out.
//
//   node scripts/check-links.mjs     print each broken link as file:line: link (reason), exit 1 if there is one
//                                     (part of npm run docs:check)
//
// Plain Node, no dependencies.
import { execFileSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

/** Links to these, followed by a path in the repository, are checked against the local tree. */
const REPOSITORY_URLS = [
  { prefix: /^https:\/\/github\.com\/DennisVerhoeven\/netjes\/(?:blob|tree)\/main(?:\/|$)/i, raw: false },
  { prefix: /^https:\/\/raw\.githubusercontent\.com\/DennisVerhoeven\/netjes\/main(?:\/|$)/i, raw: true },
];

/** The same URLs written bare in prose, which GitHub turns into links. */
const BARE_URL =
  /https:\/\/(?:github\.com\/DennisVerhoeven\/netjes\/(?:blob|tree)\/main|raw\.githubusercontent\.com\/DennisVerhoeven\/netjes\/main)(?:\/[^\s<>()[\]"'`]*)?/gi;

/** A path segment that marks test fixtures, whose links are test data. */
const FIXTURES = new Set(['fixtures', '__fixtures__']);

/** @typedef {{ file: string; line: number; link: string; reason: string }} Broken */

/**
 * The files git tracks (posix, from the root), or null when `root` is not the top of a git checkout. The `GIT_`
 * variables a git hook sets would point git at another checkout, so they are left out.
 * @param {string} root
 * @returns {string[] | null}
 */
function trackedFiles(root) {
  const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => !key.toUpperCase().startsWith('GIT_')));
  /** @param {string[]} args */
  const git = (...args) => execFileSync('git', args, { cwd: root, env, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
  try {
    if (git('rev-parse', '--show-prefix').trim() !== '') return null;
    return git('ls-files', '-z').split('\0').filter(Boolean);
  } catch {
    return null;
  }
}

/**
 * Splits a line into the parts outside inline code spans and the code spans themselves, in order.
 * @param {string} line
 * @returns {{ code: boolean; text: string }[]}
 */
function codeSpans(line) {
  /** @type {{ code: boolean; text: string }[]} */
  const parts = [];
  let i = 0;
  let prose = '';
  while (i < line.length) {
    if (line[i] === '\\' && line[i + 1] === '`') {
      prose += line.slice(i, i + 2);
      i += 2;
      continue;
    }
    if (line[i] !== '`') {
      prose += line[i];
      i++;
      continue;
    }
    let after = i;
    while (line[after] === '`') after++;
    const ticks = after - i;
    // The closing run has exactly as many backticks; without one, the backticks are text.
    let close = -1;
    for (let j = after; j < line.length; ) {
      if (line[j] !== '`') {
        j++;
        continue;
      }
      let end = j;
      while (line[end] === '`') end++;
      if (end - j === ticks) {
        close = j;
        break;
      }
      j = end;
    }
    if (close === -1) {
      prose += line.slice(i, after);
      i = after;
      continue;
    }
    if (prose) parts.push({ code: false, text: prose });
    prose = '';
    parts.push({ code: true, text: line.slice(after, close) });
    i = close + ticks;
  }
  if (prose) parts.push({ code: false, text: prose });
  return parts;
}

/**
 * The lines of a Markdown file outside front matter and fenced code, with their line numbers.
 * @param {string} text
 * @returns {{ number: number; text: string }[]}
 */
function proseLines(text) {
  const lines = text.replace(/\r\n?/g, '\n').split('\n');
  /** @type {{ number: number; text: string }[]} */
  const result = [];
  let start = 0;
  if (lines[0] === '---') {
    const end = lines.indexOf('---', 1);
    if (end !== -1) start = end + 1;
  }
  /** @type {string | null} */
  let fence = null;
  for (let i = start; i < lines.length; i++) {
    const line = lines[i] ?? '';
    const marker = /^\s*(`{3,}|~{3,})/.exec(line)?.[1];
    if (fence !== null) {
      if (marker && marker[0] === fence[0] && marker.length >= fence.length && line.trim() === marker) fence = null;
      continue;
    }
    if (marker) {
      fence = marker;
      continue;
    }
    result.push({ number: i + 1, text: line });
  }
  return result;
}

const ENTITIES = /** @type {Record<string, string>} */ ({ lt: '<', gt: '>', amp: '&', quot: '"', apos: "'", nbsp: ' ' });

/** @param {string} text */
function decodeEntities(text) {
  return text.replace(/&(#x[0-9a-f]+|#[0-9]+|[a-z]+);/gi, (entity, name) => {
    const lower = name.toLowerCase();
    if (lower.startsWith('#x')) return String.fromCodePoint(Number.parseInt(lower.slice(2), 16));
    if (lower.startsWith('#')) return String.fromCodePoint(Number.parseInt(lower.slice(1), 10));
    return ENTITIES[lower] ?? entity;
  });
}

/**
 * The text GitHub renders for a heading's Markdown: code spans as written, links as their text, images as their alt
 * text, without HTML tags, emphasis markers or backslash escapes.
 * @param {string} heading
 */
function renderedText(heading) {
  return codeSpans(heading)
    .map(({ code, text }) => {
      if (code) return /^ .*[^ ].* $/.test(text) ? text.slice(1, -1) : text;
      return decodeEntities(
        text
          .replace(/!\[([^\]]*)\]\([^)]*\)/g, '$1')
          .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
          .replace(/\[([^\]]*)\]\[[^\]]*\]/g, '$1')
          .replace(/<\/?[a-z][^>]*>/gi, '')
          .replace(/(^|[^\p{L}\p{N}_])_+(?=\S)|(?<=\S)_+(?=$|[^\p{L}\p{N}_])/gu, '$1')
          .replace(/\\([!-/:-@[-`{-~])/g, '$1'),
      );
    })
    .join('');
}

/**
 * The anchors a Markdown file has, in order: each heading's id as GitHub writes it (lower case, everything but
 * letters, marks, numbers, `_`, `-` and spaces dropped, spaces turned into hyphens, `-1`, `-2` and so on after a
 * repeated one), then the ids and names of `<a>` elements.
 * @param {string} text
 * @returns {string[]}
 */
export function headingSlugs(text) {
  /** @type {Map<string, number>} */
  const seen = new Map();
  /** @type {string[]} */
  const slugs = [];
  /** @type {string[]} */
  const explicit = [];
  /** @param {string} heading */
  const add = (heading) => {
    const base = renderedText(heading)
      .toLowerCase()
      .replace(/[^\p{L}\p{M}\p{N}\p{Pc} -]/gu, '')
      .replace(/ /g, '-');
    const count = seen.get(base) ?? 0;
    seen.set(base, count + 1);
    slugs.push(count === 0 ? base : `${base}-${count}`);
  };
  const lines = proseLines(text);
  lines.forEach((line, i) => {
    const atx = /^ {0,3}#{1,6}(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$/.exec(line.text);
    if (atx) {
      add(atx[1] ?? '');
    } else if (/^ {0,3}(=+|-+)[ \t]*$/.test(line.text)) {
      // A setext heading: the line above, when it is a paragraph line right above this one.
      const above = lines[i - 1];
      if (above && above.number === line.number - 1 && above.text.trim() && !/^\s*([#>|<]|[-*+] |\d+[.)] )/.test(above.text)) {
        add(above.text.trim());
      }
    }
    for (const match of line.text.matchAll(/<a\s[^>]*?\b(?:id|name)\s*=\s*["']([^"']+)["']/gi)) {
      if (match[1]) explicit.push(match[1]);
    }
  });
  return [...slugs, ...explicit];
}

/**
 * The link targets in a line of prose, in order: inline links and images, reference definitions, autolinks, and links
 * to this repository written bare.
 * @param {string} line
 * @returns {string[]}
 */
function linksIn(line) {
  /** @type {{ at: number; link: string }[]} */
  const found = [];
  let offset = 0;
  for (const part of codeSpans(line)) {
    if (part.code) {
      offset += part.text.length;
      continue;
    }
    // Each target found is blanked out, so the bare-URL pass below does not count it again.
    let prose = part.text;
    /** @param {number} at @param {string} link @param {string} whole */
    const take = (at, link, whole) => {
      found.push({ at: offset + at, link });
      return ' '.repeat(whole.length);
    };
    if (offset === 0) {
      prose = prose.replace(/^( {0,3}\[[^\]^][^\]]*\]:[ \t]*)(<[^>]*>|\S+)/, (whole, lead, target, at) =>
        lead + take(at + lead.length, unbracket(target), target),
      );
    }
    prose = prose.replace(/(\]\(\s*)(<[^>]*>|[^)\s]+)((?:\s+(?:"[^"]*"|'[^']*'))?\s*\))/g, (whole, open, target, close, at) =>
      open + take(at + open.length, unbracket(target), target) + close,
    );
    prose = prose.replace(/<(https?:\/\/[^\s>]+)>/gi, (whole, target, at) => take(at, target, whole));
    for (const match of prose.matchAll(BARE_URL)) {
      found.push({ at: offset + (match.index ?? 0), link: match[0].replace(/[.,:;!?'"*_~]+$/, '') });
    }
    offset += part.text.length;
  }
  return found.sort((a, b) => a.at - b.at).map((entry) => entry.link);
}

/** @param {string} target */
function unbracket(target) {
  return target.startsWith('<') && target.endsWith('>') ? target.slice(1, -1) : target;
}

/** @param {string} text */
function decodePath(text) {
  try {
    return decodeURIComponent(text);
  } catch {
    return text;
  }
}

/**
 * Checks the links of every Markdown file git tracks under `root`.
 * @param {string} root
 * @returns {Promise<{ files: number; links: number; broken: Broken[] } | null>} null when `root` is not a git checkout
 */
export async function checkLinks(root) {
  const files = trackedFiles(root);
  if (files === null) return null;
  const tracked = new Set(files);
  /** @type {Set<string>} */
  const folders = new Set(['']);
  for (const file of files) {
    for (let dir = path.posix.dirname(file); dir !== '.' && !folders.has(dir); dir = path.posix.dirname(dir)) folders.add(dir);
  }
  const markdown = files.filter(
    (file) => /\.md$/i.test(file) && !file.split('/').some((segment) => segment === 'node_modules' || FIXTURES.has(segment)),
  );

  /** @type {Map<string, Promise<Set<string>>>} */
  const anchors = new Map();
  /** @param {string} file */
  const anchorsOf = (file) => {
    let result = anchors.get(file);
    if (!result) {
      result = readFile(path.join(root, file), 'utf8').then((text) => new Set(headingSlugs(text)));
      anchors.set(file, result);
    }
    return result;
  };

  /**
   * Why a link from `file` is broken, or null when it resolves.
   * @param {string} file
   * @param {string} link
   * @returns {Promise<string | null>}
   */
  const problem = async (file, link) => {
    const repository = REPOSITORY_URLS.find(({ prefix }) => prefix.test(link));
    const hash = link.indexOf('#');
    const [location, anchor] = hash === -1 ? [link, ''] : [link.slice(0, hash), decodePath(link.slice(hash + 1))];
    const pathPart = decodePath(location.replace(/\?.*$/, ''));
    /** @type {string} */
    let target;
    if (repository) {
      target = path.posix.normalize(pathPart.replace(repository.prefix, '') || '.');
    } else if (!pathPart) {
      target = file;
    } else {
      target = path.posix.normalize(pathPart.startsWith('/') ? pathPart.slice(1) || '.' : path.posix.join(path.posix.dirname(file), pathPart));
    }
    target = target.replace(/\/+$/, '') || '.';
    if (target === '..' || target.startsWith('../')) return 'outside the repository';
    const isFolder = folders.has(target === '.' ? '' : target);
    if (!tracked.has(target) && !isFolder) {
      const onDisk = await readFile(path.join(root, target)).then(
        () => true,
        () => false,
      );
      return onDisk ? 'not tracked by git' : 'no such file';
    }
    if (repository?.raw && isFolder) return 'not a file';
    if (anchor && !isFolder && /\.md$/i.test(target) && !(await anchorsOf(target)).has(anchor)) return `no heading for #${anchor}`;
    return null;
  };

  /** @type {Broken[]} */
  const broken = [];
  let links = 0;
  for (const file of markdown) {
    const text = await readFile(path.join(root, file), 'utf8');
    for (const line of proseLines(text)) {
      for (const link of linksIn(line.text)) {
        // Other sites, mail and the like are not this repository's to check.
        const elsewhere = /^[a-z][a-z0-9+.-]*:|^\/\//i.test(link) && !REPOSITORY_URLS.some(({ prefix }) => prefix.test(link));
        if (elsewhere || link === '#') continue;
        links++;
        const reason = await problem(file, link);
        if (reason) broken.push({ file, line: line.number, link, reason });
      }
    }
  }
  return { files: markdown.length, links, broken };
}

/** @param {number} count @param {string} word */
const plural = (count, word) => `${count} ${word}${count === 1 ? '' : 's'}`;

/**
 * @param {string[]} _argv
 * @param {{ root: string; stdout(text: string): void; stderr(text: string): void }} io
 * @returns {Promise<number>}
 */
export async function main(_argv, io) {
  const result = await checkLinks(io.root);
  if (result === null) {
    io.stderr(`${io.root} is not the top of a git checkout; the link check reads the files git tracks.\n`);
    return 2;
  }
  const { files, links, broken } = result;
  if (broken.length) {
    io.stderr(broken.map((b) => `${b.file}:${b.line}: ${b.link} (${b.reason})\n`).join(''));
    io.stderr(`${plural(broken.length, 'broken link')} in ${plural(files, 'Markdown file')}\n`);
    return 1;
  }
  io.stdout(`All ${plural(links, 'link')} in ${plural(files, 'Markdown file')} resolve\n`);
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  process.exitCode = await main(process.argv.slice(2), {
    root: fileURLToPath(new URL('..', import.meta.url)),
    stdout: (text) => process.stdout.write(text),
    stderr: (text) => process.stderr.write(text),
  });
}
