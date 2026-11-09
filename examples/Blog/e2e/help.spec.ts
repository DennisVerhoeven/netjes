import { expect, test } from '@playwright/test';
import { isMarked, markWindow, watchForErrors, watchForViolations } from './helpers.ts';

// The help center page package (examples/HelpCenter), which Program.cs mounts at /help: its pages, layouts, loaders,
// page action, server action and not-found, inside the Blog's root layout, in development and in production.

test('a help page renders inside the Blog chrome and the package layout, styled by the package Tailwind tokens', async ({ page }) => {
  const errors = watchForErrors(page);
  const violations = await watchForViolations(page);
  const response = await page.goto('/help');

  expect(response?.status()).toBe(200);
  await expect(page).toHaveTitle('Help center · Netjes Blog');
  await expect(page.getByRole('navigation', { name: 'Main' })).toBeVisible();
  const center = page.locator('[data-help-center]');
  await expect(center.getByRole('navigation', { name: 'Help center' })).toBeVisible();
  await expect(center.getByRole('heading', { level: 1 })).toHaveText('How can we help?');
  // bg-help-accent: a utility only the package's pages use, from the token in its stylesheet (#0f6f6a).
  await expect(center.locator('header')).toHaveCSS('background-color', 'rgb(15, 111, 106)');
  await markWindow(page);
  expect(errors).toEqual([]);
  expect(violations).toEqual([]);
});

test('client navigation moves between Blog pages and help pages without a document load, and mounted links go below /help', async ({
  page,
}) => {
  const errors = watchForErrors(page);
  await page.goto('/');
  await markWindow(page);

  await page.getByRole('link', { name: 'Visit the help center' }).click();
  await expect(page).toHaveURL(/\/help$/);
  const center = page.locator('[data-help-center]');
  await expect(center.getByRole('heading', { level: 1 })).toHaveText('How can we help?');
  await expect(center.getByRole('link', { name: 'Contact' })).toHaveAttribute('href', '/help/contact');
  await expect(center.getByRole('link', { name: 'Privacy' })).toHaveAttribute('href', '/help/articles/privacy');

  await center.getByRole('link', { name: 'Privacy' }).click();
  await expect(page).toHaveURL(/\/help\/articles\/privacy$/);
  await expect(page).toHaveTitle('Privacy · Help center · Netjes Blog');
  await expect(page.getByRole('navigation', { name: 'Articles in the help center' }).getByRole('link', { name: 'Privacy' })).toHaveAttribute(
    'aria-current',
    'page',
  );

  await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Blog' }).click();
  await expect(page).toHaveURL(/\/blog$/);
  await expect(page.getByRole('heading', { name: 'All posts' })).toBeVisible();
  expect(await isMarked(page)).toBe(true);
  expect(errors).toEqual([]);
});

test('a note posts to the package page action and comes back to the article', async ({ page }) => {
  // The empty note's 422 shows as a failed load in the console.
  const errors = watchForErrors(page, (url) => url.pathname === '/help/articles/getting-started');
  await page.goto('/help/articles/getting-started');
  await markWindow(page);

  await page.getByRole('button', { name: 'Send note' }).click();
  await expect(page.locator('#comment-error')).toHaveText('Tell us what was missing.');

  await page.getByLabel('Your note').fill('A word on searching would help.');
  await page.getByRole('button', { name: 'Send note' }).click();

  await expect(page).toHaveURL(/\/help\/articles\/getting-started\?feedback=sent$/);
  await expect(page.getByRole('status')).toHaveText('Thanks, your note reached us.');
  expect(await isMarked(page)).toBe(true);
  expect(errors).toEqual([]);
});

test.describe('without JavaScript', () => {
  test.use({ javaScriptEnabled: false });

  test('a note posts and redirects as a plain form', async ({ page }) => {
    await page.goto('/help/articles/your-account');

    await page.getByLabel('Your note').fill('How do I close my account?');
    await page.getByRole('button', { name: 'Send note' }).click();

    await expect(page).toHaveURL(/\/help\/articles\/your-account\?feedback=sent$/);
    await expect(page.getByRole('status')).toHaveText('Thanks, your note reached us.');
  });

  test('an empty note shows its error on the article', async ({ page }) => {
    await page.goto('/help/articles/your-account');

    await page.getByRole('button', { name: 'Send note' }).click();

    await expect(page).toHaveURL(/\/help\/articles\/your-account$/);
    await expect(page.locator('#comment-error')).toHaveText('Tell us what was missing.');
  });
});

test('the help.vote server action counts the answer', async ({ page }) => {
  const errors = watchForErrors(page);
  await page.goto('/help/articles/privacy');
  await markWindow(page);

  await page.getByRole('button', { name: 'Yes' }).click();

  await expect(page.getByRole('status')).toHaveText(/^Thanks for telling us\. \d+ readers? found it helpful\.$/);
  expect(await isMarked(page)).toBe(true);
  expect(errors).toEqual([]);
});

test('the contact form posts to the package endpoint below the mount', async ({ page }) => {
  const errors = watchForErrors(page);
  await page.goto('/help/contact');
  await markWindow(page);
  const posted = page.waitForRequest((request) => request.method() === 'POST' && new URL(request.url()).pathname === '/help/messages');

  await page.getByLabel('Your email').fill('reader@example.com');
  await page.getByLabel('Message').fill('Hello from the end-to-end suite.');
  await page.getByRole('button', { name: 'Send message' }).click();

  await posted;
  await expect(page.getByRole('status')).toHaveText(/^Thanks, we have your message\. Its number is \d+\.$/);
  expect(errors).toEqual([]);
});

test('an unknown URL below /help shows the package not-found, whose mounted links work', async ({ page }) => {
  // The document's own 404 shows as a failed load in the console.
  const errors = watchForErrors(page, (url) => url.pathname === '/help/nope');
  const response = await page.goto('/help/nope');

  expect(response?.status()).toBe(404);
  await expect(page).toHaveTitle('Not found · Help center · Netjes Blog');
  const center = page.locator('[data-help-center]');
  await expect(center.getByRole('heading', { level: 1 })).toHaveText('No help page at /help/nope');
  await markWindow(page);

  await center.getByRole('link', { name: 'Browse the articles' }).click();
  await expect(page).toHaveURL(/\/help$/);
  await expect(center.getByRole('heading', { level: 1 })).toHaveText('How can we help?');
  expect(await isMarked(page)).toBe(true);
  expect(errors).toEqual([]);
});
