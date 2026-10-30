// @ts-check
// Tests for scripts/llms.mjs: node --test scripts/test/llms.test.mjs
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';
import { main, render } from '../llms.mjs';

const REPOSITORY_ROOT = fileURLToPath(new URL('../..', import.meta.url));
const RAW = 'https://raw.githubusercontent.com/DennisVerhoeven/netjes/main/';
const TREE = 'https://github.com/DennisVerhoeven/netjes/tree/main/';

/** @type {string} */
let root;

beforeEach(async () => {
  root = await mkdtemp(path.join(tmpdir(), 'netjes-llms-'));
});
afterEach(async () => {
  await rm(root, { recursive: true, force: true });
});

/** @param {Record<string, string>} files */
async function docs(files) {
  for (const [file, text] of Object.entries(files)) {
    await mkdir(path.dirname(path.join(root, file)), { recursive: true });
    await writeFile(path.join(root, file), text);
  }
}

/** @param {string[]} argv */
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

/** @param {string} file */
const read = (file) => readFile(path.join(root, file), 'utf8');

/**
 * The documents llms-full.txt holds, in order, by the source line that precedes each.
 * @param {string} full
 */
const sources = (full) => [...full.matchAll(/^<!-- Source: (\S+) -->$/gm)].map((m) => m[1]?.replace(RAW, ''));

const minimal = {
  'README.md': '# Netjes\n\nThe pitch.\n',
  'packages/netjes/README.md': '# netjes\n\nThe npm half.\n',
};

