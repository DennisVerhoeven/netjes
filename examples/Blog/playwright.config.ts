import { defineConfig, devices } from '@playwright/test';

// Each project runs the example as its own ASP.NET Core process: one in development (Vite dev server, HMR),
// one in production (the `npm run build` output), one in development mounted under /base (basePath), and one in
// production whose sidecar never becomes ready (the client-render fallback). The export project runs `netjes export`
// on the production build and serves the files. `npm run test:e2e` builds the .NET app first.
// Ports can be moved (E2E_DEV_PORT, E2E_PROD_PORT, E2E_VITE_PORT, E2E_BASE_PORT, E2E_BASE_VITE_PORT,
// E2E_FALLBACK_PORT, E2E_EXPORT_PORT) to run several checkouts side by side.
const DEV_URL = `http://127.0.0.1:${process.env.E2E_DEV_PORT ?? '5090'}`;
const PROD_URL = `http://127.0.0.1:${process.env.E2E_PROD_PORT ?? '5091'}`;
const VITE_PORT = process.env.E2E_VITE_PORT ?? '5190';
const BASE_PORT = process.env.E2E_BASE_PORT ?? '5092';
const BASE_URL = `http://127.0.0.1:${BASE_PORT}`;
const BASE_VITE_PORT = process.env.E2E_BASE_VITE_PORT ?? '5192';
// Next to the base-path server unless set: 5093 by default.
const FALLBACK_URL = `http://127.0.0.1:${process.env.E2E_FALLBACK_PORT ?? String(Number(BASE_PORT) + 1)}`;

// Every test signs the guestbook from 127.0.0.1, and `--repeat-each` repeats them: lift the per-address limit (10 a minute).
const guestbookLimit = { Guestbook__SignsPerMinute: '100000' };

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? 'github' : 'list',
  use: { ...devices['Desktop Chrome'], trace: 'retain-on-failure' },
  projects: [
    { name: 'development', use: { baseURL: DEV_URL }, testIgnore: /(basepath|fallback|export)\.spec\.ts/ },
    { name: 'production', use: { baseURL: PROD_URL }, testIgnore: /(basepath|fallback|export)\.spec\.ts/ },
    // Relative URLs in that spec resolve under /base/.
    { name: 'basepath', use: { baseURL: `${BASE_URL}/base/` }, testMatch: /basepath\.spec\.ts/ },
    { name: 'fallback', use: { baseURL: FALLBACK_URL }, testMatch: /fallback\.spec\.ts/ },
    // `netjes export` of the production build, served from its files; the spec runs the export itself (on any free
    // port, or E2E_EXPORT_PORT).
    { name: 'export', testMatch: /export\.spec\.ts/ },
  ],
  webServer: [
    {
      command: 'dotnet run --no-build --no-launch-profile',
      url: `${DEV_URL}/healthz`,
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: DEV_URL, Netjes__ViteDevPort: VITE_PORT, ...guestbookLimit },
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
    },
    {
      command: 'npm run build && dotnet run --no-build --no-launch-profile',
      url: `${PROD_URL}/healthz`,
      env: { ASPNETCORE_ENVIRONMENT: 'Production', ASPNETCORE_URLS: PROD_URL, ...guestbookLimit },
      reuseExistingServer: !process.env.CI,
      timeout: 180_000,
    },
    {
      command: 'dotnet run --no-build --no-launch-profile',
      url: `${BASE_URL}/base/healthz`,
      // Its own Vite dependency cache: two dev servers optimizing into one directory at once invalidate each other's
      // bundles, and Vite then reloads the page mid-test.
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: BASE_URL,
        Netjes__ViteDevPort: BASE_VITE_PORT,
        PathBase: '/base',
        ...guestbookLimit,
        VITE_CACHE_DIR: 'node_modules/.vite-basepath',
      },
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
    },
    {
      // Serves the production server's build (Playwright starts the servers in order); building again would change
      // dist/ under that server. Its sidecar reports ready once, so the app starts, then is gone for good.
      command: 'dotnet run --no-build --no-launch-profile',
      // Health is Unhealthy without a sidecar, so wait for a page, which the fallback serves.
      url: `${FALLBACK_URL}/about`,
      env: { ASPNETCORE_ENVIRONMENT: 'Production', ASPNETCORE_URLS: FALLBACK_URL, Netjes__SidecarScript: 'e2e/sidecar-down.mjs', ...guestbookLimit },
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
    },
  ],
});
