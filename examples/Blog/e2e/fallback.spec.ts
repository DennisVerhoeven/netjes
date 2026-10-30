import { expect, test } from '@playwright/test';
import { isMarked, markWindow, watchForErrors } from './helpers.ts';

// The Blog in production with its sidecar down for good (e2e/sidecar-down.mjs): no document can be rendered on the
// server, so each one is a shell .NET writes, and the browser renders the page instead of hydrating it.

test('with no sidecar ready, a page renders in the browser, its deferred data arrives and links navigate', async ({ page }) => {
  const errors = watchForErrors(page);

  const response = await page.goto('/blog/react-pages-on-dotnet');

  expect(response?.status()).toBe(200);
  // An output-cached page, but this is not stored, nor kept by a CDN or the browser: once a sidecar is back, the next
  // visitor gets it rendered.
  expect(response?.headers()['cache-control']).toBe('no-store');
  const html = (await response?.text()) ?? '';
  expect(html).toContain('No Node sidecar was ready');
  expect(html).not.toContain('<h1');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('React pages, served by ASP.NET Core');
  await expect(page).toHaveTitle('React pages, served by ASP.NET Core · Netjes Blog');
  // Page.Defer: the related posts stream in after the shell, with the CSP nonce.
  const related = page.getByRole('list', { name: 'Related posts' }).getByRole('link', { name: 'File-based routing without a Node server' });
  await expect(related).toBeVisible();

  await markWindow(page);
  await related.click();

  // A client navigation asks for JSON, which needs no sidecar.
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('File-based routing without a Node server');
  expect(await isMarked(page)).toBe(true);
  expect(errors).toEqual([]);
});