describe('llms.mjs', () => {
  it('concatenates the docs in reading order and skips the ones that do not exist', async () => {
    await docs({
      ...minimal,
      'docs/coming-from-nextjs.md': '# Coming from Next.js\n\nConcept by concept.\n',
      'docs/architecture.md': '# Architecture\n\nHow it works.\n',
      // The guides and references go in the order the list gives, then the others in name order.
      'docs/guides/testing.md': '# Testing\n\nVitest.\n',
      'docs/guides/routing.md': '# Routing\n\nPages and layouts.\n',
      'docs/guides/alpha.md': '# Alpha\n\nNot in the list.\n',
      'docs/reference/cli.md': '# The netjes command\n\nEvery command.\n',
      'docs/reference/react-api.md': '# React API\n\nEvery export.\n',
      'src/Netjes/README.md': '# Netjes\n\nThe NuGet half.\n',
      'docs/decisions/README.md': '# Design decisions\n\nWhy it is built this way.\n',
      'docs/roadmap.md': '# Roadmap\n\nNow, next, later.\n',
      'CHANGELOG.md': '# Changelog\n\n## 0.0.1\n',
    });

    assert.equal((await run()).code, 0);

    // No docs/getting-started.md here: the list names it, and it is skipped.
    assert.deepEqual(sources(await read('llms-full.txt')), [
      'README.md',
      'docs/coming-from-nextjs.md',
      'docs/guides/routing.md',
      'docs/guides/testing.md',
      'docs/guides/alpha.md',
      'docs/reference/react-api.md',
      'docs/reference/cli.md',
      'packages/netjes/README.md',
      'src/Netjes/README.md',
      'docs/decisions/README.md',
      'CHANGELOG.md',
      'docs/roadmap.md',
    ]);
  });

  it('groups llms.txt into the docs, guides, reference, packages, skill and optional sections', async () => {
    await docs({
      ...minimal,
      'docs/getting-started.md': '# Getting started\n\nA tutorial.\n',
      'docs/guides/routing.md': '# Routing\n\nPages.\n',
      'docs/reference/options.md': '# Options\n\nEvery option.\n',
      'packages/netjes/skills/netjes/SKILL.md': '# netjes\n\nThe skill.\n',
      'docs/architecture.md': '# Architecture\n\nHow it works.\n',
    });

    await run();

    const index = await read('llms.txt');
    const titles = [...index.matchAll(/^## (.+)$/gm)].map((m) => m[1]);
    assert.deepEqual(titles, ['Docs', 'Guides', 'Reference', 'Packages', 'Skill for coding agents', 'Optional']);
    assert.ok(!index.includes('Recipes'));
  });

  it('writes llms.txt as an index: title, summary, then sections of links with a line each', async () => {
    await docs({
      ...minimal,
      'docs/getting-started.md': '# Getting started\n\nCreate an app, add a page. Then more.\n',
      // Not in the list, so its line is its first sentence.
      'docs/guides/sessions.md': '---\ntitle: ignored\n---\n# Sessions\n\nCookies in `app/`, as files. Then more.\n',
    });

    await run();

    const index = await read('llms.txt');
    assert.match(index, /^# Netjes\n\n> \S/);
    assert.ok(index.includes(`- [README](${RAW}README.md): What Netjes is and why`), 'a listed doc has the line the list gives');
    // Without a line in the list or the docs index, a doc's line is its first sentence.
    assert.ok(index.includes(`- [Getting started](${RAW}docs/getting-started.md): Create an app, add a page.\n`), index);
    assert.ok(index.includes(`## Guides\n\n- [Sessions](${RAW}docs/guides/sessions.md): Cookies in \`app/\`, as files.\n`), index);
    assert.ok(index.includes(`(${RAW}llms-full.txt)`));
    assert.ok(!index.includes('## Reference'), 'a section without documents is left out');
    assert.ok(index.indexOf('## Optional') === -1 || index.indexOf('## Optional') > index.indexOf('## Guides'));
  });

  it('leaves out the decision records and the docs index, and lists any other doc it was not told about', async () => {
    await docs({
      ...minimal,
      'docs/README.md': '# Netjes documentation\n\nEvery page.\n',
      'docs/decisions/README.md': '# Design decisions\n\nWhy it is built this way.\n',
      'docs/decisions/0001-react-renders-in-node-sidecars.md': '# 0001 – Sidecar\n\nStatus: accepted\n',
      'docs/faq.md': '# FAQ\n\nQuestions.\n',
    });

    await run();

    const full = await read('llms-full.txt');
    assert.ok(!full.includes('0001 – Sidecar'));
    assert.ok(!full.includes('Every page.'), 'llms.txt is the index');
    assert.ok(sources(full).includes('docs/faq.md'));
    const index = await read('llms.txt');
    assert.ok(index.includes(`- [FAQ](${RAW}docs/faq.md): Questions.`));
    assert.ok(index.indexOf(`(${RAW}docs/decisions/README.md)`) > index.indexOf('## Optional'), 'the decisions index is optional');
  });

  it('in a git checkout, takes only the docs git tracks, and says which it left out', async () => {
    await docs({
      ...minimal,
      'docs/guides/routing.md': '# Routing\n\nPages and layouts.\n',
      'docs/getting-started.md': '# Getting started\n\nNot added either.\n',
      'docs/guides/draft.md': '# Draft\n\nNot added yet.\n',
      'docs/notes.md': '# Notes\n\nLocal notes.\n',
      'docs/private/plan.md': '# Plan\n\nIgnored.\n',
      '.gitignore': 'docs/private/\n',
    });
    /** @param {string[]} args */
    const git = (...args) => execFileSync('git', ['-c', 'init.defaultBranch=main', ...args], { cwd: root, stdio: 'ignore' });
    git('init', '-q');
    git('add', '.gitignore', 'README.md', 'packages/netjes/README.md', 'docs/guides/routing.md');

    const { code, err } = await run();

    assert.equal(code, 0);
    const full = await read('llms-full.txt');
    assert.deepEqual(sources(full), ['README.md', 'docs/guides/routing.md', 'packages/netjes/README.md']);
    for (const text of ['Not added either.', 'Not added yet.', 'Local notes.', 'Ignored.']) assert.ok(!full.includes(text), text);
    assert.ok(!(await read('llms.txt')).includes('docs/notes.md'));
    assert.match(err, /docs\/getting-started\.md/);
    assert.match(err, /docs\/guides\/draft\.md/);
    assert.match(err, /docs\/notes\.md/);
    assert.match(err, /docs\/private\/plan\.md/);
    assert.match(err, /git does not track/);
  });

  it('lists the benchmarks and the architecture in llms.txt only, saying so, not in llms-full.txt', async () => {
    await docs({
      ...minimal,
      'docs/benchmarks.md': '# Benchmarks\n\nResult tables.\n',
      'docs/architecture.md': '# Architecture\n\nThe internals.\n',
    });

    await run();

    const index = await read('llms.txt');
    for (const file of ['docs/benchmarks.md', 'docs/architecture.md']) {
      assert.match(index, new RegExp(`\\(${RAW}${file}\\): .+ \\(not in llms-full\\.txt\\)\n`), file);
    }
    const full = await read('llms-full.txt');
    assert.ok(!sources(full).includes('docs/benchmarks.md'));
    assert.ok(!sources(full).includes('docs/architecture.md'));
    assert.ok(!full.includes('Result tables.'));
    assert.ok(!full.includes('The internals.'));
  });

  it("takes each page's line in llms.txt from its bullet in the docs index", async () => {
    await docs({
      ...minimal,
      'docs/README.md': [
        '# Netjes documentation',
        '',
        'Every page, with a line on what it covers.',
        '',
        '- [Routing](guides/routing.md): the `app/` file conventions and the client router.',
        '- [Changelog](../CHANGELOG.md): what each release contains.',
        '- [Benchmarks](benchmarks.md): how Netjes is benchmarked.',
        '- [netjes on npm](../packages/netjes/README.md), [Netjes on NuGet](../src/Netjes/README.md): what each package holds.',
        '',
      ].join('\n'),
      'docs/guides/routing.md': '# Routing\n\nPages and layouts.\n',
      'docs/benchmarks.md': '# Benchmarks\n\nResult tables.\n',
      'CHANGELOG.md': '# Changelog\n\nEvery release.\n',
      'src/Netjes/README.md': '# Netjes\n\nThe NuGet half.\n',
    });

    await run();

    const index = await read('llms.txt');
    assert.ok(index.includes(`- [Routing](${RAW}docs/guides/routing.md): The \`app/\` file conventions and the client router\n`), index);
    assert.ok(index.includes(`- [Changelog](${RAW}CHANGELOG.md): What each release contains\n`), index);
    assert.ok(index.includes(`(${RAW}docs/benchmarks.md): How Netjes is benchmarked (not in llms-full.txt)\n`), index);
    // A bullet with several links describes them together; each package keeps the line the list gives it.
    assert.ok(!index.includes('What each package holds'), index);
  });

  it('drops front matter and rewrites relative links to absolute URLs on main', async () => {
    await mkdir(path.join(root, 'examples', 'Blog'), { recursive: true });
    await docs({
      ...minimal,
      'docs/guides/links.md': [
        '---',
        'title: Links',
        'order: 3',
        '---',
        '# Links',
        '',
        'See [streaming](../architecture.md#streaming), [ADR 1](../decisions/0001-react-renders-in-node-sidecars.md "the first"), ![logo](img/logo.png),',
        '[the Blog](../../examples/Blog), [the README](/README.md), [a site](https://example.com/x.md), [above](#links),',
        'and `[code](kept.md)`, ``a `[b](kept.md)` c``, [mail](mailto:a@example.com).',
        'Code as link text: [`<Link>`](../reference/react-api.md#link), [``a ` b``](cli.md) and [`x` and `y`](../roadmap.md),',
        'next to code that only looks like one: `](kept.md)` \\`[escaped](../roadmap.md)\\`.',
        '',
        '```md',
        '[fenced](kept.md)',
        '```',
        '',
        '  ~~~',
        '  [tilde](kept.md)',
        '  ~~~',
        '',
        '[ref]: ../reference/options.md',
        '',
      ].join('\n'),
    });

    await run();

    const full = await read('llms-full.txt');
    assert.ok(!full.includes('order: 3'));
    for (const link of [
      `[streaming](${RAW}docs/architecture.md#streaming)`,
      `[ADR 1](${RAW}docs/decisions/0001-react-renders-in-node-sidecars.md "the first")`,
      `![logo](${RAW}docs/guides/img/logo.png)`,
      `[the Blog](${TREE}examples/Blog)`,
      `[the README](${RAW}README.md)`,
      '[a site](https://example.com/x.md)',
      '[above](#links)',
      '`[code](kept.md)`',
      '``a `[b](kept.md)` c``',
      '[mail](mailto:a@example.com)',
      `[\`<Link>\`](${RAW}docs/reference/react-api.md#link)`,
      `[\`\`a \` b\`\`](${RAW}docs/guides/cli.md)`,
      `[\`x\` and \`y\`](${RAW}docs/roadmap.md)`,
      '`](kept.md)`',
      `\\\`[escaped](${RAW}docs/roadmap.md)\\\``,
      '[fenced](kept.md)',
      '[tilde](kept.md)',
      `[ref]: ${RAW}docs/reference/options.md`,
    ]) {
      assert.ok(full.includes(link), `expected ${link} in:\n${full}`);
    }
  });

  it("leaves no relative link in the repository's llms-full.txt", async () => {
    const { files } = await render(REPOSITORY_ROOT);

    // Outside fenced code, with inline code blanked out: what is left of a link must be absolute.
    let fenced = false;
    const relative = files['llms-full.txt'].split('\n').flatMap((line, i) => {
      if (/^\s*(```|~~~)/.test(line)) fenced = !fenced;
      if (fenced) return [];
      const prose = line.replace(/\\`/g, '  ').replace(/(`+)(?!`)[\s\S]*?(?<!`)\1(?!`)/g, (code) => ' '.repeat(code.length));
      return [...prose.matchAll(/\]\(([^)\s]+)/g)]
        .map((match) => match[1] ?? '')
        .filter((target) => !/^([a-z][a-z0-9+.-]*:|#)/i.test(target))
        .map((target) => `${i + 1}: ${target}`);
    });
    assert.deepEqual(relative, []);
  });

  it('--check fails while the files are missing or stale, and passes once they are written', async () => {
    await docs(minimal);

    const missing = await run(['--check']);
    assert.equal(missing.code, 1);
    assert.match(missing.err, /llms\.txt/);
    assert.match(missing.err, /llms-full\.txt/);
    assert.match(missing.err, /npm run docs:llms/);

    await run();
    assert.deepEqual(await run(['--check']), { code: 0, out: 'llms.txt and llms-full.txt are up to date\n', err: '' });

    await docs({ 'packages/netjes/README.md': '# netjes\n\nThe npm half, changed.\n' });
    const stale = await run(['--check']);
    assert.equal(stale.code, 1);
    assert.match(stale.err, /llms-full\.txt/);
  });

  it('--check accepts files checked out with CRLF line endings', async () => {
    await docs(minimal);
    await run();
    for (const file of ['llms.txt', 'llms-full.txt']) {
      await writeFile(path.join(root, file), (await read(file)).replaceAll('\n', '\r\n'));
    }

    assert.equal((await run(['--check'])).code, 0);
  });

  it('writes nothing in --check mode', async () => {
    await docs(minimal);

    await run(['--check']);

    await assert.rejects(read('llms.txt'), { code: 'ENOENT' });
  });
});
