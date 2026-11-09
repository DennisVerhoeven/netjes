import { cp, mkdir, mkdtemp, readFile, realpath, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const FIXTURES = fileURLToPath(new URL('./fixtures/page-packages/', import.meta.url));
const NODE_MODULES = fileURLToPath(new URL('../../../node_modules', import.meta.url));

/** The names of the fixture page packages, as an app lists them in netjes({ packages }). */
export const INSTALLED = '@netjes-test/installed';
export const LINKED = '@netjes-test/linked';
/** A copy of the installed one under another name, which the app's Vite config does not list. */
export const UNLISTED = '@netjes-test/unlisted';

export type PackagesApp = {
  /** The app's folder, the Vite root, by its real path. */
  root: string;
  /** The real path of each package's folder. */
  installed: string;
  linked: string;
  /** Writes a file, by its path relative to `dir` (the app's folder by default). */
  write(file: string, content: string, dir?: string): Promise<void>;
  remove(): Promise<void>;
};

/**
 * An app with the fixture page packages laid out as npm lays them out:
 *
 * - `@netjes-test/installed` (fixtures/page-packages/installed) is copied into the app's node_modules, as `npm install`
 *   puts it there. Its pages are in `app/`, its sources the defaults (`app` and `src`), and it has a stylesheet (`css`).
 * - `@netjes-test/linked` (fixtures/page-packages/linked) is copied to a folder of its own, outside the app and its
 *   workspace root, with no node_modules above it, and linked into the app's node_modules, as a workspace or `npm link`
 *   links a package. Its pages are in `pages/`, and its sources are `pages` and `lib`.
 * - `@netjes-test/unlisted` is a copy of the installed one, which the tests leave out of netjes({ packages }).
 *
 * Every other package comes from this workspace's node_modules, through a link in the folder above the app. The links
 * are junctions on Windows, as npm makes them there, which need no administrator rights or developer mode. Paths are
 * real paths, as Vite names modules: on macOS the temporary folder is under a symlink (/var is /private/var).
 */
export async function createPackagesApp(): Promise<PackagesApp> {
  const base = await realpath(await mkdtemp(path.join(tmpdir(), 'netjes-packages-')));
  const linked = await realpath(await mkdtemp(path.join(tmpdir(), 'netjes-linked-')));
  const root = path.join(base, 'site');
  const scope = path.join(root, 'node_modules', '@netjes-test');
  const installed = path.join(scope, 'installed');
  await mkdir(scope, { recursive: true });
  await symlink(NODE_MODULES, path.join(base, 'node_modules'), 'junction');
  await cp(path.join(FIXTURES, 'installed'), installed, { recursive: true });
  await cp(path.join(FIXTURES, 'linked'), linked, { recursive: true });
  await symlink(linked, path.join(scope, 'linked'), 'junction');
  const unlisted = path.join(scope, 'unlisted');
  await cp(path.join(FIXTURES, 'installed'), unlisted, { recursive: true });
  const manifest = JSON.parse(await readFile(path.join(unlisted, 'package.json'), 'utf8')) as Record<string, unknown>;
  await writeFile(path.join(unlisted, 'package.json'), `${JSON.stringify({ ...manifest, name: UNLISTED }, null, 2)}\n`);
  await writeFile(path.join(root, 'package.json'), '{ "name": "site", "private": true, "type": "module" }\n');
  return {
    root,
    installed,
    linked,
    async write(file, content, dir = root) {
      await mkdir(path.dirname(path.join(dir, file)), { recursive: true });
      await writeFile(path.join(dir, file), content);
    },
    async remove() {
      await rm(base, { recursive: true, force: true });
      await rm(linked, { recursive: true, force: true });
    },
  };
}
