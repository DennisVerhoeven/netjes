// @ts-check
// Checks on the repository's docs that the link check cannot make: node --test scripts/test/docs.test.mjs
import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import { describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';

const ROOT = fileURLToPath(new URL('../..', import.meta.url));
const DECISIONS = 'docs/decisions';

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
