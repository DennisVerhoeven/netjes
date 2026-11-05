// @ts-check
// Checks on the repository's docs that the link check cannot make: node --test scripts/test/docs.test.mjs
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import { describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';
import { SECTIONS, linkTargets } from '../llms.mjs';

const ROOT = fileURLToPath(new URL('../..', import.meta.url));
const DECISIONS = 'docs/decisions';
const INDEX = 'docs/README.md';
const GUIDES = 'docs/guides';
/** The path of a guide's file; what it captures is the guide's name. */
const GUIDE_FILE = new RegExp(`^${GUIDES}/([^/]+)\\.md$`);

/** A line that links this many guides or more lists them in the order of the docs index. */
const ORDERED_FROM = 4;

/**
 * This repository's files on GitHub, which the skill and the package READMEs link the docs by: `blob`, `tree` and raw
 * files, on any ref.
 */
const REPOSITORY_URL = /^https:\/\/(?:github\.com\/DennisVerhoeven\/netjes\/(?:blob|tree)|raw\.githubusercontent\.com\/DennisVerhoeven\/netjes)\/[^/]+\//i;

/** Folders whose Markdown is not the repository's docs: installed packages, and test fixtures that hold text on purpose. */
const NOT_DOCS = new Set(['node_modules', 'fixtures', '__fixtures__']);

/**
 * The `*.md` files git tracks (posix, from the root), or null when ROOT is not the top of a git checkout. The `GIT_`
 * variables a git hook sets would point git at another checkout, so they are left out.
 * @returns {string[] | null}
 */
function trackedMarkdown() {
  const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => !key.toUpperCase().startsWith('GIT_')));
  /** @param {string[]} args */
  const git = (...args) => execFileSync('git', args, { cwd: ROOT, env, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
  try {
    if (git('rev-parse', '--show-prefix').trim() !== '') return null;
    return git('ls-files', '-z', '--', '*.md').split('\0').filter(Boolean);
  } catch {
    return null;
  }
}

/**
 * The files among them that are the repository's docs, not installed packages or test fixtures.
 * @param {string[]} files posix paths from the root
 */
const docsAmong = (files) => files.filter((file) => !file.split('/').some((segment) => NOT_DOCS.has(segment)));

/**
 * The lines of each Mermaid block (a fenced code block whose language is `mermaid`) in a Markdown text, without the
 * fences, each with its number in the text, counted from 1. A fence ends at a line of the same character that is at
 * least as long as the opening one, so a block shown inside a longer fence is not found.
 * @param {string} markdown
 * @returns {{ line: number; text: string }[][]}
 */
function mermaidBlocks(markdown) {
  /** @type {{ line: number; text: string }[][]} */
  const blocks = [];
  // The fenced block being read: its fence, and its lines when it is a Mermaid block.
  /** @type {{ fence: string; lines?: { line: number; text: string }[] } | undefined} */
  let open;
  for (const [index, text] of markdown.split(/\r?\n/).entries()) {
    if (!open) {
      const start = /^ *(`{3,}|~{3,}) *([^\s`]*)/.exec(text);
      if (start) open = { fence: start[1] ?? '', lines: start[2] === 'mermaid' ? [] : undefined };
    } else if (new RegExp(`^ *${open.fence[0]}{${open.fence.length},} *$`).test(text)) {
      if (open.lines) blocks.push(open.lines);
      open = undefined;
    } else {
      open.lines?.push({ line: index + 1, text });
    }
  }
  return blocks;
}

/**
 * The numbers (counted from 1) of the lines of Mermaid sequence diagrams in a Markdown text that hold a `;` or a `#`
 * that is not part of an entity code. In a sequence diagram a `;` ends the statement and a `#` starts a comment, so
 * Mermaid cuts a message there: what follows a `;` is read as a statement of its own, usually a syntax error that
 * draws no diagram at all, and a `#` drops the rest of the line without a word. Mermaid sets an entity code aside
 * before it reads the diagram and draws it as its character (`#59;` for a `;`, `#35;` for a `#`), so a code is no
 * break. Like Mermaid, this leaves out front matter (from a first line `---` to the next), directives (`%%{` to `}%%`,
 * on one line or several) and comments (lines that start with `%%`), and takes the first statement left for the type:
 * a block is read when that statement starts with `sequenceDiagram`, and the rest of its line holds statements too.
 * @param {string} markdown
 * @returns {number[]}
 */
