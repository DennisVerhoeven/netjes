// @ts-check
// The checkout's line endings: node --test scripts/test/line-endings.test.mjs
// LF on every system CI runs on: the CLI fills in the app template by matching its lines, and tests compare and hash
// text from the checkout. Git for Windows checks text files out with CRLF unless .gitattributes says otherwise.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';

const ROOT = fileURLToPath(new URL('../..', import.meta.url));

/**
 * What `git ls-files --eol` reports for each tracked file: its line endings in the index and in the working tree
 * (lf, crlf, mixed, none, -text for binary, empty when the file is missing) and its attributes. Null outside a git
 * checkout. The `GIT_` variables a git hook sets would point git at another checkout, so they are left out.
 * @returns {{ file: string; index: string; worktree: string; attributes: string }[] | null}
 */
function lineEndings() {
  const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => !key.toUpperCase().startsWith('GIT_')));
  let output;
  try {
    output = execFileSync('git', ['ls-files', '--eol', '-z'], { cwd: ROOT, env, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
  } catch {
    return null;
  }
  return output
    .split('\0')
    .filter(Boolean)
    .map((entry) => {
      const tab = entry.indexOf('\t');
      const match = /^i\/(\S*)\s+w\/(\S*)\s+attr\/(.*)$/.exec(entry.slice(0, tab));
      if (!match) throw new Error(`Unexpected git ls-files --eol output: ${entry}`);
      return { file: entry.slice(tab + 1), index: match[1] ?? '', worktree: match[2] ?? '', attributes: (match[3] ?? '').trim() };
    });
}

const files = lineEndings();
const text = (files ?? []).filter((entry) => entry.index !== '-text');

describe('the checkout', { skip: files === null && 'not a git checkout' }, () => {
  it('has git check every text file out with LF line endings, whatever core.autocrlf says', () => {
    const unpinned = text.filter((entry) => !/(^|\s)eol=lf(\s|$)/.test(entry.attributes)).map((entry) => entry.file);

    assert.deepEqual(unpinned, [], 'Give these eol=lf in .gitattributes');
  });

  it('has LF line endings in every text file on disk', () => {
    const crlf = text.filter((entry) => entry.worktree === 'crlf' || entry.worktree === 'mixed').map((entry) => entry.file);

    assert.deepEqual(crlf, [], 'Convert these to LF, as .gitattributes and .editorconfig ask');
  });
});
