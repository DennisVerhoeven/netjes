import { type Browser, type Page, expect } from '@playwright/test';

export const PASSWORD = 'correct horse battery';

/** Fails the test on console errors and uncaught exceptions, which is how hydration mismatches surface. */
export function watchForErrors(page: Page): string[] {
  const errors: string[] = [];
  page.on('console', (message) => {
    // A 401/403/404 page or a 422 validation answer is expected in some tests; the browser logs those statuses.
    if (message.type() === 'error' && !/the server responded with a status of (401|403|404|422)/.test(message.text())) errors.push(message.text());
  });
  page.on('pageerror', (error) => errors.push(error.message));
  return errors;
}

/** Marks the window once the client router owns it; the mark survives client navigations, not full page loads. */
export async function markWindow(page: Page) {
  await page.waitForFunction(() => typeof window.history.state === 'object' && window.history.state !== null && '__netjes' in window.history.state);
  await page.evaluate(() => {
    (window as unknown as Record<string, unknown>).__mark = true;
  });
}

export const isMarked = (page: Page) => page.evaluate(() => (window as unknown as Record<string, unknown>).__mark === true);

export const unique = () => `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;

/** Signs up on /register through the client router and lands on the projects page; returns the email. */
export async function register(page: Page, name: string) {
  const email = `e2e-${unique()}@taskboard.test`;
  await page.goto('/register');
  await markWindow(page);
  await page.getByLabel('Name').fill(name);
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page).toHaveURL(/\/projects$/);
  return email;
}

/**
 * Fills in and sends the login page's form, once the page shows: after a click on a "Sign in" link, the landing page
 * (which has an Email field of its own) can still be on screen.
 */
export async function signIn(page: Page, email: string) {
  await expect(page.getByRole('heading', { level: 1, name: 'Welcome back' })).toBeVisible();
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Sign in' }).click();
}

/** Creates a project from the projects page and waits until it is saved; returns its key. */
export async function createProject(page: Page, name: string) {
  const slug = `e2e${unique()}`;
  await page.getByLabel('Name').fill(name);
  await page.getByLabel('Key').fill(slug);
  await page.getByRole('button', { name: 'Create project' }).click();
  const card = page.getByRole('list', { name: 'Your projects' }).getByRole('link', { name: new RegExp(name) });
  // The card shows at once (useOptimistic); wait for the saved one, which has its task counts.
  await expect(card).toContainText('0 open');
  return slug;
}

/** An account made in a separate, JavaScript-enabled browser context. */
export async function registerElsewhere(browser: Browser, name = 'Returning User') {
  const context = await browser.newContext({ javaScriptEnabled: true });
  const page = await context.newPage();
  const email = await register(page, name);
  await context.close();
  return email;
}
