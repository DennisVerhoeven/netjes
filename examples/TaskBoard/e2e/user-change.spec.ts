import { type Page, chromium, expect, test } from '@playwright/test';
import { PASSWORD, createProject, isMarked, markWindow, register, registerElsewhere, signIn } from './helpers.ts';

/**
 * A user change while the tab keeps its layouts: signing out and in never reloads the document, so the router, the
 * root layout and the query cache stay mounted, and the client still holds the first user's pages, layout keys and
 * history entries. Nothing of the first user may show for the second.
 * - Sign out and in as someone else in the same tab: the second user's sidebar, name and projects only.
 * - Back after that: the first user's pages are not shown again from the router's memory.
 * - Back after only signing out: the page signed out on is loaded again, as a visitor, not shown from memory.
 * - Back, after signing out in a later document, to a document that signed in through the router: the browser's
 *   back/forward cache does not show it, although the document itself was sent to a visitor.
 * - Another tab signs out, then in as someone else: this tab shows what the server renders for each at once, without a
 *   click, and Back there does not show the first user's pages.
 * - The same in a browser without BroadcastChannel: this tab's next click sends the first user's layout keys, and every
 *   layout is loaded again for the second user.
 * - A sign-in that stays on its page (the landing page's form): the page shows the signed-in user at once, in the same
 *   document and history entry.
 * The server half (the keys and what each loader returns) is tests/TaskBoard.Tests/UserChangeTests.cs.
 */

const sidebar = (page: Page) => page.getByRole('navigation', { name: 'Projects' });

/** The first user, with a project of their own open on its board; returns the project's key. */
async function firstUserOnTheirBoard(page: Page, project: string) {
  await register(page, 'First User');
  const slug = await createProject(page, project);
  await page.getByRole('list', { name: 'Your projects' }).getByRole('link', { name: new RegExp(project) }).click();
  await expect(page.getByRole('heading', { level: 1, name: project })).toBeVisible();
  return slug;
}

