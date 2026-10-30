import { type Page, type Request, expect, test } from '@playwright/test';
import { createProject, isMarked, markWindow, register, watchForErrors } from './helpers.ts';

/**
 * Navigation cancellation: when a navigation is overtaken (a newer click, the back button) its late answer must not
 * be shown or leave a history entry. The slow page is held at the network until the newer navigation has committed.
 * - A newer click wins, whether the slow page was prefetched on hover (a request the router does not abort) or fetched
 *   on click (aborted): one history entry, the newer page's, and back goes where it should.
 * - Back while a page loads: the page that was loading never shows and adds no entry.
 */

type Held = { requests: Request[]; release(): void; settled(): Promise<void> };

/** Holds every request for `path` until released; settled() waits until each one has finished or failed. */
async function hold(page: Page, path: string): Promise<Held> {
  let release!: () => void;
  const released = new Promise<void>((resolve) => (release = resolve));
  const requests: Request[] = [];
  await page.route(
    (url) => url.pathname === path,
    async (route) => {
      requests.push(route.request());
      await released;
      // The router may have aborted it meanwhile.
      await route.continue().catch(() => {});
    },
  );
  const settled = () =>
    Promise.all(requests.map((request) => request.response().then((response) => response?.finished(), () => null))).then(() => {});
  return { requests, release, settled };
}

const historyLength = (page: Page) => page.evaluate(() => window.history.length);

/** A user on their project's board, reached by a client navigation from the projects page, with one task. */
async function onBoard(page: Page) {
  await register(page, 'Quick Clicker');
  const slug = await createProject(page, 'Clicks');
  await page.getByRole('list', { name: 'Your projects' }).getByRole('link', { name: /Clicks/ }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Clicks' })).toBeVisible();
  await page.getByLabel('New task in Todo').fill('The newer click');
  await page.getByRole('button', { name: 'Add task to Todo' }).click();
  await expect(page.getByRole('link', { name: 'The newer click' })).toBeVisible();
  await markWindow(page);
  return slug;
}

test.describe('navigation cancellation', () => {
  for (const how of ['hovered first', 'clicked without hovering'] as const) {
    test(`a newer click wins over a slower one (${how}), and leaves one history entry`, async ({ page }) => {
      const errors = watchForErrors(page);
      const slug = await onBoard(page);
      const before = await historyLength(page);
      const members = await hold(page, `/projects/${slug}/settings`);
      const membersLink = page.getByRole('navigation', { name: 'Project', exact: true }).getByRole('link', { name: 'Members', exact: true });
      const taskLink = page.getByRole('link', { name: 'The newer click' });

      // A hover prefetches; a click event alone fetches when it navigates, with the navigation's own abort signal.
      if (how === 'hovered first') await membersLink.click();
      else await membersLink.dispatchEvent('click');
      await expect.poll(() => members.requests.length).toBeGreaterThan(0);
      if (how === 'hovered first') await taskLink.click();
      else await taskLink.dispatchEvent('click');
      await expect(page).toHaveTitle(/The newer click/);
      members.release();
      await members.settled();

      await expect(page).toHaveURL(new RegExp(`/projects/${slug}/tasks/1$`));
      await expect(page.getByRole('heading', { name: 'Comments' })).toBeVisible();
      await expect(page.getByRole('list', { name: 'Members' })).toHaveCount(0);
      expect(await historyLength(page)).toBe(before + 1);
      await page.goBack();
      await expect(page).toHaveURL(new RegExp(`/projects/${slug}$`));
      await expect(page.getByRole('list', { name: 'Todo', exact: true })).toBeVisible();
      await page.goForward();
      await expect(page).toHaveURL(new RegExp(`/projects/${slug}/tasks/1$`));
      expect(await isMarked(page)).toBe(true);
      expect(errors).toEqual([]);
    });
  }

  test('going back while a page loads stays back, and the page that was loading adds no entry', async ({ page }) => {
    const errors = watchForErrors(page);
    const slug = await onBoard(page);
    const before = await historyLength(page);
    const members = await hold(page, `/projects/${slug}/settings`);

    await page.getByRole('navigation', { name: 'Project', exact: true }).getByRole('link', { name: 'Members', exact: true }).click();
    await expect.poll(() => members.requests.length).toBeGreaterThan(0);
    await page.goBack();
    await expect(page).toHaveURL(/\/projects$/);
    await expect(page.getByRole('heading', { level: 1, name: 'Projects' })).toBeVisible();
    members.release();
    await members.settled();

    await expect(page).toHaveURL(/\/projects$/);
    await expect(page.getByRole('heading', { level: 1, name: 'Projects' })).toBeVisible();
    await expect(page.getByRole('list', { name: 'Members' })).toHaveCount(0);
    expect(await historyLength(page)).toBe(before);
    // Forward goes to the board, where the click was made, not to the page that never arrived.
    await page.goForward();
    await expect(page).toHaveURL(new RegExp(`/projects/${slug}$`));
    await expect(page.getByRole('list', { name: 'Todo', exact: true })).toBeVisible();
    expect(await isMarked(page)).toBe(true);
    expect(errors).toEqual([]);
  });
});
