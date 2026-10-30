import type { ConsoleMessage, Page } from '@playwright/test';

/**
 * Fails the test on console errors and uncaught exceptions, which is how hydration mismatches surface. `ignore` leaves
 * out the console errors about a URL it accepts, such as a request the test knows has nowhere to go.
 */
export function watchForErrors(page: Page, ignore?: (url: URL) => boolean): string[] {
  const errors: string[] = [];
  page.on('console', (message) => {
    const url = locationOf(message);
    if (message.type() === 'error' && !(ignore && url && ignore(url))) errors.push(message.text());
  });
  page.on('pageerror', (error) => errors.push(error.message));
  return errors;
}

/** The URL a console message came from, or null when it has none (`URL.parse` would do, from Node 22.1). */
function locationOf(message: ConsoleMessage): URL | null {
  try {
    return new URL(message.location().url);
  } catch {
    return null;
  }
}

/**
 * Waits for hydration, then marks the window; the mark survives client-side navigations and disappears on a full
 * page load. The router keys the history entry once it is running; until then links are plain links and forms post
 * natively, so a test that clicks or types before this returns can silently take the no-JavaScript path.
 */
export async function markWindow(page: Page) {
  await page.waitForFunction(() => typeof (window.history.state as Record<string, unknown> | null)?.__netjes === 'string');
  await page.evaluate(() => {
    (window as unknown as Record<string, unknown>).__netjesMark = true;
  });
}

export const isMarked = (page: Page) => page.evaluate(() => (window as unknown as Record<string, unknown>).__netjesMark === true);
