import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { type Page, expect, test } from '@playwright/test';
import { markWindow, watchForErrors, watchForViolations } from './helpers.ts';

/**
 * The Blog as `netjes export` writes it, served from the files as a static host would: the export runs once, on the
 * build the production server made (`npm run build`, and `dotnet build` from `npm run test:e2e`), and each page is
 * then answered from the output by Playwright's router, so nothing but the files is behind it.
 */
const app = fileURLToPath(new URL('..', import.meta.url));
const output = path.join(app, 'test-results', 'export');
const SITE = 'http://export.netjes.test';
const REPORT = 'Report ready: 42 items, from a deliberately slow C# task.';

/** What the export logged, its standard output and error as they came. */
let exportLog = '';

/** What the CLI's `netjes export --no-build` runs, on the Debug build the other servers use; returns what it logged. */
async function exportBlog(): Promise<string> {
  const env: NodeJS.ProcessEnv = {
    ...process.env,
    ASPNETCORE_ENVIRONMENT: 'Production',
    // Any free port unless set, like the CLI.
    ASPNETCORE_URLS: `http://127.0.0.1:${process.env.E2E_EXPORT_PORT ?? '0'}`,
    NETJES_COMMAND: 'export',
    NETJES_OUTPUT: output,
    // The static host's URL, for the links in sitemap.xml and robots.txt (Program.cs falls back to localhost).
    SiteUrl: SITE,
  };
  delete env.NETJES_CHECK;
  const child = spawn('dotnet', ['run', '--no-build', '--no-launch-profile'], { cwd: app, env, stdio: ['ignore', 'pipe', 'pipe'] });
  let log = '';
  child.stdout.on('data', (chunk) => {
    log += String(chunk);
  });
  child.stderr.on('data', (chunk) => {
    log += String(chunk);
  });
  const code = await new Promise<number | null>((resolve, reject) => {
    child.on('error', reject);
    child.on('exit', resolve);
  });
  if (code !== 0) throw new Error(`The export exited with ${code}:\n${log}`);
  return log;
}

/**
 * Answers SITE from the export: a path names a file or a directory with an index.html; anything else is the 404 page.
 * Every response carries `headers`, such as the static host's own Content-Security-Policy.
 */
async function serveExport(page: Page, headers: Record<string, string> = {}) {
  await page.route(`${SITE}/**`, async (route) => {
    const file = path.join(output, ...decodeURIComponent(new URL(route.request().url()).pathname).split('/'));
    const found = [file, path.join(file, 'index.html')].find(
      (candidate) => candidate.startsWith(output + path.sep) && existsSync(candidate) && statSync(candidate).isFile(),
    );
    await route.fulfill(found ? { path: found, headers } : { status: 404, path: path.join(output, '404.html'), headers });
  });
}

/** The `script-src` sources that allow each inline script in an exported file: the hash of its text. */
function inlineScriptHashes(file: string): string[] {
  const scripts = readFileSync(file, 'utf8').matchAll(/<script(?![^>]*\ssrc=)[^>]*>([\s\S]*?)<\/script>/g);
  return [...scripts].map((script) => `'sha256-${createHash('sha256').update(script[1] ?? '').digest('base64')}'`);
}

test.beforeAll(async () => {
  test.setTimeout(180_000);
  exportLog = await exportBlog();
});

test.describe('without JavaScript', () => {
  test.use({ javaScriptEnabled: false });

  test('an exported page shows its deferred content in place of the fallback', async ({ page }) => {
    await serveExport(page);
    await page.goto(`${SITE}/streaming`);

    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Streaming');
    await expect(page.getByRole('status')).toHaveText(REPORT);
    await expect(page.getByText('Preparing the slow report…')).toHaveCount(0);
    // No script to reveal it either, which a hash-based policy on the static host would block.
    expect(readFileSync(path.join(output, 'streaming', 'index.html'), 'utf8')).not.toContain('$RC');
  });
});

test('an exported page shows its deferred content on load where the static host runs no inline script', async ({ page }) => {
  // What a hash-based policy does to the scripts React and Netjes write into a document for one render. The script
  // that delivers the value is blocked too, so the content shows but does not hydrate: see the next test.
  await serveExport(page, { 'Content-Security-Policy': "script-src 'self'" });
  await page.goto(`${SITE}/streaming`);

  await expect(page.getByRole('status')).toHaveText(REPORT);
  await expect(page.getByText('Preparing the slow report…')).toHaveCount(0);
});

