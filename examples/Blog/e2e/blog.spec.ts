import { mkdir, rename, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { type APIRequestContext, expect, test } from '@playwright/test';
import { isMarked, markWindow, watchForErrors } from './helpers.ts';

test.describe('server rendering', () => {
  test.use({ javaScriptEnabled: false });

  test('pages are complete HTML before any JavaScript runs', async ({ page }) => {
    const response = await page.goto('/blog/react-pages-on-dotnet');

    expect(response?.status()).toBe(200);
    await expect(page).toHaveTitle('React pages, served by ASP.NET Core · Netjes Blog');
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('React pages, served by ASP.NET Core');
    await expect(page.getByRole('button', { name: /likes?/ })).toBeVisible();
  });

  test('a post sends its Open Graph image with its size and alt as head tags', async ({ page }) => {
    await page.goto('/blog/react-pages-on-dotnet');

    const tags = await page.locator('head meta[property^="og:image"]').evaluateAll((metas) =>
      metas.map((meta) => [meta.getAttribute('property'), meta.getAttribute('content')]),
    );
    expect(tags).toEqual([
      ['og:image', new URL('/hero.png', page.url()).href],
      ['og:image:width', '1600'],
      ['og:image:height', '800'],
      ['og:image:alt', 'A soft gradient'],
    ]);
  });

  test('the hero image is server-rendered with a srcset from the optimizer, eager and high priority', async ({ page }) => {
    await page.goto('/');

    const hero = page.getByRole('img', { name: 'Gradient' });
    await expect(hero).toHaveAttribute('srcset', /\/_netjes\/image\?url=%2Fhero\.png&w=640&q=75 640w/);
    await expect(hero).toHaveAttribute('loading', 'eager');
    await expect(hero).toHaveAttribute('fetchpriority', 'high');
    await expect(page.locator('head link[rel="preload"][as="image"]')).toHaveCount(1);
  });

  test('the image optimizer resizes and serves WebP to browsers that accept it', async ({ request }) => {
    const webp = await request.get('/_netjes/image?url=%2Fhero.png&w=640&q=75', { headers: { Accept: 'image/webp,*/*' } });
    const png = await request.get('/_netjes/image?url=%2Fhero.png&w=640&q=75', { headers: { Accept: 'image/png' } });
    const original = await request.get('/hero.png');

    expect(webp.status()).toBe(200);
    expect(webp.headers()['content-type']).toBe('image/webp');
    expect(webp.headers()['cache-control']).toContain('max-age=');
    expect(png.headers()['content-type']).toBe('image/png');
    expect((await webp.body()).length).toBeLessThan((await original.body()).length);
    expect((await request.get('/_netjes/image?url=%2Fhero.png&w=641')).status()).toBe(400);
  });

  test('pages are styled with Tailwind before any JavaScript runs', async ({ page }) => {
    await page.goto('/');

    await expect(page.locator('body')).toHaveCSS('background-color', 'rgb(247, 245, 240)');
    await expect(page.getByRole('link', { name: 'Read the docs' })).toHaveCSS('background-color', 'rgb(180, 68, 31)');
  });

  test('unknown URLs render the 404 page with a 404 status', async ({ page }) => {
    const response = await page.goto('/no/such/page');

    expect(response?.status()).toBe(404);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Nothing at /no/such/page');
  });

  test('a loader returning NotFound renders the 404 page', async ({ page }) => {
    const response = await page.goto('/blog/missing-post');

    expect(response?.status()).toBe(404);
    await expect(page.getByText('404')).toBeVisible();
  });

  test('forms work without JavaScript', async ({ page }) => {
    const name = `NoJS ${Date.now()}`;
    await page.goto('/guestbook');

    await page.getByLabel('Name').fill(name);
    await page.getByLabel('Message').fill('Posted with a plain form submission.');
    await page.getByRole('button', { name: 'Sign the guestbook' }).click();

    await expect(page).toHaveURL(/\/guestbook$/);
    await expect(page.getByRole('list', { name: 'Entries' })).toContainText(name);
  });
});

test.describe('in the browser', () => {
  test('hydrates and navigates between pages without reloading', async ({ page }) => {
    const errors = watchForErrors(page);
    await page.goto('/');
    await markWindow(page);

    await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Blog' }).click();
    await expect(page.getByRole('heading', { name: 'All posts' })).toBeVisible();
    await expect(page).toHaveTitle('Blog · Netjes Blog');
    await page.getByRole('link', { name: 'File-based routing without a Node server' }).click();
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('File-based routing without a Node server');

    expect(await isMarked(page)).toBe(true);
    expect(errors).toEqual([]);
  });

  test('a nonce-based Content-Security-Policy allows exactly the scripts Netjes rendered', async ({ page }) => {
    const errors = watchForErrors(page);
    const response = await page.goto('/blog/react-pages-on-dotnet');
    await markWindow(page);

    const policy = response?.headers()['content-security-policy'] ?? '';
    const nonce = /'nonce-([^']+)'/.exec(policy)?.[1];
    expect(nonce).toBeTruthy();
    const scriptNonces = await page.locator('script[type="module"]').evaluateAll((scripts) =>
      scripts.map((script) => (script as HTMLScriptElement).nonce),
    );
    expect(scriptNonces.length).toBeGreaterThan(0);
    expect(new Set(scriptNonces)).toEqual(new Set([nonce]));
    // An injected script without the nonce is refused.
    const injected = await page.evaluate(() => {
      const holder = document.createElement('div');
      holder.innerHTML = '<img src="data:image/png;base64,AAAA" onerror="window.__xss = true">';
      document.body.append(holder);
      return new Promise((resolve) => setTimeout(() => resolve((window as unknown as Record<string, unknown>).__xss === true), 100));
    });
    expect(injected).toBe(false);
    await page.getByRole('link', { name: '← All posts' }).click();
    await expect(page.getByRole('heading', { name: 'All posts' })).toBeVisible();
    expect(await isMarked(page)).toBe(true);
    expect(errors.filter((e) => !e.includes('Content Security Policy'))).toEqual([]);
  });

  test('Core Web Vitals are reported to the server', async ({ page }) => {
    const beacon = page.waitForRequest((request) => request.url().endsWith('/_netjes/vitals') && request.method() === 'POST');
    await page.goto('/about');

    const report = JSON.parse((await beacon).postData() ?? '{}');
    expect(['TTFB', 'FCP', 'LCP', 'CLS', 'INP']).toContain(report.name);
    expect(report.route).toBe('/about');
    expect((await (await beacon).response())?.status()).toBe(204);
  });

  test('back and forward restore pages on the client', async ({ page }) => {
    await page.goto('/about');
    await markWindow(page);
    await page.getByRole('link', { name: 'Docs' }).click();
    await expect(page.getByRole('heading', { name: 'Introduction' })).toBeVisible();

    await page.goBack();
    await expect(page.getByRole('heading', { name: 'About' })).toBeVisible();
    await page.goForward();
    await expect(page.getByRole('heading', { name: 'Introduction' })).toBeVisible();

    expect(await isMarked(page)).toBe(true);
  });

  test('search params reach the C# loader', async ({ page }) => {
    await page.goto('/blog');
    await markWindow(page);

    await page.getByRole('navigation', { name: 'Filter by tag' }).getByRole('link', { name: 'routing' }).click();

    await expect(page).toHaveURL(/\/blog\?tag=routing$/);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Posts tagged “routing”');
    await expect(page.getByRole('article')).toHaveCount(1);
    expect(await isMarked(page)).toBe(true);
  });

  test('catch-all routes serve nested docs paths', async ({ page }) => {
    await page.goto('/docs/getting-started/routing');
    await markWindow(page);

    await page.getByRole('navigation', { name: 'Documentation' }).getByRole('link', { name: 'Deployment' }).click();

    await expect(page).toHaveURL(/\/docs\/guides\/deployment$/);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Deployment');
    expect(await isMarked(page)).toBe(true);
  });

  test('the like button calls a server action and keeps the count in TanStack Query', async ({ page }) => {
    const errors = watchForErrors(page);
    // The page is output-cached, so the count it was rendered with can be behind; the client refetches it.
    const refetched = page.waitForResponse((response) => response.url().endsWith('/api/posts/forms-that-work-without-javascript/likes'));
    await page.goto('/blog/forms-that-work-without-javascript');
    await markWindow(page);
    await refetched;
    const button = page.getByRole('button', { name: /likes?/ });
    await expect(button).toBeEnabled();

    const action = page.waitForResponse((response) => response.url().endsWith('/_netjes/actions/posts.like'));
    await button.click();

    const response = await action;
    expect(response.status()).toBe(200);
    const { result } = (await response.json()) as { result: { likes: number } };
    await expect(button).toContainText(`${result.likes} like`);
    await expect(button).toBeEnabled();
    expect(errors).toEqual([]);
  });

  test('a streaming page is interactive before its slow part arrives', async ({ page }) => {
    const errors = watchForErrors(page);
    // Do not wait for the load event: that fires only when the whole stream has ended.
    await page.goto('/streaming', { waitUntil: 'commit' });

    await expect(page.getByText('Preparing the slow report…')).toBeVisible();
    await expect(async () => {
      await page.getByRole('button', { name: /^clicks/ }).click();
      await expect(page.getByRole('button', { name: /^clicks [1-9]/ })).toBeVisible({ timeout: 100 });
    }).toPass({ timeout: 2000 });
    await expect(page.getByText('Preparing the slow report…')).toBeVisible();

    await expect(page.getByRole('status')).toHaveText('Report ready: 42 items, from a deliberately slow C# task.', { timeout: 10_000 });
    expect(errors).toEqual([]);
  });

  test('deferred data streams into the page after it is shown', async ({ page }) => {
    const errors = watchForErrors(page);
    await page.goto('/blog/react-pages-on-dotnet');

    await expect(page.getByRole('list', { name: 'Related posts' }).getByRole('link', { name: 'File-based routing without a Node server' })).toBeVisible();
    await markWindow(page);
    await page.getByRole('link', { name: 'File-based routing without a Node server' }).click();

    await expect(page.getByRole('heading', { level: 1 })).toHaveText('File-based routing without a Node server');
    await expect(page.getByRole('list', { name: 'Related posts' }).getByRole('link', { name: 'React pages, served by ASP.NET Core' })).toBeVisible();
    expect(await isMarked(page)).toBe(true);
    expect(errors).toEqual([]);
  });

  test('app router: the docs layout loads its table of contents once, titles come from C# through templates', async ({ page }) => {
    const errors = watchForErrors(page);
    const segmentHeaders: (string | null)[] = [];
    page.on('request', (request) => {
      if (request.headers()['x-netjes-data']) segmentHeaders.push(request.headers()['x-netjes-segments'] ?? null);
    });
    await page.goto('/docs/introduction');
    await expect(page).toHaveTitle('Introduction · Docs · Netjes Blog');
    await markWindow(page);

    await page.getByRole('navigation', { name: 'Documentation' }).getByRole('link', { name: 'Routing' }).click();

    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Routing');
    await expect(page).toHaveTitle('Routing · Docs · Netjes Blog');
    expect(segmentHeaders.some((keys) => keys !== null && keys.length > 0)).toBe(true);
    expect(await isMarked(page)).toBe(true);
    expect(errors).toEqual([]);
  });

  test('app router: an unknown doc renders the docs not-found page inside the docs layout', async ({ page }) => {
    const response = await page.goto('/docs/no-such-doc');

    expect(response?.status()).toBe(404);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('No doc at /docs/no-such-doc');
    await expect(page.getByRole('navigation', { name: 'Documentation' })).toBeVisible();
  });

  test('the docs not-found page has exactly one title, the docs one', async ({ page }) => {
    await page.goto('/docs/no-such-doc');

    await expect(page.locator('title')).toHaveCount(1);
    await expect(page).toHaveTitle('No such doc · Docs · Netjes Blog');
  });

  test('the guestbook shows validation errors, then signs without a reload', async ({ page }) => {
    const errors = watchForErrors(page);
    const name = `Ada ${Date.now()}`;
    await page.goto('/guestbook');
    await markWindow(page);

    await page.getByRole('button', { name: 'Sign the guestbook' }).click();
    await expect(page.getByText('Tell us who you are.')).toBeVisible();
    await expect(page.getByLabel('Name')).toHaveAttribute('aria-invalid', 'true');

    await page.getByLabel('Name').fill(name);
    await page.getByLabel('Message').fill('Signed through the client router.');
    await page.getByRole('button', { name: 'Sign the guestbook' }).click();

    await expect(page.getByRole('list', { name: 'Entries' })).toContainText(name);
    await expect(page.getByText('Tell us who you are.')).toBeHidden();
    expect(await isMarked(page)).toBe(true);
    // The 422 response for the invalid submission is logged by the browser; anything else is a real error.
    expect(errors.filter((e) => !e.includes('422'))).toEqual([]);
  });

  test('the 404 page hydrates and its links navigate on the client', async ({ page }) => {
    const errors = watchForErrors(page);
    const response = await page.goto('/blog/not-a-post');
    expect(response?.status()).toBe(404);
    await markWindow(page);

    await page.getByRole('main').getByRole('link', { name: 'all posts' }).click();

    await expect(page.getByRole('heading', { name: 'All posts' })).toBeVisible();
    expect(await isMarked(page)).toBe(true);
    // The browser logs the document's own 404 status; anything else is a real error.
    expect(errors.filter((e) => !e.includes('404'))).toEqual([]);
  });
});

