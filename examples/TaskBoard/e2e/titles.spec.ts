import { expect, test } from '@playwright/test';
import { createProject, register } from './helpers.ts';

/** Each page carries one <title>: the page's own through metadata, not a second one next to the layout's default. */
test.describe('page titles', () => {
  test('the not-found page has exactly one title', async ({ page }) => {
    const response = await page.goto('/no/such/page');

    expect(response?.status()).toBe(404);
    await expect(page.locator('title')).toHaveCount(1);
    await expect(page).toHaveTitle('Not found · TaskBoard');
  });

  test('a signed-out visitor gets one title on the sign-in prompt', async ({ browser }) => {
    // Without JavaScript the prompt stays; with it the client goes straight on to the login page.
    const context = await browser.newContext({ javaScriptEnabled: false });
    const page = await context.newPage();
    const response = await page.goto('/projects');

    expect(response?.status()).toBe(401);
    await expect(page.locator('title')).toHaveCount(1);
    await expect(page).toHaveTitle('Sign in · TaskBoard');
    await context.close();
  });

  test('a project you are not on has exactly one title', async ({ page, browser }) => {
    await register(page, 'Owner');
    const slug = await createProject(page, 'Private');
    const strangerContext = await browser.newContext();
    const stranger = await strangerContext.newPage();
    await register(stranger, 'Stranger');

    const response = await stranger.goto(`/projects/${slug}`);

    expect(response?.status()).toBe(403);
    await expect(stranger.locator('title')).toHaveCount(1);
    await expect(stranger).toHaveTitle('No access · TaskBoard');
    await strangerContext.close();
  });

  test('a missing project has exactly one title', async ({ page }) => {
    await register(page, 'Owner');
    const response = await page.goto('/projects/no-such-project-here');

    expect(response?.status()).toBe(404);
    await expect(page.locator('title')).toHaveCount(1);
    await expect(page).toHaveTitle('No such project · TaskBoard');
  });

  test('a missing task has exactly one title', async ({ page }) => {
    await register(page, 'Owner');
    const slug = await createProject(page, 'Board');
    const response = await page.goto(`/projects/${slug}/tasks/9999`);

    expect(response?.status()).toBe(404);
    await expect(page.locator('title')).toHaveCount(1);
    await expect(page).toHaveTitle('No such task · TaskBoard');
  });

});

/**
 * An action endpoint has no page route, so a refused action (here a post without a valid antiforgery token) renders
 * the root error page.
 */
test.describe('without JavaScript', () => {
  test.use({ javaScriptEnabled: false });

  test('the root error page has exactly one title', async ({ page, context }) => {
    await page.goto('/login');
    await context.clearCookies();
    await page.getByLabel('Email').fill('nobody@taskboard.test');
    await page.getByLabel('Password').fill('whatever it is');
    const response = page.waitForResponse((r) => r.url().endsWith('/_netjes/actions/auth.login'));
    await page.getByRole('button', { name: 'Sign in' }).click();

    expect((await response).status()).toBe(400);
    await expect(page.locator('title')).toHaveCount(1);
    await expect(page).toHaveTitle('Something went wrong · TaskBoard');
  });
});
