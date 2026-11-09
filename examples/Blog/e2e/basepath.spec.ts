import { expect, test } from '@playwright/test';
import { isMarked, markWindow } from './helpers.ts';

// The Blog mounted with app.UsePathBase("/base"). URLs here are relative to the project's baseURL (…/base/).

test('links, the router and the hero image stay under the base path, and navigation stays on the client', async ({ page }) => {
  const errors: string[] = [];
  page.on('console', (message) => {
    if (message.type() === 'error') errors.push(message.text());
  });
  await page.goto('./');
  await markWindow(page);

  const nav = page.getByRole('navigation', { name: 'Main' });
  await expect(nav.getByRole('link', { name: 'Blog' })).toHaveAttribute('href', '/base/blog');
  await expect(page.getByRole('img', { name: 'Gradient' })).toHaveAttribute('srcset', /^\/base\/_netjes\/image\?url=%2Fhero\.png/);
  await nav.getByRole('link', { name: 'Blog' }).click();

  await expect(page).toHaveURL(/\/base\/blog$/);
  await expect(page.getByRole('heading', { name: 'All posts' })).toBeVisible();
  await expect(nav.getByRole('link', { name: 'Blog' })).toHaveAttribute('aria-current', 'page');
  expect(await isMarked(page)).toBe(true);
  expect(errors).toEqual([]);
});

test('forms post and redirect under the base path', async ({ page }) => {
  const name = `Base ${Date.now()}`;
  await page.goto('guestbook');
  await markWindow(page);

  await page.getByLabel('Name').fill(name);
  await page.getByLabel('Message').fill('Signed under a base path.');
  await page.getByRole('button', { name: 'Sign the guestbook' }).click();

  await expect(page).toHaveURL(/\/base\/guestbook$/);
  await expect(page.getByRole('list', { name: 'Entries' })).toContainText(name);
});

test('the help center page package links, navigates, posts and fetches under the base path', async ({ page }) => {
  await page.goto('help');
  await markWindow(page);

  const center = page.locator('[data-help-center]');
  await expect(center.getByRole('link', { name: 'Contact' })).toHaveAttribute('href', '/base/help/contact');
  await center.getByRole('link', { name: 'Privacy' }).click();
  await expect(page).toHaveURL(/\/base\/help\/articles\/privacy$/);

  // The page action's redirect comes from PagePackageMount.Url in C#, and the server puts the base path in front.
  await page.getByLabel('Your note').fill('Sent under a base path.');
  await page.getByRole('button', { name: 'Send note' }).click();
  await expect(page).toHaveURL(/\/base\/help\/articles\/privacy\?feedback=sent$/);
  await expect(page.getByRole('status').filter({ hasText: 'your note reached us' })).toBeVisible();

  await page.getByRole('button', { name: 'Yes' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Thanks for telling us' })).toBeVisible();

  await center.getByRole('link', { name: 'Contact' }).click();
  await expect(page).toHaveURL(/\/base\/help\/contact$/);
  const posted = page.waitForRequest((request) => request.method() === 'POST' && new URL(request.url()).pathname === '/base/help/messages');
  await page.getByLabel('Your email').fill('reader@example.com');
  await page.getByLabel('Message').fill('Hello from under a base path.');
  await page.getByRole('button', { name: 'Send message' }).click();
  await posted;
  await expect(page.getByRole('status')).toHaveText(/^Thanks, we have your message\./);
  expect(await isMarked(page)).toBe(true);
});

test('the image optimizer answers under the base path', async ({ request }) => {
  const response = await request.get('_netjes/image?url=%2Fhero.png&w=640', { headers: { Accept: 'image/webp' } });

  expect(response.status()).toBe(200);
  expect(response.headers()['content-type']).toBe('image/webp');
});

test('the like button fetches its count under the base path', async ({ page }) => {
  // LikeButton fetches the count once the page is interactive, from the endpoint's URL under the base path.
  const fetched = page.waitForRequest((request) => request.url().endsWith('/likes'));
  await page.goto('blog/forms-that-work-without-javascript');

  expect(new URL((await fetched).url()).pathname).toBe('/base/api/posts/forms-that-work-without-javascript/likes');
  await expect(page.getByRole('button', { name: /likes?/ })).toBeEnabled();
});

test.describe('without JavaScript', () => {
  test.use({ javaScriptEnabled: false });

  test('a form post redirects back under the base path', async ({ page }) => {
    const name = `NoJs ${Date.now()}`;
    await page.goto('guestbook');

    await page.getByLabel('Name').fill(name);
    await page.getByLabel('Message').fill('Signed without JavaScript under a base path.');
    await page.getByRole('button', { name: 'Sign the guestbook' }).click();

    // A plain document post: the 303 goes to the base-prefixed page, and the entry is on it.
    await expect(page).toHaveURL(/\/base\/guestbook$/);
    await expect(page.getByRole('list', { name: 'Entries' })).toContainText(name);
  });

  test('an invalid post shows its errors on the base-prefixed page', async ({ page }) => {
    await page.goto('guestbook');

    await page.getByRole('button', { name: 'Sign the guestbook' }).click();

    await expect(page).toHaveURL(/\/base\/guestbook$/);
    await expect(page.getByText('Tell us who you are.')).toBeVisible();
  });
});