test.describe('a page whose module does not load', () => {
  test.beforeEach(({}, testInfo) => test.skip(testInfo.project.name !== 'production', 'the built chunks are the ones a deploy removes'));

  test('stays as the server rendered it, says which module, and the next link loads in full', async ({ page, request }) => {
    // The about page's own chunk: the one page chunk its document preloads.
    const html = await (await request.get('/about')).text();
    const chunk = /<link rel="modulepreload" href="([^"]*\/page-[^"]+\.js)"/.exec(html)?.[1];
    expect(chunk).toBeTruthy();
    let requests = 0;
    await page.route(`**${chunk}`, (route) => {
      requests++;
      return route.abort('internetdisconnected');
    });
    const logged: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error' && message.text().startsWith('netjes:')) logged.push(message.text());
    });
    const uncaught: string[] = [];
    page.on('pageerror', (error) => uncaught.push(error.message));

    await page.goto('/about');

    await expect.poll(() => logged.length).toBe(1);
    expect(logged[0]).toContain('app/about/page.tsx');
    // The preload, the import and the import tried once more.
    expect(requests).toBe(3);
    await expect(page.getByRole('heading', { level: 1, name: 'About' })).toBeVisible();
    // The router never started: its links are plain links, which the browser loads in full.
    expect(await page.evaluate(() => window.history.state)).toBeNull();
    await page.evaluate(() => {
      (window as unknown as Record<string, unknown>).__beforeTheLink = true;
    });
    await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Blog' }).click();
    await expect(page.getByRole('heading', { name: 'All posts' })).toBeVisible();
    expect(await page.evaluate(() => (window as unknown as Record<string, unknown>).__beforeTheLink)).toBeUndefined();
    // The next document has its modules, and its router runs.
    await markWindow(page);
    expect(logged).toHaveLength(1);
    expect(uncaught).toEqual([]);
  });
});

