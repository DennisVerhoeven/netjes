import { defineConfig, devices } from '@playwright/test';
import { dbPort } from './e2e/db-config.mjs';

// Two projects, each its own ASP.NET Core process: development (Vite dev server, HMR) and production (the
// `npm run build` output, migrated by the explicit `migrate` command first). Both use a throwaway Postgres in
// Docker (e2e/db.mjs, removed again by e2e/global-teardown.mjs), with a database each. E2E_DB_PORT moves it
// (default 8432); the container is named after the port, so concurrent runs never share one.
const DEV_PORT = Number(process.env.E2E_DEV_PORT ?? 8090);
const PROD_PORT = Number(process.env.E2E_PROD_PORT ?? 8091);
const VITE_PORT = Number(process.env.E2E_VITE_PORT ?? 8190);
const DEV_URL = `http://127.0.0.1:${DEV_PORT}`;
const PROD_URL = `http://127.0.0.1:${PROD_PORT}`;
const database = (name: string) => `Host=127.0.0.1;Port=${dbPort()};Database=${name};Username=taskboard;Password=e2e`;
// Every test signs up from 127.0.0.1, and a reused server keeps counting across runs: lift the per-address limits.
const authLimits = { AuthLimits__SignInsPerAddress: '100000', AuthLimits__SignUpsPerAddress: '100000' };

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? 'github' : 'list',
  globalSetup: './e2e/global-setup.ts',
  globalTeardown: './e2e/global-teardown.mjs',
  use: { ...devices['Desktop Chrome'], trace: 'retain-on-failure' },
  projects: [
    { name: 'development', use: { baseURL: DEV_URL } },
    { name: 'production', use: { baseURL: PROD_URL } },
  ],
  webServer: [
    {
      command: 'node e2e/db.mjs && dotnet run --no-build --no-launch-profile',
      url: `${DEV_URL}/healthz`,
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: DEV_URL,
        Netjes__ViteDevPort: String(VITE_PORT),
        ConnectionStrings__TaskBoard: database('taskboard_e2e_dev'),
        ...authLimits,
      },
      reuseExistingServer: !process.env.CI,
      timeout: 180_000,
    },
    {
      command: 'node e2e/db.mjs && npm run build && dotnet run --no-build --no-launch-profile -- migrate && dotnet run --no-build --no-launch-profile',
      url: `${PROD_URL}/healthz`,
      env: {
        ASPNETCORE_ENVIRONMENT: 'Production',
        ASPNETCORE_URLS: PROD_URL,
        ConnectionStrings__TaskBoard: database('taskboard_e2e_prod'),
        ...authLimits,
      },
      reuseExistingServer: !process.env.CI,
      timeout: 240_000,
    },
  ],
});