function sequenceDiagramBreaks(markdown) {
  /** @param {string} text */
  const blank = (text) => text.replace(/[^\n]/g, ' ');
  return mermaidBlocks(markdown).flatMap((block) => {
    // The block with what Mermaid leaves out blanked, so that each line keeps its number.
    const code = block
      .map(({ text }) => text)
      .join('\n')
      .replace(/^([ \t]*)---[ \t]*\n[\s\S]*?\n\1---[ \t]*(?=\n|$)/, blank)
      .replace(/%%\{[\s\S]*?\}%%/g, blank)
      .replace(/^[ \t]*%%.*$/gm, blank);
    if (!/^\s*sequenceDiagram/.test(code)) return [];
    const lines = code.split('\n');
    return block
      .filter((_, index) => /[;#]/.test((lines[index] ?? '').replace(/#\w+;/g, '')))
      .map(({ line }) => line);
  });
}

/**
 * The guide a link points at, by the name of its file in docs/guides/ (`caching` for docs/guides/caching.md), or
 * undefined for any other target. A relative target is read from the folder of the file that holds it, this repository's
 * files on GitHub count, and a query or a fragment does not.
 * @param {string} from posix path of the file that holds the link
 * @param {string} target
 * @returns {string | undefined}
 */
function guideOf(from, target) {
  const location = target.replace(/[?#].*$/, '');
  /** @type {string | undefined} */
  let file;
  if (REPOSITORY_URL.test(location)) file = location.replace(REPOSITORY_URL, '');
  else if (!/^([a-z][a-z0-9+.-]*:|\/\/)/i.test(location)) file = path.posix.normalize(path.posix.join(path.posix.dirname(from), location));
  return GUIDE_FILE.exec(file ?? '')?.[1];
}

/**
 * The guides that each line of a Markdown text links, with the line's number, counted from 1: every guide once, in the
 * order the line first links it. A line that links no guide is left out.
 * @param {string} from posix path of the file the text is from
 * @param {string} markdown
 * @returns {{ line: number; guides: string[] }[]}
 */
function guidesByLine(from, markdown) {
  /** @type {Map<number, string[]>} */
  const lines = new Map();
  for (const { line, target } of linkTargets(markdown.replace(/\r\n?/g, '\n'))) {
    const guide = guideOf(from, target);
    if (guide === undefined) continue;
    const guides = lines.get(line) ?? [];
    if (!guides.includes(guide)) guides.push(guide);
    lines.set(line, guides);
  }
  return [...lines].map(([line, guides]) => ({ line, guides }));
}

/**
 * The guides under `## Guides` in the docs index, in the order it lists them, each with the title of the H3 group above
 * it (undefined before the first group), and the groups' titles.
 * @param {string} index the text of the docs index
 * @returns {{ guides: { name: string; group: string | undefined }[]; groups: string[] } | undefined} undefined when the
 *   index has no `## Guides`
 */
function indexedGuides(index) {
  const text = index.replace(/\r\n?/g, '\n');
  /** @type {{ line: number; level: number; title: string }[]} */
  const headings = [];
  let fenced = false;
  for (const [at, line] of text.split('\n').entries()) {
    if (/^ {0,3}(```|~~~)/.test(line)) fenced = !fenced;
    const heading = fenced ? null : /^(#{2,3}) +(.*?) *$/.exec(line);
    if (heading) headings.push({ line: at + 1, level: heading[1]?.length ?? 0, title: heading[2] ?? '' });
  }
  const start = headings.findIndex(({ level, title }) => level === 2 && title === 'Guides');
  if (start === -1) return undefined;
  const end = headings.findIndex(({ level }, at) => at > start && level === 2);
  const section = headings.slice(start, end === -1 ? undefined : end);
  const groups = section.filter(({ level }) => level === 3);
  const first = section[0]?.line ?? 0;
  const last = headings[end]?.line ?? Infinity;
  return {
    guides: guidesByLine(INDEX, text)
      .filter(({ line }) => line > first && line < last)
      .flatMap(({ line, guides }) => guides.map((name) => ({ name, group: groups.findLast((group) => group.line < line)?.title }))),
    groups: groups.map(({ title }) => title),
  };
}

/**
 * The lines of a Markdown text that link `ORDERED_FROM` or more guides and list them in another order than the docs
 * index does, each with the order the index gives them in. A guide the index does not list has no place to keep.
 * @param {string} from posix path of the file the text is from
 * @param {string} markdown
 * @param {string[]} order the guides' names, in the order the docs index lists them
 * @returns {{ line: number; guides: string[]; expected: string[] }[]}
 */
function linesOutOfOrder(from, markdown, order) {
  return guidesByLine(from, markdown).flatMap(({ line, guides }) => {
    if (guides.length < ORDERED_FROM) return [];
    const known = guides.filter((guide) => order.includes(guide));
    const expected = [...known].sort((a, b) => order.indexOf(a) - order.indexOf(b));
    return known.some((guide, at) => guide !== expected[at]) ? [{ line, guides: known, expected }] : [];
  });
}

describe('the docs', () => {
  it('list every decision record in both indexes, in order, by its own title', async () => {
    const records = (await readdir(path.join(ROOT, DECISIONS))).filter((file) => /^\d{4}-.+\.md$/.test(file)).sort();
    /** @type {[string, string | undefined][]} */
    const titles = await Promise.all(
      records.map(async (file) => {
        const text = await readFile(path.join(ROOT, DECISIONS, file), 'utf8');
        return /** @type {[string, string | undefined]} */ ([file, /^# (.+)$/m.exec(text)?.[1]]);
      }),
    );

    for (const index of [`${DECISIONS}/README.md`, 'docs/README.md']) {
      const text = await readFile(path.join(ROOT, index), 'utf8');
      const listed = [...text.matchAll(/\[([^\]]+)\]\(([^)#\s]+)\)/g)]
        .map(([, label, target]) => [path.posix.relative(DECISIONS, path.posix.join(path.posix.dirname(index), target ?? '')), label])
        .filter(([file]) => /^\d{4}-.+\.md$/.test(file ?? ''));
      assert.deepEqual(listed, titles, index);
    }
  });

  // "Add Netjes to an existing app" tells readers to add "the one `netjes new` writes".
  it('inline the template\'s tsconfig.json in the guide for an existing app', async () => {
    const guide = await readFile(path.join(ROOT, 'docs/getting-started.md'), 'utf8');
    const step = guide.indexOf('Add a `tsconfig.json`, the one `netjes new` writes');
    assert.notEqual(step, -1, 'getting-started.md no longer has the tsconfig.json step');
    const block = /^ *```json\r?\n([\s\S]*?)\r?\n *```/m.exec(guide.slice(step));
    assert.ok(block, 'the tsconfig.json step has no json block');
    const inlined = JSON.parse(block[1] ?? '');
    const template = JSON.parse(await readFile(path.join(ROOT, 'packages/netjes/template/tsconfig.json'), 'utf8'));
    assert.deepEqual(inlined, template);
  });

  // Mermaid draws a diagram with a `;` in a message as a syntax error, and cuts a message at a `#` without a word.
  // This reads the characters that end a message; parsing the diagram would take Mermaid as a dependency.
  it('write no ; or lone # in Mermaid sequence diagrams, where either ends the message', async (t) => {
    const files = trackedMarkdown();
    if (!files) return t.skip('not a git checkout');
    /** @type {string[]} */
    const found = [];
    for (const file of docsAmong(files)) {
      const text = await readFile(path.join(ROOT, file), 'utf8');
      found.push(...sequenceDiagramBreaks(text).map((line) => `${file}:${line}`));
    }

    assert.deepEqual(
      found,
      [],
      'In a sequence diagram a ; ends the message and a # starts a comment: write #59; for a ; and #35; for a #, or reword',
    );
  });

  // The order is kept by hand in the docs index, in `SECTIONS` in scripts/llms.mjs, and in the few lines that name most
  // of the guides. A line that names three keeps the order of its sentence, so only four or more are checked.
  it("list the guides in the docs index's order wherever a line names four or more, and in llms.mjs", async (t) => {
    const files = trackedMarkdown();
    if (!files) return t.skip('not a git checkout');
    const indexed = indexedGuides(await readFile(path.join(ROOT, INDEX), 'utf8'));
    assert.ok(indexed, `${INDEX} has no ## Guides`);
    const order = indexed.guides.map(({ name }) => name);

    /** @type {string[]} */
    const problems = [];
    for (const file of docsAmong(files)) {
      const text = await readFile(path.join(ROOT, file), 'utf8');
      for (const { line, guides, expected } of linesOutOfOrder(file, text, order)) {
        problems.push(`${file}:${line} lists ${guides.join(', ')}; in the index's order that is ${expected.join(', ')}`);
      }
    }
    const section = SECTIONS.find(({ title }) => title === 'Guides');
    const listed = (section?.entries ?? []).flatMap((entry) => {
      const name = 'path' in entry ? GUIDE_FILE.exec(entry.path)?.[1] : undefined;
      return name === undefined ? [] : [name];
    });
    if (listed.join() !== order.join()) {
      problems.push(`scripts/llms.mjs lists ${listed.join(', ')} under Guides; the index's order is ${order.join(', ')}`);
    }

    assert.deepEqual(
      problems,
      [],
      `List the guides in the order of ${INDEX}, in a line that names four or more and in SECTIONS in scripts/llms.mjs`,
    );
  });

  it('group every guide in the docs index under exactly one H3', async () => {
    const indexed = indexedGuides(await readFile(path.join(ROOT, INDEX), 'utf8'));
    assert.ok(indexed, `${INDEX} has no ## Guides`);
    assert.ok(indexed.groups.length > 0, `${INDEX} has no H3 group under ## Guides`);
    const files = (await readdir(path.join(ROOT, GUIDES))).filter((file) => file.endsWith('.md')).map((file) => file.slice(0, -'.md'.length));
    const listed = indexed.guides.map(({ name }) => name);

    assert.deepEqual(
      {
        beforeTheFirstGroup: indexed.guides.filter(({ group }) => group === undefined).map(({ name }) => name),
        groupsWithoutGuides: indexed.groups.filter((title) => !indexed.guides.some(({ group }) => group === title)),
        listedTwice: listed.filter((name, at) => listed.indexOf(name) !== at),
        notListed: files.filter((name) => !listed.includes(name)).sort(),
        notFiles: listed.filter((name) => !files.includes(name)),
      },
      { beforeTheFirstGroup: [], groupsWithoutGuides: [], listedTwice: [], notListed: [], notFiles: [] },
      `Under ## Guides in ${INDEX}, list every file in ${GUIDES}/ once, in an H3 group`,
    );
  });
});

describe('the checks on the guides in the docs index', () => {
  const order = ['routing', 'data-loading', 'actions-and-forms', 'images', 'metadata', 'testing'];
  /** @param {...string} names the links of one line, to the guides of those names */
  const links = (...names) => names.map((name) => `[${name}](guides/${name}.md)`).join(', ');

  it('read a guide once per line, in the order the line links it, from a relative path or from GitHub', () => {
    const text = [
      '[Metadata](../guides/metadata.md#titles), [Routing](../guides/routing.md), [Metadata again](../guides/metadata.md)',
      '[Testing](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/testing.md), [Images](https://raw.githubusercontent.com/DennisVerhoeven/netjes/main/docs/guides/images.md?plain=1)',
      '[Options](options.md), [a site](https://example.com/docs/guides/routing.md), [a fork](https://github.com/someone/netjes/blob/main/docs/guides/routing.md), [a folder](../guides/sub/images.md)',
    ].join('\n');

    assert.deepEqual(guidesByLine('docs/reference/react-api.md', text), [
      { line: 1, guides: ['metadata', 'routing'] },
      { line: 2, guides: ['testing', 'images'] },
    ]);
  });

  it('leave out a link in code', () => {
    const text = ['`[Routing](guides/routing.md)` and [`Images`](guides/images.md)', '```md', links('metadata', 'testing'), '```'].join('\n');

    assert.deepEqual(guidesByLine('docs/README.md', text), [{ line: 1, guides: ['images'] }]);
  });

  it('report a line of four or more guides in another order, not one of three, one in order or one that skips a guide', () => {
    const text = [
      links('metadata', 'images', 'routing'),
      links('metadata', 'metadata', 'images', 'images', 'routing'),
      links('routing', 'data-loading', 'images', 'metadata'),
      links('routing', 'images', 'data-loading', 'metadata'),
      links('routing', 'later', 'data-loading', 'testing'),
    ].join('\n');

    assert.deepEqual(linesOutOfOrder('docs/README.md', text, order), [
      { line: 4, guides: ['routing', 'images', 'data-loading', 'metadata'], expected: ['routing', 'data-loading', 'images', 'metadata'] },
    ]);
  });

  it('read the groups and the guides under ## Guides, not those of another section', () => {
    const index = [
      '# Netjes documentation',
      '',
      '## Start here',
      '',
      '- [A guide, early](guides/early.md): not under ## Guides.',
      '',
      '## Guides',
      '',
      'One topic each.',
      '',
      '- [Loose](guides/loose.md): before any group.',
      '',
      '### Build',
      '',
      '- [Routing](guides/routing.md): pages.',
      '- [Data loading](guides/data-loading.md): loaders.',
      '',
      '### Run',
      '',
      '- [Security](guides/security.md): headers.',
      '',
      '## Reference',
      '',
      '- [A guide, late](guides/late.md): not under ## Guides.',
      '',
      '### Design decisions',
    ].join('\n');

    assert.deepEqual(indexedGuides(index), {
      guides: [
        { name: 'loose', group: undefined },
        { name: 'routing', group: 'Build' },
        { name: 'data-loading', group: 'Build' },
        { name: 'security', group: 'Run' },
      ],
      groups: ['Build', 'Run'],
    });
    assert.equal(indexedGuides('# Netjes documentation\n\n## Reference\n'), undefined);
  });
});

describe('the check for a ; or # in a sequence diagram', () => {
  /**
   * A Markdown text with one Mermaid sequence diagram, whose first statement is on line 3.
   * @param {...string} statements
   */
  const diagram = (...statements) => ['```mermaid', 'sequenceDiagram', ...statements, '```'].join('\n');

  it('reports the line of a ; and of a lone # in a message, a note and an alias', () => {
    const text = diagram(
      'A->>B: fine',
      'A->>B: one; two',
      'A->>B: run the C# loader',
      'Note over A,B: see issue #12',
      'participant C as C#',
      'A->>B: x##59; y',
    );

    assert.deepEqual(sequenceDiagramBreaks(text), [4, 5, 6, 7, 8]);
  });

  it('takes an entity code for its character, and leaves comments and the text around the diagram alone', () => {
    const text = [
      'A ; and a # in prose are nothing to Mermaid.',
      diagram('A->>B: a semicolon #59;, a hash C#35; and a heart #9829;', '%% a comment may use ; and #'),
      'Nor are this ; and this #.',
    ].join('\n');

    assert.deepEqual(sequenceDiagramBreaks(text), []);
  });

  it('reads sequence diagrams only, not other diagrams, other blocks or a block shown in a longer fence', () => {
    const text = [
      ['```mermaid', 'flowchart LR', '  a[PageResult&lt;T&gt;] --> b', '```'].join('\n'),
      ['```mermaid', 'flowchart LR', '  sequenceDiagram --> b', '  b --> c;', '```'].join('\n'),
      ['```text', 'sequenceDiagram', 'A->>B: not Mermaid; at all', '```'].join('\n'),
      ['````markdown', '```text', 'x', '```', diagram('A->>B: shown; not drawn'), '````'].join('\n'),
    ].join('\n\n');

    assert.deepEqual(sequenceDiagramBreaks(text), []);
  });

  it('reads a diagram after front matter, in a ~~~ fence, indented in a list item and with autonumber on its first line', () => {
    const text = [
      '- A list item:',
      '',
      '  ~~~mermaid',
      '  ---',
      "  config: { themeVariables: { primaryColor: '#ff0000' } }",
      '  ---',
      '  sequenceDiagram autonumber',
      '  A->>B: one; two',
      '  ~~~',
    ].join('\n');

    assert.deepEqual(sequenceDiagramBreaks(text), [8]);
  });

  it('reads statements on the sequenceDiagram line, and a diagram after a comment and a directive over several lines', () => {
    const text = [
      ['```mermaid', 'sequenceDiagram A->>B: run the C# loader', '```'].join('\n'),
      [
        '```mermaid',
        '%% a comment',
        '%%{',
        "  init: { 'themeVariables': { 'primaryColor': '#ff0000' } }",
        '}%%',
        'sequenceDiagram',
        'A->>B: one; two',
        '```',
      ].join('\n'),
    ].join('\n');

    assert.deepEqual(sequenceDiagramBreaks(text), [2, 10]);
  });
});
