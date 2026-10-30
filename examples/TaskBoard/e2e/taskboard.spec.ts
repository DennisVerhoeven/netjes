import { expect, test } from '@playwright/test';
import { PASSWORD, createProject, isMarked, markWindow, register, registerElsewhere, unique, watchForErrors } from './helpers.ts';

test.describe('the main flow', () => {
  test('register, create a project, add a task, move it and comment, all without a page reload', async ({ page }) => {
    const errors = watchForErrors(page);
    await register(page, 'Ada Lovelace');
    expect(await isMarked(page)).toBe(true);

    const slug = await createProject(page, 'Launch');
    await expect(page.getByRole('navigation', { name: 'Projects' }).getByRole('link', { name: /Launch/ })).toBeVisible();
    await page.getByRole('list', { name: 'Your projects' }).getByRole('link', { name: /Launch/ }).click();
    await expect(page.getByRole('heading', { level: 1, name: 'Launch' })).toBeVisible();
    await expect(page).toHaveTitle('Launch · TaskBoard');

    await page.getByLabel('New task in Todo').fill('Write the announcement');
    await page.getByRole('button', { name: 'Add task to Todo' }).click();
    const todo = page.getByRole('list', { name: 'Todo', exact: true });
    await expect(todo.getByRole('link', { name: 'Write the announcement' })).toBeVisible();
    await expect(page.getByLabel('Task counts')).toContainText('Todo1');

    const key = `${slug.toUpperCase()}-1`;
    await page.getByRole('button', { name: `Move ${key} to In progress` }).click();
    const inProgress = page.getByRole('list', { name: 'In progress', exact: true });
    await expect(inProgress.getByRole('link', { name: 'Write the announcement' })).toBeVisible();
    await expect(todo.getByRole('link', { name: 'Write the announcement' })).toHaveCount(0);

    await inProgress.getByRole('link', { name: 'Write the announcement' }).click();
    await expect(page).toHaveTitle(`${key} Write the announcement · TaskBoard`);
    await page.getByLabel('Your comment').fill('First draft is in the doc.');
    await page.getByRole('button', { name: 'Comment' }).click();
    await expect(page.getByRole('list', { name: 'Comments' })).toContainText('First draft is in the doc.');
    // The streamed activity feed (Page.Defer) fills in after the page.
    await expect(page.getByRole('list', { name: 'Activity' })).toContainText(`moved ${key} to In progress`);

    expect(await isMarked(page)).toBe(true);
    expect(errors).toEqual([]);
  });

  test('a task edit is validated and saved', async ({ page }) => {
    const errors = watchForErrors(page);
    await register(page, 'Grace Hopper');
    const slug = await createProject(page, 'Compiler');
    await page.goto(`/projects/${slug}`);
    await markWindow(page);
    await page.getByLabel('New task in Backlog').fill('Write a manual');
    await page.getByRole('button', { name: 'Add task to Backlog' }).click();
    await page.getByRole('link', { name: 'Write a manual' }).click();

    // Blank passes the browser's required check; the C# [Required] rejects it. What else was typed stays.
    await page.getByLabel('Title').fill('   ');
    await page.getByLabel('Description').fill('Chapters one to three.');
    await page.getByRole('button', { name: 'Save changes' }).click();
    await expect(page.getByText('Give the task a title.')).toBeVisible();
    await expect(page.getByLabel('Description')).toHaveValue('Chapters one to three.');
    await page.getByLabel('Title').fill('Write the manual');
    await page.getByLabel('Priority').selectOption('urgent');
    await page.getByLabel('Assignee').selectOption({ label: 'Grace Hopper' });
    await page.getByRole('button', { name: 'Save changes' }).click();

    await expect(page.getByRole('status')).toHaveText('Saved.');
    await expect(page.getByRole('list', { name: 'Activity' })).toContainText('updated the title, the description, the priority to urgent and the assignee');
    expect(await isMarked(page)).toBe(true);
    expect(errors).toEqual([]);
  });

  test('board filters live in the URL', async ({ page }) => {
    await register(page, 'Filter Person');
    const slug = await createProject(page, 'Filters');
    await page.goto(`/projects/${slug}`);
    await markWindow(page);
    for (const title of ['Fix the login bug', 'Write docs', 'Fix the footer']) {
      await page.getByLabel('New task in Todo').fill(title);
      await page.getByRole('button', { name: 'Add task to Todo' }).click();
      await expect(page.getByRole('link', { name: title })).toBeVisible();
    }

    await page.getByLabel('Search').fill('fix');
    await page.getByRole('button', { name: 'Apply' }).click();

    await expect(page).toHaveURL(/\?q=fix/);
    const todo = page.getByRole('list', { name: 'Todo', exact: true });
    await expect(todo.getByRole('listitem')).toHaveCount(2);
    await page.getByRole('link', { name: 'Clear filters' }).click();
    await expect(todo.getByRole('listitem')).toHaveCount(3);
  });
});

