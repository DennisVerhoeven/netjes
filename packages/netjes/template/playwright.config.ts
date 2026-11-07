import { defineConfig, devices } from '@playwright/test';

// Each project runs the app as its own ASP.NET Core process: one in Development (the Vite dev server) and one in
// Production (the `npm run build` output). `npm run test:e2e` builds the .NET app first; install the browser once with
// `npx playwright install chromium`. E2E_DEV_PORT, E2E_PROD_PORT and E2E_VITE_PORT move the ports, so that several runs
// can share a machine. With CI set, Playwright always starts fresh servers; otherwise it reuses one that is listening.
const DEV_URL = `http://127.0.0.1:${process.env['E2E_DEV_PORT'] ?? '5090'}`;
const PROD_URL = `http://127.0.0.1:${process.env['E2E_PROD_PORT'] ?? '5091'}`;
const VITE_PORT = process.env['E2E_VITE_PORT'] ?? '5190';

export default defineConfig({
  testDir: './e2e',
  forbidOnly: !!process.env['CI'],
  retries: process.env['CI'] ? 1 : 0,
  use: { ...devices['Desktop Chrome'], trace: 'retain-on-failure' },
  projects: [
    { name: 'development', use: { baseURL: DEV_URL } },
    { name: 'production', use: { baseURL: PROD_URL } },
  ],
  webServer: [
    {
      command: 'dotnet run --no-build --no-launch-profile',
      url: `${DEV_URL}/healthz`,
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: DEV_URL, Netjes__ViteDevPort: VITE_PORT },
      reuseExistingServer: !process.env['CI'],
      timeout: 120_000,
    },
    {
      command: 'npm run build && dotnet run --no-build --no-launch-profile',
      url: `${PROD_URL}/healthz`,
      env: { ASPNETCORE_ENVIRONMENT: 'Production', ASPNETCORE_URLS: PROD_URL },
      reuseExistingServer: !process.env['CI'],
      timeout: 180_000,
    },
  ],
});
