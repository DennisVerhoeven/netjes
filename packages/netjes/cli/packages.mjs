// @ts-check
import { existsSync } from 'node:fs';
import path from 'node:path';

/**
 * Where a package is installed for the app: its package.json in the nearest node_modules, as Node and the app's
 * sidecar resolve it. Read directly rather than through require.resolve, which only finds package.json when the
 * package exports it.
 * @param {string} cwd
 * @param {string} name
 * @returns {string | null}
 */
export function findPackage(cwd, name) {
  for (let dir = path.resolve(cwd); ; dir = path.dirname(dir)) {
    const manifest = path.join(dir, 'node_modules', name, 'package.json');
    if (existsSync(manifest)) return manifest;
    if (path.dirname(dir) === dir) return null;
  }
}
