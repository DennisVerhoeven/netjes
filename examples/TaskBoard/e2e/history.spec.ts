import { type Page, expect, test } from '@playwright/test';
import { createProject, isMarked, markWindow, register, watchForErrors } from './helpers.ts';

/**
 * A state per history entry (useHistoryState) and a page shown from memory (useHistoryRestore):
 * - A task opened from a filtered board goes back to the board as it was filtered: the card's link carries the board's
 *   address into the task's history entry, which keeps it through Back, Forward and a reload. A task opened another way
 *   goes back to the board without filters.
 * - Back from a task whose state changed on its page shows the board the router kept, and the board loads itself again,
 *   so the task is in its new column without a full page load.
 */

/** A user on their project's board, reached by a client navigation, with one task in Todo; returns the project's key. */
async function onBoard(page: Page, task: string) {
  await register(page, 'History Keeper');
  const slug = await createProject(page, 'Kept');
  await page.getByRole('list', { name: 'Your projects' }).getByRole('link', { name: /Kept/ }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Kept' })).toBeVisible();
  await page.getByLabel('New task in Todo').fill(task);
  await page.getByRole('button', { name: 'Add task to Todo' }).click();
  await expect(page.getByRole('list', { name: 'Todo', exact: true }).getByRole('link', { name: task })).toBeVisible();
  return slug;
}

const backToBoard = (page: Page) => page.getByRole('link', { name: '← Board' });

test.describe('history state', () => {
  test('a task goes back to the board as it was filtered, through Back, Forward and a reload', async ({ page }) => {
    const errors = watchForErrors(page);
    const slug = await onBoard(page, 'Water the plants');
    await markWindow(page);
    await page.getByLabel('Search').fill('Water');
    await page.getByRole('button', { name: 'Apply' }).click();
    await expect(page).toHaveURL(new RegExp(`/projects/${slug}\\?q=Water`));
    // The form's other fields are in the query too, empty.
    const filtered = new URL(page.url());
    const board = filtered.pathname + filtered.search;

    await page.getByRole('link', { name: 'Water the plants' }).click();
    await expect(page.getByRole('heading', { name: 'Comments' })).toBeVisible();
    await expect(backToBoard(page)).toHaveAttribute('href', board);

    await page.goBack();
    await expect(page.getByLabel('Search')).toHaveValue('Water');
    expect(new URL(page.url()).search).toBe(filtered.search);
    await page.goForward();
    await expect(page.getByRole('heading', { name: 'Comments' })).toBeVisible();
    await expect(backToBoard(page)).toHaveAttribute('href', board);
    expect(await isMarked(page)).toBe(true);

    // The entry keeps its state when the browser loads the document again. The server renders the plain board link,
    // and the page shows the kept one once it has hydrated, with no mismatch (watchForErrors).
    await page.reload();
    await markWindow(page);
    await expect(backToBoard(page)).toHaveAttribute('href', board);

    // Opened another way, the task has no board in its entry. (Going to the URL on screen would be a reload, which
    // keeps the entry.)
    await page.goto('/projects');
    await page.goto(`/projects/${slug}/tasks/1`);
    await markWindow(page);
    await expect(backToBoard(page)).toHaveAttribute('href', `/projects/${slug}`);
    expect(errors).toEqual([]);
  });

  test('Back from a task changed on its page shows the board from memory, which loads itself again', async ({ page }) => {
    const errors = watchForErrors(page);
    await onBoard(page, 'Repot the fern');
    await markWindow(page);

    await page.getByRole('link', { name: 'Repot the fern' }).click();
    await expect(page.getByRole('heading', { name: 'Comments' })).toBeVisible();
    await page.getByLabel('State').selectOption({ label: 'Done' });
    await page.getByRole('button', { name: 'Save changes' }).click();
    await expect(page.getByRole('status')).toHaveText('Saved.');

    await page.goBack();
    await expect(page.getByRole('list', { name: 'Done', exact: true }).getByRole('link', { name: 'Repot the fern' })).toBeVisible();
    await expect(page.getByRole('list', { name: 'Todo', exact: true }).getByRole('link', { name: 'Repot the fern' })).toHaveCount(0);
    expect(await isMarked(page)).toBe(true);
    expect(errors).toEqual([]);
  });
});
