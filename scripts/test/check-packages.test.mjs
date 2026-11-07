// @ts-check
// The package check's browser install: node --test scripts/test/check-packages.test.mjs
// scripts/check-packages.sh installs Chromium for the Playwright suite of the app it scaffolds, whose @playwright/test is
// the newest release in the template's range rather than the version this repository's lockfile pins, so it can need
// another Chromium. Each `playwright install` removes every browser in the shared cache that no Playwright it knows of
// still uses, and it knows only the folders that ran an install themselves and are still there: the scaffold is a
// temporary folder, and a worktree is removed once its branch merges. Unless the install skips that clean-up, a package
// check can remove the Chromium that the examples' end-to-end suites launch.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';

const SCRIPT = fileURLToPath(new URL('../check-packages.sh', import.meta.url));

describe('the package check', () => {
  it("installs the scaffolded app's Chromium without removing the browsers that other checkouts use", async () => {
    const installs = (await readFile(SCRIPT, 'utf8'))
      .split('\n')
      .filter((line) => /\bplaywright install\b/.test(line) && !/^\s*#/.test(line));

    assert.notDeepEqual(installs, [], 'check-packages.sh installs no browser any more: remove this test');
    assert.deepEqual(
      installs.filter((line) => !/(^|\s)PLAYWRIGHT_SKIP_BROWSER_GC=1\s/.test(line)),
      [],
      'Run these with PLAYWRIGHT_SKIP_BROWSER_GC=1',
    );
  });
});
