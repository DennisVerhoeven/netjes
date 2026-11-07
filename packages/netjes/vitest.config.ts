import react from '@vitejs/plugin-react';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      'virtual:netjes/pages': fileURLToPath(new URL('./test/stub-pages.ts', import.meta.url)),
      'virtual:netjes/query': fileURLToPath(new URL('./test/stub-query.ts', import.meta.url)),
    },
  },
  test: {
    // template/ is the `netjes new` starter, with its own tests that run in the scaffolded app.
    include: ['test/**/*.test.{ts,tsx}'],
    environment: 'jsdom',
    setupFiles: ['./test/setup.ts'],
    // As netjes({ query }) would provide them to an app's tests; a test file that imports src/testing-vitest.ts, the
    // plugin's setup file, hands them to createTestApp.
    provide: {
      'netjes:query': {
        queries: { staleTime: Number.POSITIVE_INFINITY, retry: 1, retryDelay: 1000, gcTime: 5000 },
        mutations: { retry: 1, retryDelay: 1000 },
      },
    },
  },
});
