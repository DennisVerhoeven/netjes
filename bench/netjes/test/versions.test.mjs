import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { after, test } from 'node:test';
import { gitCommit, packageVersions } from '../lib/versions.mjs';

/** @type {string[]} */
const dirs = [];
after(() => Promise.all(dirs.map((d) => rm(d, { recursive: true, force: true }))));

async function tempDir() {
  const dir = await mkdtemp(path.join(os.tmpdir(), 'netjes-bench-'));
  dirs.push(dir);
  return dir;
}

test('package versions are those a directory resolves, from its node_modules or a parent’s', async () => {
  const root = await tempDir();
  await mkdir(path.join(root, 'node_modules', 'react'), { recursive: true });
  await writeFile(path.join(root, 'node_modules', 'react', 'package.json'), JSON.stringify({ name: 'react', version: '19.3.1' }));
  await mkdir(path.join(root, 'node_modules', '@scope', 'pkg'), { recursive: true });
  await writeFile(path.join(root, 'node_modules', '@scope', 'pkg', 'package.json'), JSON.stringify({ version: '1.2.3' }));
  const app = path.join(root, 'bench', 'app');
  await mkdir(app, { recursive: true });
  await writeFile(path.join(app, 'package.json'), '{}');
  assert.deepEqual(await packageVersions(app, ['react', '@scope/pkg', 'missing']), { react: '19.3.1', '@scope/pkg': '1.2.3' });
});

test('the commit is short, and marked when the tree has changes', async () => {
  const repo = await tempDir();
  const git = (/** @type {string[]} */ ...args) =>
    execFileSync('git', ['-c', 'user.name=t', '-c', 'user.email=t@example.com', '-c', 'commit.gpgsign=false', ...args], { cwd: repo }).toString().trim();
  git('init', '-q');
  await writeFile(path.join(repo, 'a.txt'), 'a');
  git('add', 'a.txt');
  git('commit', '-q', '-m', 'a');
  const head = git('rev-parse', '--short', 'HEAD');
  assert.equal(await gitCommit(repo), head);
  await writeFile(path.join(repo, 'a.txt'), 'b');
  assert.equal(await gitCommit(repo), `${head}+changes`);
  assert.equal(await gitCommit(await tempDir()), null);
});