test('an exported page hydrates its deferred content under a policy that lists its inline scripts', async ({ page }) => {
  // In a file, the script that delivers a Page.Defer value is fixed text like the bootstrap, so a static host's policy
  // can allow it by its hash. Without it the value never arrives, and the click below, which renders the Suspense
  // boundary again, would bring the fallback back for good.
  const hashes = inlineScriptHashes(path.join(output, 'streaming', 'index.html'));
  // The policy has no 'unsafe-eval'. A violation the page's code catches logs nothing to the console.
  const violations = await watchForViolations(page);
  await serveExport(page, { 'Content-Security-Policy': `script-src 'self' ${hashes.join(' ')}` });
  await page.goto(`${SITE}/streaming`);
  await markWindow(page);

  await page.getByRole('button', { name: 'clicks 0' }).click();

  await expect(page.getByRole('button', { name: 'clicks 1' })).toBeVisible();
  await expect(page.getByRole('status')).toHaveText(REPORT);
  await expect(page.getByText('Preparing the slow report…')).toHaveCount(0);
  expect(violations).toEqual([]);
});

test('an exported page with deferred content hydrates', async ({ page }) => {
  // The Blog collects Web Vitals, posted to /_netjes/vitals, which a static host does not have.
  const errors = watchForErrors(page, (url) => url.pathname === '/_netjes/vitals');
  await serveExport(page);
  await page.goto(`${SITE}/streaming`);
  await markWindow(page);

  await page.getByRole('button', { name: 'clicks 0' }).click();

  await expect(page.getByRole('button', { name: 'clicks 1' })).toBeVisible();
  await expect(page.getByRole('status')).toHaveText(REPORT);
  expect(errors).toEqual([]);
});

test('the export warns that /blog reads tag from the query string', () => {
  // The export requests /blog without a query string, so its file lists every post, and a static host serves it for
  // every tag link. The Blog keeps the filter as its example of a loader that reads the query string (PostPages.cs).
  expect(exportLog.split('Netjes.Export.QueryString').length - 1).toBe(1);
  expect(exportLog).toContain("app/blog/page.tsx reads 'tag'");
});

test('the export leaves out the help center, which Program.cs mounts with ExcludeFromExport(), and its 404 is the root one', () => {
  // The package's article page posts notes to a page action, which needs a server.
  expect(existsSync(path.join(output, 'help'))).toBe(false);
  const notFound = readFileSync(path.join(output, '404.html'), 'utf8');
  expect(notFound).toContain('"page":"app/not-found.tsx"');
  expect(notFound).not.toContain('@netjes-examples/help-center/app/not-found.tsx');
  expect(readFileSync(path.join(output, 'sitemap.xml'), 'utf8')).not.toContain('/help');
});

test('the export writes sitemap.xml with the exported pages, and robots.txt', () => {
  const sitemap = readFileSync(path.join(output, 'sitemap.xml'), 'utf8');
  const locs = [...sitemap.matchAll(/<loc>([^<]*)<\/loc>/g)].map((match) => match[1] ?? '');

  expect(locs).toContain(`${SITE}/`);
  expect(locs.filter((loc) => loc.startsWith(`${SITE}/blog/`)).length).toBeGreaterThan(0);
  expect(locs.filter((loc) => loc.startsWith(`${SITE}/docs/`)).length).toBeGreaterThan(0);
  // The guestbook needs a server for its form, so the export leaves it out (GuestbookPage.cs), and the sitemap too.
  expect(locs).not.toContain(`${SITE}/guestbook`);
  // Every URL it lists is a page the export wrote.
  for (const loc of locs) {
    const segments = decodeURIComponent(new URL(loc).pathname).split('/').filter(Boolean);
    expect(existsSync(path.join(output, ...segments, 'index.html')), loc).toBe(true);
  }
  expect(readFileSync(path.join(output, 'robots.txt'), 'utf8')).toContain(`\nSitemap: ${SITE}/sitemap.xml\n`);
  expect(exportLog).not.toContain('loopback');
});
