import { expect, test } from '@playwright/test';
import { isMarked, markWindow, watchForErrors } from './helpers.ts';

// The help center page package (examples/HelpCenter), which Program.cs mounts at /(marketing)/help: inside the
// marketing chrome, with TaskBoard's own contact page in its place (.Override) and TaskBoard's shortcuts page in the
// package's (center) group.

test('the help center renders inside the marketing chrome, in TaskBoard colours', async ({ page }) => {
  const errors = watchForErrors(page);
  await page.goto('/');
  await markWindow(page);

  await page.getByRole('navigation', { name: 'Account' }).getByRole('link', { name: 'Help' }).click();

  await expect(page).toHaveURL(/\/help$/);
  await expect(page).toHaveTitle('Help center · TaskBoard');
  await expect(page.getByRole('navigation', { name: 'Account' })).toBeVisible();
  const center = page.locator('[data-help-center]');
  await expect(center.getByRole('heading', { level: 1 })).toHaveText('How can we help?');
  // TaskBoard sets the package's --help-accent to its signal colour (#c8f031) in styles/global.css.
  await expect(center.locator('header')).toHaveCSS('background-color', 'rgb(200, 240, 49)');
  expect(await isMarked(page)).toBe(true);
  expect(errors).toEqual([]);
});

test("/help/contact is TaskBoard's own page, with the package's props and layout", async ({ page }) => {
  const errors = watchForErrors(page);
  await page.goto('/help');
  await markWindow(page);

  await page.locator('[data-help-center]').getByRole('link', { name: 'Contact' }).click();

  await expect(page).toHaveURL(/\/help\/contact$/);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Talk to the people who build TaskBoard');
  await expect(page.getByRole('link', { name: 'help@example.com' })).toHaveAttribute('href', 'mailto:help@example.com');
  await expect(page.locator('[data-help-center]').getByRole('navigation', { name: 'Help center' })).toBeVisible();
  expect(await isMarked(page)).toBe(true);
  expect(errors).toEqual([]);
});

test("TaskBoard's shortcuts page renders inside the package's (center) layout", async ({ page }) => {
  const errors = watchForErrors(page);
  await page.goto('/help/shortcuts');
  await markWindow(page);

  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Shortcuts');
  const articles = page.getByRole('navigation', { name: 'Articles in the help center' });
  await expect(articles.getByRole('link', { name: 'Privacy' })).toHaveAttribute('href', '/help/articles/privacy');

  await articles.getByRole('link', { name: 'Privacy' }).click();
  await expect(page).toHaveURL(/\/help\/articles\/privacy$/);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Privacy');
  expect(await isMarked(page)).toBe(true);
  expect(errors).toEqual([]);
});
