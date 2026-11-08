// @vitest-environment node
// A page package's generated file and the file of an app that mounts it, pinned by
// tests/Netjes.Tests/TypeGen/GoldenFileTests.cs: what the package's file checks and builds, and that the two type-check
// in one program, which two apps' files do not.
import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { endpointUrl, Href, isHref, pages, route } from './fixtures/typegen/mounted/package.generated.ts';

const FIXTURE = fileURLToPath(new URL('./fixtures/typegen/mounted/', import.meta.url));
const TSC = path.join(path.dirname(createRequire(import.meta.url).resolve('typescript/package.json')), 'bin', 'tsc');

function typecheck(config: string): { status: number | null; output: string } {
  const result = spawnSync(process.execPath, [TSC, '-p', path.join(FIXTURE, config), '--pretty', 'false'], { encoding: 'utf8' });
  return { status: result.status, output: `${result.stdout}${result.stderr}` };
}

describe("a page package's generated file", () => {
  it('checks the shape of a URL from data, which is a URL of the app with the mount in it', () => {
    for (const url of ['/help/articles', '/help/articles/a?page=2#top', '?page=2', '#top', 'https://example.com/a', 'mailto:help@example.com']) {
      expect(isHref(url), url).toBe(true);
      expect(Href.safeParse(url).success, url).toBe(true);
    }
  });

  it('refuses a URL that leaves the app or is no link', () => {
    for (const url of ['//x', '//evil.example/help', '/\\evil.example', 'javascript:alert(1)', 'data:text/html,x', ' /help', '/help\n', 'help']) {
      expect(isHref(url), url).toBe(false);
      expect(Href.safeParse(url).success, url).toBe(false);
    }
  });

  it("parses a page's props with that check", () => {
    expect(pages['/articles/[slug]'].parse({ slug: 'a', title: 'A', back: '/help' }).back).toBe('/help');
    expect(pages['/articles/[slug]'].safeParse({ slug: 'a', title: 'A', back: '//evil.example' }).success).toBe(false);
  });

  it('builds the URLs of pages and endpoints below the mount', () => {
    expect(route('/')).toBe('/');
    expect(route('/articles/[slug]', { slug: 'a b/c' }, { page: 2 })).toBe('/articles/a%20b%2Fc?page=2');
    expect(endpointUrl('GET /articles/{slug}/feed', { slug: 'a', take: 5 })).toBe('/articles/a/feed?take=5');
    expect(endpointUrl('POST /feedback')).toBe('/feedback');
  });
});

describe("an app's file and a page package's file in one program", () => {
  it('type-check under the strictest flags, with no conflict over the Href that netjes registers', () => {
    const { status, output } = typecheck('tsconfig.json');

    expect(output).toBe('');
    expect(status).toBe(0);
  });

  it("type-check under create-vite's flags", () => {
    const { status, output } = typecheck('tsconfig.create-vite.json');

    expect(output).toBe('');
    expect(status).toBe(0);
  });

  it("conflict when both register an Href, as two apps' files do", () => {
    const { status, output } = typecheck('tsconfig.two-apps.json');

    expect(output).toContain('error TS2717');
    expect(status).not.toBe(0);
  });
});
