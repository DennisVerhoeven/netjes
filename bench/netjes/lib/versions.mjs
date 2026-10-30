import { execFile } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { promisify } from 'node:util';

const run = promisify(execFile);

/**
 * The installed version of each package, found the way Node resolves it from a directory: in its node_modules or
 * the nearest parent's.
 * @param {string} fromDir
 * @param {string[]} names
 * @returns {Promise<Record<string, string>>} missing packages are left out
 */
export async function packageVersions(fromDir, names) {
  /** @type {Record<string, string>} */
  const versions = {};
  for (const name of names) {
    for (let dir = path.resolve(fromDir); ; dir = path.dirname(dir)) {
      try {
        const manifest = JSON.parse(await readFile(path.join(dir, 'node_modules', name, 'package.json'), 'utf8'));
        if (typeof manifest.version === 'string') versions[name] = manifest.version;
        break;
      } catch {
        if (path.dirname(dir) === dir) break;
      }
    }
  }
  return versions;
}

/**
 * The checkout's commit, short, with `+changes` when the working tree differs from it.
 * @param {string} dir
 * @returns {Promise<string | null>}
 */
export async function gitCommit(dir) {
  try {
    const head = (await run('git', ['-C', dir, 'rev-parse', '--short', 'HEAD'])).stdout.trim();
    const status = (await run('git', ['-C', dir, 'status', '--porcelain'])).stdout.trim();
    return status === '' ? head : `${head}+changes`;
  } catch {
    return null;
  }
}
