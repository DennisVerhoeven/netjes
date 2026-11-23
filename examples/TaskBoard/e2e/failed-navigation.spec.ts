import { type Page, type Route, expect, test } from '@playwright/test';
import { createProject, isMarked, markWindow, register } from './helpers.ts';

/**
 * A refresh or a navigation that fails while the browser is online keeps the page and says so, through
 * useNavigation().error, which the app layout shows; the browser loads no document and shows no error page of its own.
 * The connection is cut for the router's requests of one URL only (X-Netjes-Data): the server action itself goes
 * through, and the refresh it asks for gets no answer.
 */

/** Cuts the connection for the client router's requests of `path`, until the returned function puts it back. */
async function cutRouterRequests(page: Page, path: string): Promise<() => Promise<void>> {
  const matches = (url: URL) => url.pathname === path;
  const handler = (route: Route) => (route.request().headers()['x-netjes-data'] ? route.abort('connectionrefused') : route.continue());
  await page.route(matches, handler);
  return () => page.unroute(matches, handler);
}

// The board's own form: the project's name is a heading on every page of the project, and a column with no task is an
// empty list, which takes no room.
const board = (page: Page) => page.getByRole('form', { name: 'Filter tasks' });

/** A user on their project's board, reached by a client navigation from the projects page. */
async function onBoard(page: Page) {
  await register(page, 'Lost Connection');
  const slug = await createProject(page, 'Flaky');
  await page.getByRole('list', { name: 'Your projects' }).getByRole('link', { name: /Flaky/ }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Flaky' })).toBeVisible();
  await markWindow(page);
  return slug;
}

test.describe('a failed refresh or navigation', () => {
  test('keeps the board when the refresh after adding a task gets no answer, and brings it up to date on Try again', async ({ page }) => {
    const slug = await onBoard(page);
    const reconnect = await cutRouterRequests(page, `/projects/${slug}`);

    await page.getByLabel('New task in Todo').fill('Written while the line was down');
    await page.getByRole('button', { name: 'Add task to Todo' }).click();

    const alert = page.getByRole('alert').filter({ hasText: 'This page could not be brought up to date.' });
    await expect(alert).toBeVisible();
    await expect(alert).toContainText('No answer came.');
    await expect(page).toHaveURL(new RegExp(`/projects/${slug}$`));
    await expect(board(page)).toBeVisible();
    expect(await isMarked(page)).toBe(true);

    await reconnect();
    await alert.getByRole('button', { name: 'Try again' }).click();

    await expect(page.getByRole('link', { name: 'Written while the line was down' })).toBeVisible();
    await expect(page.getByRole('alert')).toHaveCount(0);
    expect(await isMarked(page)).toBe(true);
  });

  test('keeps the board when a link gets no answer, and the link works once the connection is back', async ({ page }) => {
    const slug = await onBoard(page);
    const reconnect = await cutRouterRequests(page, `/projects/${slug}/settings`);
    const members = page.getByRole('navigation', { name: 'Project', exact: true }).getByRole('link', { name: 'Members', exact: true });

    await members.click();

    await expect(page.getByRole('alert').filter({ hasText: 'That page could not be opened.' })).toBeVisible();
    await expect(page).toHaveURL(new RegExp(`/projects/${slug}$`));
    await expect(board(page)).toBeVisible();
    expect(await isMarked(page)).toBe(true);

    await reconnect();
    await members.click();

    await expect(page.getByRole('list', { name: 'Members' })).toBeVisible();
    await expect(page.getByRole('alert')).toHaveCount(0);
    expect(await isMarked(page)).toBe(true);
  });
});
