import { type Browser, type Page, expect, test } from '@playwright/test';
import { PASSWORD, createProject, register, registerElsewhere, signIn, unique } from './helpers.ts';

/**
 * TaskBoard without JavaScript: every form is a plain post to its server action, answered with a redirect (303) or,
 * when it fails validation, the page again with the errors and what was typed. Every action of the app, by flow:
 * - auth.register, projects.create, auth.logout: sign-up, starting a project (a taken key comes back as an error)
 *   and signing out.
 * - auth.login: sign-in from a page that needs it (the 401 page's link carries the return URL), and back to that page.
 * - auth.logout again: Back after signing out asks the server, so it shows the 401 page, not the signed-out user's.
 * - tasks.create, tasks.move: the board, adding a task (an empty title is an error) and moving it.
 * - tasks.update, comments.add: a task, editing it (a blank title is an error that keeps the other edits) and commenting.
 * - projects.addMember: members, adding someone by email (an unknown email is an error).
 * The sign-in errors themselves are in taskboard.spec.ts; the server's answers to each post are in tests/TaskBoard.Tests.
 * The project pages render inside projects/[id]/loading.tsx's Suspense boundary, and the board is larger than React's
 * progressiveChunkSize (12.8 kB): they show without JavaScript because the sidecar writes a boundary that is ready
 * with the shell in place, not after it.
 */

test.use({ javaScriptEnabled: false });

/** An account with a project, made with JavaScript in a context of its own; with a task title, also its task 1. */
async function accountWithProject(browser: Browser, name: string, project: string, task?: string) {
  const context = await browser.newContext({ javaScriptEnabled: true });
  const page = await context.newPage();
  const email = await register(page, name);
  const slug = await createProject(page, project);
  if (task !== undefined) {
    await page.goto(`/projects/${slug}`);
    await page.getByLabel('New task in Backlog').fill(task);
    await page.getByRole('button', { name: 'Add task to Backlog' }).click();
    await expect(page.getByRole('link', { name: task })).toBeVisible();
  }
  await context.close();
  return { email, slug };
}

/** Signs in without JavaScript and opens one of the project's pages. */
async function signInTo(page: Page, account: { email: string; slug: string }, path = '') {
  await page.goto('/login');
  await signIn(page, account.email);
  await expect(page).toHaveURL(/\/projects$/);
  await page.goto(`/projects/${account.slug}${path}`);
}