test.describe('a user change in one tab', () => {
  test('signing out and in as someone else shows only their projects, in the same document', async ({ page, browser }) => {
    const second = await registerElsewhere(browser, 'Second User');
    await firstUserOnTheirBoard(page, 'First private');
    await markWindow(page);

    await page.getByRole('button', { name: 'Sign out' }).click();
    await page.getByRole('navigation', { name: 'Account' }).getByRole('link', { name: 'Sign in' }).click();
    await signIn(page, second);

    await expect(page).toHaveURL(/\/projects$/);
    await expect(page.getByRole('heading', { level: 1, name: 'Projects' })).toBeVisible();
    await expect(page.getByText(second)).toBeVisible();
    await expect(page.getByText('First User')).toHaveCount(0);
    await expect(sidebar(page).getByRole('link', { name: /First private/ })).toHaveCount(0);
    await expect(page.getByText('First private')).toHaveCount(0);
    expect(await isMarked(page)).toBe(true);
  });

  test("going back after the user change does not show the first user's pages", async ({ page, browser }) => {
    const second = await registerElsewhere(browser, 'Second User');
    const slug = await firstUserOnTheirBoard(page, 'First private');
    await markWindow(page);

    await page.getByRole('button', { name: 'Sign out' }).click();
    await page.getByRole('navigation', { name: 'Account' }).getByRole('link', { name: 'Sign in' }).click();
    await signIn(page, second);
    await expect(page).toHaveURL(/\/projects$/);
    // Back past the sign-in page and the landing page, to the first user's board.
    for (const url of [/\/login$/, /\/$/, new RegExp(`/projects/${slug}$`)]) {
      await page.goBack();
      await expect(page).toHaveURL(url);
    }

    await expect(page.getByRole('heading', { name: 'You are not on this project' })).toBeVisible();
    await expect(page.getByText('First private')).toHaveCount(0);
    await expect(page.getByText('First User')).toHaveCount(0);
    await expect(page.getByText(second)).toBeVisible();
    expect(await isMarked(page)).toBe(true);
  });

  test('going back after signing out loads the page signed out on again', async ({ page }) => {
    const slug = await firstUserOnTheirBoard(page, 'First private');
    await markWindow(page);

    await page.getByRole('button', { name: 'Sign out' }).click();
    await expect(page).toHaveURL(/\/$/);
    await page.goBack();

    // The board needs an account: its 401 page sends the visitor on to sign in, and back to the board after.
    await expect(page).toHaveURL(new RegExp(`/login\\?returnUrl=%2Fprojects%2F${slug}$`));
    await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();
    await expect(page.getByText('First private')).toHaveCount(0);
    await expect(page.getByText('First User')).toHaveCount(0);
    expect(await isMarked(page)).toBe(true);
  });

  // A visitor's document stays the document when the client router signs the visitor in. Sent `no-cache`, the browser
  // could keep it in its back/forward cache and, after a full page load and a sign-out there, restore it on Back with
  // the user's name, email and projects. Every page goes out `no-store`, which Chromium restores only while the site's
  // cookies are unchanged, and a sign-out changes them.
  test("going back to a document that signed in through the router, after signing out in a later one, does not show it", async ({
    baseURL,
    browser: shared,
  }) => {
    test.skip(test.info().project.name !== 'production', "the dev server's websocket keeps every page out of the back/forward cache");
    const email = await registerElsewhere(shared, 'Cached User');
    // Playwright starts browsers with the back/forward cache off; this one keeps it on.
    const browser = await chromium.launch({ channel: 'chromium', ignoreDefaultArgs: ['--disable-back-forward-cache'] });
    try {
      const page = await (await browser.newContext({ baseURL })).newPage();
      const restored: string[] = [];
      const notRestored: string[] = [];
      const cdp = await page.context().newCDPSession(page);
      await cdp.send('Page.enable');
      cdp.on('Page.frameNavigated', (event) => {
        if (event.type === 'BackForwardCacheRestore') restored.push(event.frame.url);
      });
      cdp.on('Page.backForwardCacheNotUsed', (event) => notRestored.push(...event.notRestoredExplanations.map((e) => e.reason)));

      // A first visit stores the antiforgery cookie; /login is then a returning visitor's document, and it stays the
      // document while the router signs in and shows the user's projects.
      await page.goto('/');
      await page.goto('/login');
      await markWindow(page);
      await signIn(page, email);
      await expect(page).toHaveURL(/\/projects$/);
      await expect(page.getByText('Cached User')).toBeVisible();
      const signedIn = await page.evaluate(() => history.length);

      // A full page load (a reloadDocument link, a typed URL, a new deploy), and the sign-out there.
      await page.goto('/projects?again=1');
      await markWindow(page);
      await page.getByRole('button', { name: 'Sign out' }).click();
      await expect(page).toHaveURL(/\/$/);
      const signedOut = await page.evaluate(() => history.length);
      await page.evaluate((delta) => history.go(delta), signedIn - signedOut);

      await expect(page).toHaveURL(/\/login\?returnUrl=%2Fprojects$/);
      await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();
      await expect(page.getByText('Cached User')).toHaveCount(0);
      await expect(page.getByText(email)).toHaveCount(0);
      // The cache was on: it restored the document, or refused it for the page's own reasons and not the browser's
      // settings. A cache that is off would pass this test whatever Netjes does.
      expect(restored.length + notRestored.length).toBeGreaterThan(0);
      expect(notRestored.filter((reason) => reason.startsWith('BackForwardCacheDisabled'))).toEqual([]);
    } finally {
      await browser.close();
    }
  });

  test('signing in on the landing page shows the signed-in user there at once', async ({ page, browser }) => {
    const email = await registerElsewhere(browser, 'Landing User');
    await page.goto('/');
    await markWindow(page);
    const entries = await page.evaluate(() => history.length);

    const form = page.getByRole('form', { name: 'Sign in here' });
    await form.getByLabel('Email').fill(email);
    await form.getByLabel('Password').fill(PASSWORD);
    await form.getByRole('button', { name: 'Sign in here' }).click();

    const account = page.getByRole('navigation', { name: 'Account' });
    await expect(account.getByText('Landing User')).toBeVisible();
    await expect(account.getByRole('link', { name: 'Open your board' })).toBeVisible();
    await expect(form).toHaveCount(0);
    await expect(page).toHaveURL(/\/$/);
    expect(await page.evaluate(() => history.length)).toBe(entries);
    expect(await isMarked(page)).toBe(true);
  });

  test("another tab's sign-out and sign-in show in this tab at once, and Back here does not show the first user's pages", async ({
    browser,
  }) => {
    const second = await registerElsewhere(browser, 'Second User');
    const context = await browser.newContext();
    const page = await context.newPage();
    const slug = await firstUserOnTheirBoard(page, 'Tab private');
    await markWindow(page);

    // Another tab of the same browser signs out: this tab loads its board again, as a visitor, and is sent on to sign in.
    const other = await context.newPage();
    await other.goto('/projects');
    await markWindow(other);
    await other.getByRole('button', { name: 'Sign out' }).click();
    await expect(other).toHaveURL(/\/$/);

    await expect(page).toHaveURL(new RegExp(`/login\\?returnUrl=%2Fprojects%2F${slug}$`));
    await expect(page.getByRole('heading', { level: 1, name: 'Welcome back' })).toBeVisible();
    await expect(page.getByText('Tab private')).toHaveCount(0);
    await expect(page.getByText('First User')).toHaveCount(0);

    // It signs in as someone else: this tab's sign-in page sends the second user back to the board.
    await other.getByRole('navigation', { name: 'Account' }).getByRole('link', { name: 'Sign in' }).click();
    await signIn(other, second);
    await expect(other).toHaveURL(/\/projects$/);

    await expect(page).toHaveURL(new RegExp(`/projects/${slug}$`));
    await expect(page.getByRole('heading', { name: 'You are not on this project' })).toBeVisible();
    await expect(page.getByText(second)).toBeVisible();
    await expect(page.getByText('First User')).toHaveCount(0);
    await expect(sidebar(page).getByRole('link', { name: /Tab private/ })).toHaveCount(0);

    // Back to the projects page the first user made the project on: loaded from the server, for the second user.
    await page.goBack();
    await expect(page).toHaveURL(/\/projects$/);
    await expect(page.getByRole('heading', { level: 1, name: 'Projects' })).toBeVisible();
    await expect(page.getByText(second)).toBeVisible();
    await expect(page.getByText('Tab private')).toHaveCount(0);
    await expect(page.getByText('First User')).toHaveCount(0);
    expect(await isMarked(page)).toBe(true);
    await context.close();
  });

  test("a tab holding the first user's layouts, in a browser without BroadcastChannel, loads them again for whoever another tab signed in", async ({
    browser,
  }) => {
    const second = await registerElsewhere(browser, 'Second User');
    const context = await browser.newContext();
    // No message reaches this tab: it learns of the user change from the server, on its next click.
    await context.addInitScript(() => Reflect.deleteProperty(window, 'BroadcastChannel'));
    const page = await context.newPage();
    await firstUserOnTheirBoard(page, 'Tab private');
    await markWindow(page);

    // Another tab of the same browser signs out and in as someone else: the cookie changes under this tab.
    const other = await context.newPage();
    await other.goto('/projects');
    await markWindow(other);
    await other.getByRole('button', { name: 'Sign out' }).click();
    await expect(other).toHaveURL(/\/$/);
    await other.getByRole('navigation', { name: 'Account' }).getByRole('link', { name: 'Sign in' }).click();
    await signIn(other, second);
    await expect(other).toHaveURL(/\/projects$/);

    // This tab still shows the first user's layouts; a click inside them sends their keys.
    await page.getByRole('navigation', { name: 'Project', exact: true }).getByRole('link', { name: 'Members', exact: true }).click();

    await expect(page.getByRole('heading', { name: 'You are not on this project' })).toBeVisible();
    await expect(page.getByText(second)).toBeVisible();
    await expect(page.getByText('First User')).toHaveCount(0);
    await expect(sidebar(page).getByRole('link', { name: /Tab private/ })).toHaveCount(0);
    expect(await isMarked(page)).toBe(true);
    await context.close();
  });
});
