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

/**
 * Collects the Content-Security-Policy violations the browser reports in every document the page loads from now on:
 * the directive, what it refused, the script that tried and the page's path. A refusal that the page's code catches,
 * such as a `new Function` that a policy without 'unsafe-eval' blocks, logs nothing to the console, so
 * `watchForErrors` does not see it; a `securitypolicyviolation` listener does.
 */
export async function watchForViolations(page: Page): Promise<string[]> {
  const violations: string[] = [];
  await page.exposeFunction('__netjesViolation', (violation: string) => {
    violations.push(violation);
  });
  await page.addInitScript(() => {
    const exposed = window as unknown as { __netjesViolation: (violation: string) => void };
    // On the window in the capture phase, ahead of every listener of the page's own, so that none can stop it.
    window.addEventListener(
      'securitypolicyviolation',
      (event) => exposed.__netjesViolation(`${event.effectiveDirective} ${event.blockedURI} from ${event.sourceFile} on ${location.pathname}`),
      true,
    );
  });
  return violations;
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
 * Waits for hydration, then marks the window; the window's mark survives client-side navigations and disappears on a
 * full page load. Netjes puts `data-netjes-hydrated` on `<html>` once the client router runs in the document; until
 * then links are plain links and forms post natively, so a test that clicks or types before this returns can silently
 * take the no-JavaScript path.
 */
export async function markWindow(page: Page) {
  await page.locator('html[data-netjes-hydrated]').waitFor({ state: 'attached' });
  await page.evaluate(() => {
    (window as unknown as Record<string, unknown>).__netjesMark = true;
  });
}

export const isMarked = (page: Page) => page.evaluate(() => (window as unknown as Record<string, unknown>).__netjesMark === true);