test.describe('development only', () => {
  test.beforeEach(({}, testInfo) => test.skip(testInfo.project.name !== 'development', 'needs the dev server'));

  const app = fileURLToPath(new URL('../app/', import.meta.url));

  /**
   * Adds app/<folder>/page.tsx whole, as an editor saves: written in the test's output folder, then moved in, so no
   * request can find it half written.
   */
  async function addPage(folder: string, heading: string) {
    const staging = test.info().outputPath('staging', folder);
    await mkdir(staging, { recursive: true });
    await writeFile(path.join(staging, 'page.tsx'), `export default function Added() { return <h1>${heading}</h1>; }\n`);
    await mkdir(path.dirname(path.join(app, folder)), { recursive: true });
    await rename(staging, path.join(app, folder));
  }

  /** Waits for .NET's file watcher to route the URL, and returns the first status that is not a 404. */
  async function firstRoutedStatus(request: APIRequestContext, url: string) {
    let status = 404;
    await expect.poll(async () => (status = (await request.get(url)).status()), { timeout: 10_000, intervals: [20] }).not.toBe(404);
    return status;
  }

  test('a new page file is routable without restarting', async ({ page, request }) => {
    expect((await request.get('/about')).status()).toBe(200);
    await addPage('e2e-fresh', 'Fresh page');
    try {
      // From the moment .NET routes it, whether or not Vite's watcher has reported it yet.
      expect(await firstRoutedStatus(request, '/e2e-fresh')).toBe(200);
      await page.goto('/e2e-fresh');
      await expect(page.getByRole('heading', { name: 'Fresh page' })).toBeVisible();
    } finally {
      await rm(path.join(app, 'e2e-fresh'), { recursive: true, force: true });
    }
  });

  // Vite's watcher skips test-results/ folders (Playwright's output); .NET's does not. Pages there are routed before
  // Vite ever hears of them, and adding them reloads no other test's page.

  test('a page Vite never hears of renders and hydrates', async ({ page, request }) => {
    // A page first, so the server's and the browser's page module maps are built before the file exists.
    const errors = watchForErrors(page);
    await page.goto('/about');
    await markWindow(page);
    await addPage('test-results/unwatched', 'Unwatched page');
    try {
      expect(await firstRoutedStatus(request, '/test-results/unwatched')).toBe(200);
      await page.goto('/test-results/unwatched');
      await expect(page.getByRole('heading', { name: 'Unwatched page' })).toBeVisible();
      await markWindow(page);
      expect(errors).toEqual([]);
    } finally {
      await rm(path.join(app, 'test-results'), { recursive: true, force: true });
    }
  });

  test('a page folder renamed answers at its new URL, and a deleted one is gone', async ({ request }) => {
    expect((await request.get('/about')).status()).toBe(200);
    await addPage('test-results/before', 'Renamed page');
    try {
      expect(await firstRoutedStatus(request, '/test-results/before')).toBe(200);

      await rename(path.join(app, 'test-results/before'), path.join(app, 'test-results/after'));
      expect(await firstRoutedStatus(request, '/test-results/after')).toBe(200);
      await expect.poll(async () => (await request.get('/test-results/before')).status(), { timeout: 10_000 }).toBe(404);

      await rm(path.join(app, 'test-results/after'), { recursive: true, force: true });
      await expect.poll(async () => (await request.get('/test-results/after')).status(), { timeout: 10_000 }).toBe(404);
    } finally {
      await rm(path.join(app, 'test-results'), { recursive: true, force: true });
    }
  });
});
