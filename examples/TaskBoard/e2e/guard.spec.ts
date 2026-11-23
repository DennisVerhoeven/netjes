import { type Dialog, type Page, expect, test } from '@playwright/test';
import { createProject, isMarked, markWindow, register, watchForErrors } from './helpers.ts';

/**
 * Holding a navigation (useNavigationGuard): the task's edit form, with changes that are not saved, asks "Leave without
 * saving?" before a link, Back or Forward leaves it. For Back and Forward the router first puts the task's entry back,
 * so the address bar shows the task while the question is open. Browsers differ in when they fire `popstate`, so the
 * config also runs this spec in Firefox and WebKit.
 */

/** A user on a task of their project's board, opened from its card; returns the project's key. */
async function onTask(page: Page, task: string) {
  await register(page, 'Careful Editor');
  const slug = await createProject(page, 'Held');
  await page.getByRole('list', { name: 'Your projects' }).getByRole('link', { name: /Held/ }).click();
  await expect(board(page)).toBeVisible();
  await page.getByLabel('New task in Todo').fill(task);
  await page.getByRole('button', { name: 'Add task to Todo' }).click();
  await page.getByRole('list', { name: 'Todo', exact: true }).getByRole('link', { name: task }).click();
  await expect(page.getByLabel('Title')).toHaveValue(task);
  return slug;
}

const board = (page: Page) => page.getByRole('heading', { level: 1, name: 'Held' });
const question = (page: Page) => page.getByRole('alertdialog', { name: 'Leave without saving?' });

/** Types into a field after clicking it, so the page has the user's activation, which browsers want before they ask on unload. */
async function change(page: Page, label: string, value: string) {
  await page.getByLabel(label).click();
  await page.getByLabel(label).fill(value);
}

test.describe('holding a navigation', () => {
  test('a link asks before it leaves changes that are not saved', async ({ page }) => {
    const errors = watchForErrors(page);
    const slug = await onTask(page, 'Prune the roses');
    await markWindow(page);
    const task = page.url();

    await change(page, 'Title', 'Prune the climbing roses');
    await page.getByRole('link', { name: '← Board' }).click();
    await expect(question(page)).toBeVisible();
    await expect(question(page).getByRole('button', { name: 'Keep editing' })).toBeFocused();
    await question(page).getByRole('button', { name: 'Keep editing' }).click();
    await expect(question(page)).toHaveCount(0);
    expect(page.url()).toBe(task);
    await expect(page.getByLabel('Title')).toHaveValue('Prune the climbing roses');

    await page.getByRole('link', { name: '← Board' }).click();
    await question(page).getByRole('button', { name: 'Leave without saving' }).click();
    await expect(board(page)).toBeVisible();
    expect(new URL(page.url()).pathname).toBe(`/projects/${slug}`);
    expect(await isMarked(page)).toBe(true);
    expect(errors).toEqual([]);
  });

  test('Back puts the task back and asks, and goes back when told to', async ({ page }) => {
    const errors = watchForErrors(page);
    const slug = await onTask(page, 'Sow the beans');
    await markWindow(page);
    const task = page.url();

    await change(page, 'Description', 'Two rows by the fence.');
    await page.goBack({ waitUntil: 'commit' });
    await expect(question(page)).toBeVisible();
    await expect(page).toHaveURL(task);
    await question(page).getByRole('button', { name: 'Keep editing' }).click();
    await expect(question(page)).toHaveCount(0);
    await expect(page).toHaveURL(task);
    await expect(page.getByLabel('Description')).toHaveValue('Two rows by the fence.');

    // Asked again on the next Back: staying took no step.
    await page.goBack({ waitUntil: 'commit' });
    await question(page).getByRole('button', { name: 'Leave without saving' }).click();
    await expect(board(page)).toBeVisible();
    expect(new URL(page.url()).pathname).toBe(`/projects/${slug}`);
    expect(await isMarked(page)).toBe(true);
    expect(errors).toEqual([]);
  });

  test('Forward puts the task back and asks too', async ({ page }) => {
    const errors = watchForErrors(page);
    const slug = await onTask(page, 'Water the tomatoes');
    await markWindow(page);
    const task = page.url();
    // The board as an entry after the task: there, and Back to the task, with nothing changed yet.
    await page.getByRole('link', { name: '← Board' }).click();
    await expect(board(page)).toBeVisible();
    await page.goBack({ waitUntil: 'commit' });
    await expect(page.getByLabel('Title')).toHaveValue('Water the tomatoes');
    await expect(page).toHaveURL(task);

    await change(page, 'Title', 'Water the tomatoes twice');
    await page.goForward({ waitUntil: 'commit' });
    await expect(question(page)).toBeVisible();
    await expect(page).toHaveURL(task);
    await question(page).getByRole('button', { name: 'Keep editing' }).click();
    await expect(page.getByLabel('Title')).toHaveValue('Water the tomatoes twice');

    await page.goForward({ waitUntil: 'commit' });
    await question(page).getByRole('button', { name: 'Leave without saving' }).click();
    await expect(board(page)).toBeVisible();
    expect(new URL(page.url()).pathname).toBe(`/projects/${slug}`);
    expect(await isMarked(page)).toBe(true);
    expect(errors).toEqual([]);
  });

  test("saved changes hold nothing, and closing the tab with changes gets the browser's own question", async ({ page }) => {
    const errors = watchForErrors(page);
    await onTask(page, 'Mulch the beds');
    await markWindow(page);

    await change(page, 'Title', 'Mulch the beds thickly');
    await page.getByRole('button', { name: 'Save changes' }).click();
    await expect(page.getByRole('status')).toHaveText('Saved.');
    await page.getByRole('link', { name: '← Board' }).click();
    await expect(board(page)).toBeVisible();
    await expect(question(page)).toHaveCount(0);
    expect(await isMarked(page)).toBe(true);
    expect(errors).toEqual([]);

    await page.goBack({ waitUntil: 'commit' });
    await change(page, 'Title', 'Mulch the beds twice');
    const unloading = new Promise<Dialog>((resolve) => page.once('dialog', resolve));
    await page.close({ runBeforeUnload: true });
    const dialog = await unloading;
    expect(dialog.type()).toBe('beforeunload');
    await dialog.accept();
  });
});
