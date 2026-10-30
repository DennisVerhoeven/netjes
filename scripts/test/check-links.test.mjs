// @ts-check
// Tests for scripts/check-links.mjs: node --test scripts/test/check-links.test.mjs
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, it } from 'node:test';
import { headingSlugs, main } from '../check-links.mjs';

const BLOB = 'https://github.com/DennisVerhoeven/netjes/blob/main/';
const TREE = 'https://github.com/DennisVerhoeven/netjes/tree/main/';
const RAW = 'https://raw.githubusercontent.com/DennisVerhoeven/netjes/main/';

/** @type {string} */
let root;

beforeEach(async () => {
  root = await mkdtemp(path.join(tmpdir(), 'netjes-links-'));
  git('init', '-q');
});
afterEach(async () => {
  await rm(root, { recursive: true, force: true });
});

/** @param {string[]} args */
function git(...args) {
  // A git hook's GIT_ variables would point git at another checkout.
  const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => !key.toUpperCase().startsWith('GIT_')));
  execFileSync('git', ['-c', 'init.defaultBranch=main', ...args], { cwd: root, env, stdio: 'ignore' });
}

/**
 * Writes the files and has git track them, unless `untracked` names them.
 * @param {Record<string, string>} files
 * @param {{ untracked?: string[] }} [options]
 */
async function tracked(files, { untracked = [] } = {}) {
  for (const [file, text] of Object.entries(files)) {
    await mkdir(path.dirname(path.join(root, file)), { recursive: true });
    await writeFile(path.join(root, file), text);
  }
  const add = Object.keys(files).filter((file) => !untracked.includes(file));
  if (add.length) git('add', '--', ...add);
}

/** @param {string[]} [argv] */
async function run(argv = []) {
  let out = '';
  let err = '';
  const code = await main(argv, {
    root,
    stdout: (text) => {
      out += text;
    },
    stderr: (text) => {
      err += text;
    },
  });
  return { code, out, err };
}