test.describe('without JavaScript', () => {
  test('signing up, starting a project and signing out', async ({ page }) => {
    const email = `e2e-${unique()}@taskboard.test`;
    await page.goto('/register');
    await page.getByLabel('Name').fill('Plain Poster');
    await page.getByLabel('Email').fill(email);
    await page.getByLabel('Password').fill(PASSWORD);
    await page.getByRole('button', { name: 'Create account' }).click();
    await expect(page).toHaveURL(/\/projects$/);
    await expect(page.getByText(email)).toBeVisible();

    const slug = `e2e${unique()}`;
    await page.getByLabel('Name').fill('Plain project');
    await page.getByLabel('Key').fill(slug);
    await page.getByRole('button', { name: 'Create project' }).click();
    await expect(page).toHaveURL(/\/projects$/);
    const projects = page.getByRole('list', { name: 'Your projects' });
    await expect(projects.getByRole('link', { name: /Plain project/ })).toContainText('0 open');

    await page.getByLabel('Name').fill('Same key again');
    await page.getByLabel('Key').fill(slug);
    await page.getByRole('button', { name: 'Create project' }).click();
    await expect(page.getByText('That key is taken. Try another.')).toBeVisible();
    await expect(page.getByLabel('Name')).toHaveValue('Same key again');
    await expect(projects.getByRole('link')).toHaveCount(1);

    await page.getByRole('button', { name: 'Sign out' }).click();
    await expect(page).toHaveURL(/\/$/);
    await expect(page.getByRole('navigation', { name: 'Account' }).getByRole('link', { name: 'Sign in' })).toBeVisible();
    const response = await page.goto('/projects');
    expect(response?.status()).toBe(401);
  });

  test('signing in from a page that needs it goes back to that page', async ({ page, browser }) => {
    const account = await accountWithProject(browser, 'Returning Owner', 'Return trip');
    const response = await page.goto(`/projects/${account.slug}/settings`);
    expect(response?.status()).toBe(401);
    await expect(page.getByRole('heading', { name: 'Sign in to continue' })).toBeVisible();

    // Without JavaScript the page cannot send the visitor on by itself; its link carries the return URL.
    await page.getByRole('link', { name: 'Sign in' }).click();
    await expect(page).toHaveURL(new RegExp(`/login\\?returnUrl=%2Fprojects%2F${account.slug}%2Fsettings$`));
    await page.getByLabel('Email').fill(account.email);
    await page.getByLabel('Password').fill('not the password');
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByText('That email and password do not match.')).toBeVisible();
    await signIn(page, account.email);

    await expect(page).toHaveURL(new RegExp(`/projects/${account.slug}/settings$`));
    // The project's header comes from its layout; the page itself from inside the loading boundary.
    await expect(page.getByRole('heading', { level: 1, name: 'Return trip' })).toBeVisible();
    await expect(page.getByRole('list', { name: 'Members' })).toContainText('Returning Owner');
  });

  test("going back after signing out shows the page as a visitor sees it, not the signed-out user's", async ({ page, browser }) => {
    const account = await accountWithProject(browser, 'Leaving User', 'Left behind');
    await page.goto('/login');
    await signIn(page, account.email);
    await expect(page).toHaveURL(/\/projects$/);
    await expect(page.getByText('Leaving User')).toBeVisible();

    await page.getByRole('button', { name: 'Sign out' }).click();
    await expect(page).toHaveURL(/\/$/);
    // A browser shows a page it kept again without asking the server, unless the page said not to keep it.
    await page.goBack();

    await expect(page).toHaveURL(/\/projects$/);
    await expect(page.getByRole('heading', { name: 'Sign in to continue' })).toBeVisible();
    await expect(page.getByText('Leaving User')).toHaveCount(0);
    await expect(page.getByText('Left behind')).toHaveCount(0);
  });

  test('the board: adding a task, an empty title, and moving it', async ({ page, browser }) => {
    const account = await accountWithProject(browser, 'Board Poster', 'Plain board');
    await signInTo(page, account);
    const todo = page.getByRole('list', { name: 'Todo', exact: true });
    const inProgress = page.getByRole('list', { name: 'In progress', exact: true });

    await page.getByRole('button', { name: 'Add task to Todo' }).click();
    await expect(page.getByText('Give the task a title.').first()).toBeVisible();
    await expect(todo.getByRole('listitem')).toHaveCount(0);

    await page.getByLabel('New task in Todo').fill('Posted without scripts');
    await page.getByRole('button', { name: 'Add task to Todo' }).click();
    await expect(page).toHaveURL(new RegExp(`/projects/${account.slug}$`));
    await expect(todo.getByRole('link', { name: 'Posted without scripts' })).toBeVisible();
    await expect(page.getByLabel('Task counts')).toContainText('Todo1');

    await page.getByRole('button', { name: `Move ${account.slug.toUpperCase()}-1 to In progress` }).click();
    await expect(page).toHaveURL(new RegExp(`/projects/${account.slug}$`));
    await expect(inProgress.getByRole('link', { name: 'Posted without scripts' })).toBeVisible();
    await expect(todo.getByRole('listitem')).toHaveCount(0);
  });

  test('a task: a blank title keeps the other edits, then saving and commenting', async ({ page, browser }) => {
    // Made with JavaScript and opened directly; the board flow above adds tasks without it.
    const account = await accountWithProject(browser, 'Task Poster', 'Plain tasks', 'Edit me plainly');
    await signInTo(page, account, '/tasks/1');
    await expect(page.getByLabel('Title')).toHaveValue('Edit me plainly');

    // Blank passes the browser's required check; the C# [Required] rejects it.
    await page.getByLabel('Title').fill('   ');
    await page.getByLabel('Description').fill('Typed before the title was fixed.');
    await page.getByLabel('Priority').selectOption('urgent');
    await page.getByRole('button', { name: 'Save changes' }).click();
    await expect(page.getByText('Give the task a title.')).toBeVisible();
    await expect(page.getByLabel('Description')).toHaveValue('Typed before the title was fixed.');
    await expect(page.getByLabel('Priority')).toHaveValue('urgent');

    await page.getByLabel('Title').fill('Edited plainly');
    await page.getByRole('button', { name: 'Save changes' }).click();
    await expect(page).toHaveURL(new RegExp(`/projects/${account.slug}/tasks/1$`));
    await expect(page.getByLabel('Title')).toHaveValue('Edited plainly');
    await expect(page.getByLabel('Description')).toHaveValue('Typed before the title was fixed.');
    await expect(page.getByLabel('Priority')).toHaveValue('urgent');

    await page.getByLabel('Your comment').fill('A comment without scripts.');
    await page.getByRole('button', { name: 'Comment' }).click();
    await expect(page).toHaveURL(new RegExp(`/projects/${account.slug}/tasks/1$`));
    await expect(page.getByRole('list', { name: 'Comments' })).toContainText('A comment without scripts.');
  });

  test('members: an unknown email is an error, a known one is added', async ({ page, browser }) => {
    const account = await accountWithProject(browser, 'Member Poster', 'Plain members');
    const colleague = await registerElsewhere(browser, 'Plain Colleague');
    // The project's tabs are in its layout, outside the loading boundary, so they show on the board too.
    await signInTo(page, account);
    await page.getByRole('navigation', { name: 'Project', exact: true }).getByRole('link', { name: 'Members', exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/projects/${account.slug}/settings$`));

    const nobody = `nobody-${unique()}@taskboard.test`;
    await page.getByLabel('Email').fill(nobody);
    await page.getByRole('button', { name: 'Add member' }).click();
    await expect(page.getByText('Nobody has an account with that email yet. Ask them to sign up first.')).toBeVisible();
    await expect(page.getByLabel('Email')).toHaveValue(nobody);

    await page.getByLabel('Email').fill(colleague);
    await page.getByRole('button', { name: 'Add member' }).click();
    await expect(page).toHaveURL(new RegExp(`/projects/${account.slug}/settings$`));
    await expect(page.getByRole('list', { name: 'Members' })).toContainText('Plain Colleague');
  });
});
