import { expect, test } from '@playwright/test';

// These tests pass with or without hydration: a link and a form work before the client router runs. A test that needs
// the client, such as one that clicks a button with an onClick handler, first waits for it after each full page load:
//   await expect(page.locator('html')).toHaveAttribute('data-netjes-hydrated', '');

test('the home page shows the message from its C# loader', async ({ page }) => {
  await page.goto('/');

  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Hello from C#');
});

test('the link on the home page leads to the about page', async ({ page }) => {
  await page.goto('/');

  await page.getByRole('link', { name: 'About this app' }).click();

  await expect(page).toHaveURL('/about');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('About');
});

test.describe('with JavaScript turned off', () => {
  test.use({ javaScriptEnabled: false });

  test('the form asks for the name it did not get', async ({ page }) => {
    await page.goto('/');

    await page.getByRole('button', { name: 'Say hello' }).click();

    await expect(page.getByText('Tell us your name.')).toBeVisible();
  });
});