describe('check-links.mjs', () => {
  it('passes when every relative link points at a tracked file or folder', async () => {
    await tracked({
      'README.md': '# Netjes\n\nSee [the guide](docs/guides/routing.md), [the docs](docs/), [the code](src) and [the root](/README.md).\n',
      'docs/guides/routing.md': '# Routing\n\nBack to [the README](../../README.md) and ![a picture](img/map.png "Map").\n',
      'docs/guides/img/map.png': 'png',
      'src/Program.cs': '// code',
    });

    const { code, out, err } = await run();

    assert.equal(err, '');
    assert.equal(code, 0);
    assert.equal(out, 'All 6 links in 2 Markdown files resolve\n');
  });

  it('reports a link to a missing file as file:line: link (reason) and exits 1', async () => {
    await tracked({
      'docs/README.md': '# Docs\n\nIntro.\n\n- [Recipes](recipes/headers.md)\n- [Routing](guides/routing.md)\n',
      'docs/guides/routing.md': '# Routing\n',
    });

    const { code, err } = await run();

    assert.equal(code, 1);
    assert.equal(err, 'docs/README.md:5: recipes/headers.md (no such file)\n1 broken link in 2 Markdown files\n');
  });

  it('treats a file git does not track as missing, and leaves untracked Markdown unchecked', async () => {
    await tracked(
      {
        'README.md': '# Netjes\n\n[Notes](notes.md)\n',
        'notes.md': '# Notes\n\n[Nowhere](nowhere.md)\n',
      },
      { untracked: ['notes.md'] },
    );

    const { code, err } = await run();

    assert.equal(code, 1);
    assert.equal(err, 'README.md:3: notes.md (not tracked by git)\n1 broken link in 1 Markdown file\n');
  });

  it('reports links that leave the repository', async () => {
    await tracked({ 'docs/a.md': '# A\n\n[Out](../../elsewhere/README.md)\n' });

    const { err } = await run();

    assert.match(err, /^docs\/a\.md:3: \.\.\/\.\.\/elsewhere\/README\.md \(outside the repository\)\n/);
  });

  it('checks anchors against the headings of the target, as GitHub writes their ids', async () => {
    await tracked({
      'docs/guides/actions.md': [
        '# Actions and forms',
        '',
        '## `useAction` and `<Form>`',
        '',
        '## Results',
        '',
        '### Results',
        '',
        '## C# loaders: `Page.Defer(...)`',
        '',
        '```md',
        '## Not a heading',
        '```',
        '',
      ].join('\n'),
      'docs/reference.md': [
        '# Reference',
        '',
        '[form](guides/actions.md#useaction-and-form), [results](guides/actions.md#results), [again](guides/actions.md#results-1),',
        '[defer](guides/actions.md#c-loaders-pagedefer), [here](#reference), [code](guides/actions.md#not-a-heading),',
        '[case](guides/actions.md#Results), [no such](guides/actions.md#results-2).',
        '',
      ].join('\n'),
    });

    const { code, err } = await run();

    assert.equal(code, 1);
    assert.equal(
      err,
      [
        'docs/reference.md:4: guides/actions.md#not-a-heading (no heading for #not-a-heading)',
        'docs/reference.md:5: guides/actions.md#Results (no heading for #Results)',
        'docs/reference.md:5: guides/actions.md#results-2 (no heading for #results-2)',
        '3 broken links in 2 Markdown files',
        '',
      ].join('\n'),
    );
  });

  it('checks links to this repository on GitHub against the local tree', async () => {
    await tracked({
      'docs/routing.md': '# Routing\n\n## Route warnings\n',
      'packages/netjes/template/_AGENTS.md': [
        '# App',
        '',
        `[Routing](${BLOB}docs/routing.md#route-warnings), [the docs](${TREE}docs), [raw](${RAW}docs/routing.md),`,
        `[gone](${BLOB}docs/recipes/headers.md), [no anchor](${BLOB}docs/routing.md#warnings), [raw folder](${RAW}docs),`,
        'and a bare one: https://github.com/DennisVerhoeven/netjes/blob/main/docs/missing.md.',
        '',
        '[issues](https://github.com/DennisVerhoeven/netjes/issues), [elsewhere](https://example.com/x.md#y), [mail](mailto:a@example.com).',
        '',
      ].join('\n'),
    });

    const { code, err } = await run();

    assert.equal(code, 1);
    assert.equal(
      err,
      [
        `packages/netjes/template/_AGENTS.md:4: ${BLOB}docs/recipes/headers.md (no such file)`,
        `packages/netjes/template/_AGENTS.md:4: ${BLOB}docs/routing.md#warnings (no heading for #warnings)`,
        `packages/netjes/template/_AGENTS.md:4: ${RAW}docs (not a file)`,
        `packages/netjes/template/_AGENTS.md:5: ${BLOB}docs/missing.md (no such file)`,
        '4 broken links in 2 Markdown files',
        '',
      ].join('\n'),
    );
  });

  it('checks reference definitions, and ignores links in code', async () => {
    await tracked({
      'README.md': [
        '# Netjes',
        '',
        'Read [the guide][guide], or `[inline](missing.md)` and ``a `[b](missing.md)` c``.',
        '',
        '~~~md',
        '[fenced](missing.md)',
        '~~~',
        '',
        '    ```',
        '    [indented fence](missing.md)',
        '    ```',
        '',
        '[guide]: docs/guide.md',
        '[old]: docs/old.md "Gone"',
        '',
      ].join('\n'),
      'docs/guide.md': '# Guide\n',
    });

    const { err } = await run();

    assert.equal(err, 'README.md:14: docs/old.md (no such file)\n1 broken link in 2 Markdown files\n');
  });

  it('leaves out test fixtures', async () => {
    await tracked({
      'README.md': '# Netjes\n',
      'packages/netjes/test/fixtures/page/README.md': '# Fixture\n\n[missing](missing.md)\n',
    });

    const { code, out } = await run();

    assert.equal(code, 0);
    assert.match(out, /in 1 Markdown file resolve/);
  });

  it('decodes percent-encoded paths and anchors, and ignores a query string', async () => {
    await tracked({
      'docs/a b.md': '# Ünïcode heading\n',
      'README.md': '# Netjes\n\n[space](docs/a%20b.md#%C3%BCn%C3%AFcode-heading), [query](docs/a%20b.md?plain=1)\n',
    });

    assert.equal((await run()).code, 0);
  });
});

describe('headingSlugs', () => {
  it('follows GitHub: lower case, punctuation dropped, spaces to hyphens, repeats numbered', () => {
    const text = [
      '# 0001 – React renders in Node',
      '## `PageResult<T>` and PageResult&lt;TProps&gt;',
      '## Third-party scripts: `<Script>`',
      '## What a *failed* post hands back',
      '## `__netjes_return` and _emphasis_',
      '## [Linked](https://example.com) heading <img src="x.png">',
      'Setext heading',
      '--------------',
      '## Results',
      '## Results',
      '## Results',
      '## Results #',
    ].join('\n');

    assert.deepEqual(headingSlugs(text), [
      '0001--react-renders-in-node',
      'pageresultt-and-pageresulttprops',
      'third-party-scripts-script',
      'what-a-failed-post-hands-back',
      '__netjes_return-and-emphasis',
      'linked-heading-',
      'setext-heading',
      'results',
      'results-1',
      'results-2',
      'results-3',
    ]);
  });

  it('counts explicit anchors as targets too', () => {
    assert.deepEqual(headingSlugs('# Title\n\n<a id="custom"></a>\n<a name="old-name"></a>\n'), ['title', 'custom', 'old-name']);
  });
});
