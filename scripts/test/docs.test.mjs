// @ts-check
// Checks on the repository's docs that the link check cannot make: node --test scripts/test/docs.test.mjs
import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import { describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';

const ROOT = fileURLToPath(new URL('../..', import.meta.url));
const DECISIONS = 'docs/decisions';

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
