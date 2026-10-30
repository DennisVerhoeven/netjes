import { type FullConfig, chromium } from '@playwright/test';
import { PASSWORD, unique } from './helpers.ts';

/**
 * Warms each server once, before the first test: the first sign-up of a freshly started process pays for the EF model,
 * the connection pool, the password hasher's first run and the first server render, which took over four seconds on a
 * loaded machine. The tests keep their short timeouts; only this one request waits for the cold start.
 */
export default async function globalSetup(config: FullConfig) {
  const browser = await chromium.launch();
  try {
    for (const project of config.projects) {
      const context = await browser.newContext({ baseURL: project.use.baseURL });
      const page = await context.newPage();
      await page.goto('/register');
      await page.getByLabel('Name').fill('Warm-up');
      await page.getByLabel('Email').fill(`warmup-${unique()}@taskboard.test`);
      await page.getByLabel('Password').fill(PASSWORD);
      await page.getByRole('button', { name: 'Create account' }).click();
      await page.waitForURL(/\/projects$/, { timeout: 90_000 });
      await context.close();
    }
  } finally {
    await browser.close();
  }
}