test.describe('access', () => {
  test('signed-out visitors are sent to sign in and back', async ({ page, browser }) => {
    const email = await registerElsewhere(browser);
    await page.goto('/projects');
    await markWindow(page);

    await expect(page).toHaveURL(/\/login\?returnUrl=%2Fprojects$/);
    await page.getByLabel('Email').fill(email);
    await page.getByLabel('Password').fill('correct horse battery');
    await page.getByRole('button', { name: 'Sign in' }).click();

    await expect(page).toHaveURL(/\/projects$/);
    await expect(page.getByRole('heading', { level: 1, name: 'Projects' })).toBeVisible();
  });

  test('other people cannot open a project', async ({ page, browser }) => {
    await register(page, 'Owner');
    const slug = await createProject(page, 'Private');
    const strangerContext = await browser.newContext();
    const stranger = await strangerContext.newPage();
    await register(stranger, 'Stranger');

    const response = await stranger.goto(`/projects/${slug}`);

    expect(response?.status()).toBe(403);
    await expect(stranger.getByRole('heading', { name: 'You are not on this project' })).toBeVisible();
    await expect(stranger.getByText('Private')).toHaveCount(0);
    await strangerContext.close();
  });
});

test.describe('as soon as the page is hydrated', () => {
  test('a form submitted before the router is ready still signs up through it', async ({ page }) => {
    const errors = watchForErrors(page);
    const email = `e2e-${unique()}@taskboard.test`;
    // React reports each commit to the DevTools hook. This stand-in fills in the form when React commits the hydrated
    // page and clicks in the next task, where a click that came in while React hydrated lands: after the commit, and
    // before React runs the effects in which the router registers itself for server actions.
    await page.addInitScript(
      ({ email, password }) => {
        if (window.location.pathname !== '/register') return;
        let clicked = false;
        (window as unknown as Record<string, unknown>).__REACT_DEVTOOLS_GLOBAL_HOOK__ = {
          renderers: new Map(),
          supportsFiber: true,
          inject: () => 1,
          onScheduleFiberRoot() {},
          onCommitFiberUnmount() {},
          onCommitFiberRoot() {
            if (clicked) return;
            clicked = true;
            const field = (name: string) => document.querySelector<HTMLInputElement>(`input[name="${name}"]`)!;
            field('name').value = 'Quick Clicker';
            field('email').value = email;
            field('password').value = password;
            (window as unknown as Record<string, unknown>).__mark = true;
            const channel = new MessageChannel();
            channel.port1.onmessage = () => document.querySelector<HTMLButtonElement>('button[type="submit"]')!.click();
            channel.port2.postMessage(null);
          },
        };
      },
      { email, password: PASSWORD },
    );

    await page.goto('/register');

    await expect(page).toHaveURL(/\/projects$/);
    await expect(page.getByText(email)).toBeVisible();
    // Through the client router: a plain form post would have loaded a new document, without the mark.
    expect(await isMarked(page)).toBe(true);
    expect(errors).toEqual([]);
  });
});

test.describe('without JavaScript', () => {
  test.use({ javaScriptEnabled: false });

  test('signing in works with a plain form post, and errors come back on the page', async ({ page, browser }) => {
    const email = await registerElsewhere(browser);
    await page.goto('/login');

    await page.getByLabel('Email').fill(email);
    await page.getByLabel('Password').fill('not the password');
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByText('That email and password do not match.')).toBeVisible();
    await expect(page.getByLabel('Email')).toHaveValue(email);

    await page.getByLabel('Password').fill('correct horse battery');
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page).toHaveURL(/\/projects$/);
    await expect(page.getByRole('heading', { level: 1, name: 'Projects' })).toBeVisible();
  });
});
