// @ts-check
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, mkdir, readFile, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { after, describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';
import { checkPackages, checkSources, parseTag, releaseNotes, VERSION_PINS } from '../release.mjs';

const SCRIPT = fileURLToPath(new URL('../release.mjs', import.meta.url));
const ROOT = fileURLToPath(new URL('../..', import.meta.url));

/** @type {string[]} */
const temps = [];
after(() => Promise.all(temps.map((dir) => rm(dir, { recursive: true, force: true }))));

/** @param {Record<string, string>} files */
async function tree(files) {
  const dir = await mkdtemp(path.join(tmpdir(), 'netjes-release-'));
  temps.push(dir);
  for (const [name, content] of Object.entries(files)) {
    await mkdir(path.dirname(path.join(dir, name)), { recursive: true });
    await writeFile(path.join(dir, name), content);
  }
  return dir;
}

const CHANGELOG = `# Changelog

## [Unreleased]

## [0.0.1] - 2026-10-29

### Added

- The first release.

[Unreleased]: https://github.com/owner/netjes/compare/v0.0.1...HEAD
[0.0.1]: https://github.com/owner/netjes/releases/tag/v0.0.1
`;

/**
 * The files that pin a version for the docs, each holding every pinned text at `version`.
 * @param {string} version
 * @returns {Record<string, string>}
 */
function pinnedDocs(version) {
  /** @type {Record<string, string>} */
  const files = {};
  for (const pin of VERSION_PINS) {
    files[pin.file] = `${files[pin.file] ?? ''}${pin.text.replaceAll('{version}', version)}\n`;
  }
  return files;
}

/** A repository whose package sources and pinned docs all carry `version`; `files` replaces or adds files. */
function repo(version = '0.0.1', files = {}) {
  return tree({
    'packages/netjes/package.json': JSON.stringify({
      name: 'netjes',
      version,
      repository: { type: 'git', url: 'git+https://github.com/owner/netjes.git' },
    }),
    'package-lock.json': JSON.stringify({ packages: { '': {}, 'packages/netjes': { name: 'netjes', version } } }),
    'Directory.Build.props': `<Project>\n  <PropertyGroup>\n    <Version>${version}</Version>\n  </PropertyGroup>\n</Project>\n`,
    'CHANGELOG.md': CHANGELOG,
    ...pinnedDocs(version),
    ...files,
  });
}

describe('parseTag', () => {
  it('reads the version from a v-prefixed tag', () => {
    assert.deepEqual(parseTag('v0.0.1'), { version: '0.0.1', prerelease: false });
  });

  it('marks a version with a prerelease label', () => {
    assert.deepEqual(parseTag('v0.1.0-rc.1'), { version: '0.1.0-rc.1', prerelease: true });
  });

  for (const tag of ['0.0.1', 'v0.0', 'v0.0.1.2', 'v01.0.0', 'v0.0.1+build.5', 'v0.0.1-', 'V0.0.1', 'v0.0.1\n', 'release-0.0.1']) {
    it(`rejects ${JSON.stringify(tag)}`, () => {
      assert.throws(() => parseTag(tag), /is not v<major>\.<minor>\.<patch>/);
    });
  }
});

describe('checkSources', () => {
  it('passes when every source carries the version and the changelog has its entry', async () => {
    assert.deepEqual(await checkSources(await repo(), '0.0.1'), []);
  });

  it('names each source whose version differs', async () => {
    const root = await repo('0.0.1', {
      'Directory.Build.props': '<Project><PropertyGroup><Version>0.1.0</Version></PropertyGroup></Project>',
      'package-lock.json': JSON.stringify({ packages: { 'packages/netjes': { version: '0.0.2' } } }),
    });
    assert.deepEqual(await checkSources(root, '0.0.1'), [
      'package-lock.json (packages/netjes) has version 0.0.2, not 0.0.1.',
      'Directory.Build.props has version 0.1.0, not 0.0.1.',
    ]);
  });

  it('names each pinned doc version that differs, and each pin that is gone', async () => {
    const pin = VERSION_PINS.find((candidate) => candidate.file === 'packages/netjes/README.md');
    assert.ok(pin);
    const root = await repo('0.0.1', {
      'packages/netjes/README.md': `Install ${pin.text.replaceAll('{version}', '0.0.0')} and ${pin.text.replaceAll('{version}', '0.0.1')}.\n`,
      'docs/reference/cli.md': 'No version here.\n',
    });
    const errors = await checkSources(root, '0.0.1');
    assert.ok(errors.includes(`packages/netjes/README.md pins 0.0.0 in "${pin.text.replaceAll('{version}', '0.0.0')}", not 0.0.1.`), errors.join('\n'));
    assert.equal(errors.filter((error) => error.startsWith('packages/netjes/README.md')).length, 1);
    assert.ok(errors.some((error) => error.startsWith('docs/reference/cli.md has no "')), errors.join('\n'));
  });

  it('compares every version of a pin that names the version twice', async () => {
    const pin = VERSION_PINS.find((candidate) => candidate.text.split('{version}').length > 2);
    assert.ok(pin);
    const [first = '', second = '', rest = ''] = pin.text.split('{version}');
    const stale = `${first}0.0.1${second}0.0.0${rest}`;
    const root = await repo('0.0.1', { [pin.file]: `${pinnedDocs('0.0.1')[pin.file]}${stale}\n` });
    const errors = (await checkSources(root, '0.0.1')).filter((error) => error.startsWith(`${pin.file} pins`));
    assert.equal(errors.length, 1, errors.join('\n'));
    assert.ok(errors[0]?.includes('0.0.0'), errors.join('\n'));
  });

  it('reports a pinned doc that is missing', async () => {
    const root = await repo();
    await rm(path.join(root, 'README.md'));
    assert.ok((await checkSources(root, '0.0.1')).some((error) => error.startsWith('README.md has no "')));
  });

  it('finds the pins of this repository at the version it is at', async () => {
    const manifest = JSON.parse(await readFile(path.join(ROOT, 'packages/netjes/package.json'), 'utf8'));
    assert.deepEqual(await checkSources(ROOT, manifest.version), []);
  });

  it('needs a changelog entry for the version', async () => {
    assert.deepEqual(await checkSources(await repo('0.0.2', { 'CHANGELOG.md': CHANGELOG }), '0.0.2'), [
      'CHANGELOG.md has no entry for 0.0.2.',
    ]);
    const root = await repo('0.0.1');
    await rm(path.join(root, 'CHANGELOG.md'));
    assert.deepEqual(await checkSources(root, '0.0.1'), ['CHANGELOG.md has no entry for 0.0.1.']);
  });

  // npm rejects a provenance statement whose repository differs from package.json's, after the NuGet packages are out.
  it('checks the npm package names the repository it is released from', async () => {
    const root = await repo();
    assert.deepEqual(await checkSources(root, '0.0.1', { repository: 'Owner/Netjes' }), []);
    assert.deepEqual(await checkSources(root, '0.0.1', { repository: 'someone/netjes' }), [
      'packages/netjes/package.json names repository git+https://github.com/owner/netjes.git, not https://github.com/someone/netjes.',
    ]);
    const bare = await repo('0.0.1', { 'packages/netjes/package.json': JSON.stringify({ name: 'netjes', version: '0.0.1' }) });
    assert.deepEqual(await checkSources(bare, '0.0.1', { repository: 'owner/netjes' }), [
      'packages/netjes/package.json names repository (none), not https://github.com/owner/netjes.',
    ]);
  });
});

describe('releaseNotes', () => {
  it('is the entry for the version, without the links to compare versions', () => {
    assert.equal(releaseNotes(CHANGELOG, '0.0.1'), '### Added\n\n- The first release.');
  });

  it('stops at the next version and keeps the entry\'s own links', () => {
    const changelog = `## [0.0.2] - 2026-11-05\n\n- Fixed [#3].\n\n[#3]: https://example.org/3\n\n## [0.0.1] - 2026-10-29\n\n- First.\n`;
    assert.equal(releaseNotes(changelog, '0.0.2'), '- Fixed [#3].\n\n[#3]: https://example.org/3');
    assert.equal(releaseNotes(changelog, '0.0.1'), '- First.');
  });

  it('matches the whole version', () => {
    assert.equal(releaseNotes('## [0.0.10] - 2026-12-01\n\n- Later.\n', '0.0.1'), null);
    assert.equal(releaseNotes('## [0.0.1-rc.1]\n\n- Candidate.\n', '0.0.1'), null);
    assert.equal(releaseNotes('## 0.0.1\n\n- Plain heading.\n', '0.0.1'), '- Plain heading.');
  });

  it('is null for a missing or empty entry', () => {
    assert.equal(releaseNotes(CHANGELOG, '9.9.9'), null);
    assert.equal(releaseNotes('## [0.0.1]\n\n## [0.0.0]\n\n- Older.\n', '0.0.1'), null);
  });
});

describe('checkPackages', () => {
  /** @param {string} version */
  const packages = (version) => ({
    [`netjes-${version}.tgz`]: '',
    [`Netjes.${version}.nupkg`]: '',
    [`Netjes.Images.SkiaSharp.${version}.nupkg`]: '',
  });

  it('passes for exactly the three packages of the version', async () => {
    assert.deepEqual(await checkPackages(await tree(packages('0.0.1')), '0.0.1'), []);
  });

  it('names the missing and the unexpected files', async () => {
    const dir = await tree({ ...packages('0.1.0'), 'Netjes.0.0.1.nupkg': '' });
    assert.deepEqual(await checkPackages(dir, '0.0.1'), [
      'Missing netjes-0.0.1.tgz.',
      'Missing Netjes.Images.SkiaSharp.0.0.1.nupkg.',
      'Unexpected Netjes.0.1.0.nupkg.',
      'Unexpected Netjes.Images.SkiaSharp.0.1.0.nupkg.',
      'Unexpected netjes-0.1.0.tgz.',
    ]);
  });
});

describe('the command line', () => {
  /** @param {string[]} args */
  const run = (...args) => spawnSync(process.execPath, [SCRIPT, ...args], { encoding: 'utf8' });

  it('check prints the version and whether it is a prerelease, for GITHUB_OUTPUT', async () => {
    const result = run('check', 'v0.0.1', '--root', await repo(), '--repository', 'owner/netjes');
    assert.equal(result.stderr, '');
    assert.equal(result.stdout, 'version=0.0.1\nprerelease=false\n');
    assert.equal(result.status, 0);
  });

  it('check fails with every problem it finds', async () => {
    const result = run('check', 'v0.0.2', '--root', await repo());
    assert.equal(result.status, 1);
    assert.equal(result.stdout, '');
    assert.match(result.stderr, /packages\/netjes\/package\.json has version 0\.0\.1, not 0\.0\.2\./);
    assert.match(result.stderr, /CHANGELOG\.md has no entry for 0\.0\.2\./);
  });

  it('check rejects a tag that is not a version', async () => {
    const result = run('check', 'main', '--root', await repo());
    assert.equal(result.status, 1);
    assert.match(result.stderr, /Tag "main" is not v<major>/);
  });

  it('packages fails on a version mismatch', async () => {
    const result = run('packages', '0.0.2', await tree({ 'netjes-0.0.1.tgz': '' }));
    assert.equal(result.status, 1);
    assert.match(result.stderr, /Missing netjes-0\.0\.2\.tgz\./);
  });

  it('notes prints the changelog entry', async () => {
    const result = run('notes', '0.0.1', '--root', await repo());
    assert.equal(result.stdout, '### Added\n\n- The first release.\n');
    assert.equal(result.status, 0);
  });

  it('explains its usage', () => {
    const result = run('publish');
    assert.equal(result.status, 2);
    assert.match(result.stderr, /Usage:/);
  });

  it('says what check --repository checks', () => {
    const result = run();
    assert.match(result.stderr, /with\s+--repository, that packages\/netjes\/package\.json names that GitHub repository/);
  });

  // Node resolves symlinks in the script's own URL but not in process.argv[1].
  it('runs when it is started through a symlink', async () => {
    const link = path.join(await tree({}), 'scripts');
    await symlink(path.dirname(SCRIPT), link, 'junction');
    const result = spawnSync(process.execPath, [path.join(link, 'release.mjs'), 'publish'], { encoding: 'utf8' });
    assert.equal(result.status, 2);
    assert.match(result.stderr, /Usage:/);
  });
});
