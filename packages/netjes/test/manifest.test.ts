// @vitest-environment node
import { execSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const manifest = JSON.parse(await readFile(fileURLToPath(new URL('../package.json', import.meta.url)), 'utf8'));

describe('the npm manifest', () => {
  it('names the repository, so registry provenance can match it', () => {
    expect(manifest.repository).toEqual({
      type: 'git',
      url: 'git+https://github.com/DennisVerhoeven/netjes.git',
      directory: 'packages/netjes',
    });
    expect(manifest.homepage).toBe('https://github.com/DennisVerhoeven/netjes#readme');
    expect(manifest.bugs).toEqual({ url: 'https://github.com/DennisVerhoeven/netjes/issues' });
  });

  it('is published publicly under MIT, for the Node.js releases its dependencies support', () => {
    expect(manifest.license).toBe('MIT');
    expect(manifest.author).toContain('Dennis Verhoeven');
    expect(manifest.publishConfig).toEqual({ access: 'public' });
    expect(manifest.engines).toEqual({ node: '^22.22.2 || ^24.15.0 || >=26.0.0' });
    expect(manifest.version).toMatch(/^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$/);
  });

  it('describes itself with factual keywords', () => {
    expect(manifest.description).toBeTruthy();
    expect(manifest.keywords).toEqual(expect.arrayContaining(['react', 'aspnetcore', 'dotnet', 'ssr', 'vite']));
  });

  it('ships the compiled modules, their sources, tooling and template, but no tests', () => {
    expect(manifest.files).toEqual(expect.arrayContaining(['dist', 'src', 'vite', 'sidecar', 'bin', 'cli', 'template']));
    expect(manifest.files).not.toContain('test');
  });

  it('exports compiled JavaScript with its declarations, so an app type-checks declarations, not sources', () => {
    const { './vite': plugin, './sidecar': sidecar, ...modules } = manifest.exports as Record<string, unknown>;
    expect(plugin).toEqual({ types: './vite/plugin.d.ts', default: './vite/plugin.mjs' });
    expect(sidecar).toBe('./sidecar/server.mjs');
    expect(Object.keys(modules).sort()).toEqual(['.', './client-entry', './server-entry', './testing', './testing/setup', './vitals', './zod']);
    for (const [entry, target] of Object.entries(modules)) {
      const file = /^\.\/dist\/([\w-]+)\.js$/.exec((target as { default?: string }).default ?? '')?.[1];
      expect(file, entry).toBeDefined();
      expect(target, entry).toEqual({ types: `./dist/${file}.d.ts`, default: `./dist/${file}.js` });
    }
  });

  describe('the packed tarball', () => {
    const packageDir = fileURLToPath(new URL('..', import.meta.url));
    // Through a shell: on Windows npm is npm.cmd, which Node starts only that way.
    const packed: { files: { path: string }[] } = JSON.parse(
      execSync('npm pack --dry-run --json --ignore-scripts', { cwd: packageDir, encoding: 'utf8' }),
    )[0];
    const paths = packed.files.map((f) => f.path);

    it('holds what a new app needs', () => {
      expect(paths).toEqual(
        expect.arrayContaining([
          'package.json',
          'README.md',
          'LICENSE',
          'bin/netjes.mjs',
          'sidecar/server.mjs',
          'vite/plugin.mjs',
          'template/_gitignore',
          'template/__NAME__.csproj',
          'template/Properties/launchSettings.json',
        ]),
      );
    });

    it('holds the compiled module and the declarations of every entry point (npm run build writes them)', () => {
      const files = Object.values(manifest.exports as Record<string, string | Record<string, string>>).flatMap((target) =>
        typeof target === 'string' ? [target] : Object.values(target),
      );
      expect(paths).toEqual(expect.arrayContaining(files.map((file) => file.slice(2))));
    });

    it('holds no tests of its own, and maps only to the sources it holds', async () => {
      expect(paths.filter((p) => /(^|\/)test\//.test(p) && !p.startsWith('template/'))).toEqual([]);
      expect(paths.filter((p) => /\.(test|spec)\.[cm]?[jt]sx?$/.test(p) && !p.startsWith('template/'))).toEqual([]);
      const maps = paths.filter((p) => p.endsWith('.map'));
      expect(maps.length).toBeGreaterThan(0);
      for (const map of maps) {
        expect(map, map).toMatch(/^dist\/[\w-]+\.(d\.ts|js)\.map$/);
        const { sources } = JSON.parse(await readFile(path.join(packageDir, map), 'utf8')) as { sources: string[] };
        for (const source of sources) expect(paths, `${map}: ${source}`).toContain(path.posix.join('dist', source));
      }
    });

    it('carries the same licence text as the repository root', async () => {
      const own = await readFile(new URL('../LICENSE', import.meta.url), 'utf8');
      expect(own).toContain('MIT License');
      expect(own).toContain('Copyright (c) 2026 Dennis Verhoeven');
      expect(own).toBe(await readFile(new URL('../../../LICENSE', import.meta.url), 'utf8'));
    });

    it('holds declarations that import only each other, the dependencies, the peer dependencies and Node.js', async () => {
      const allowed = new Set([...Object.keys(manifest.dependencies), ...Object.keys(manifest.peerDependencies)]);
      const declarations = paths.filter((p) => p.startsWith('dist/') && p.endsWith('.d.ts'));
      expect(declarations.length).toBeGreaterThan(0);
      for (const declaration of declarations) {
        // Comments may hold examples, such as import('./Chart.tsx').
        const code = (await readFile(path.join(packageDir, declaration), 'utf8')).replace(/\/\*[\s\S]*?\*\/|\/\/.*$/gm, '');
        for (const [, specifier = ''] of code.matchAll(/(?:\bfrom|\bimport\s*\(?)\s*["']([^"']+)["']/g)) {
          if (specifier.startsWith('.')) {
            const target = path.posix.join(path.posix.dirname(declaration), specifier.replace(/\.(tsx?|jsx?)$/, '.d.ts'));
            expect(paths, `${declaration}: ${specifier}`).toContain(target);
          } else if (!specifier.startsWith('node:')) {
            const name = specifier.split('/').slice(0, specifier.startsWith('@') ? 2 : 1).join('/');
            expect(allowed, `${declaration}: ${specifier}`).toContain(name);
          }
        }
      }
    });
  });
});
