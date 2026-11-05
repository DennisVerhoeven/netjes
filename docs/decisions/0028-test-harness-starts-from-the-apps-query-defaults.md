# 0028 – The test harness starts from the app's query defaults and keeps its retries, without the waits

Status: accepted

## Context

`createTestApp()` from `netjes/testing` gives the components under test a TanStack Query `QueryClient`. It built that client from the Netjes defaults (a `staleTime` of one minute), then `retry: false` and an infinite `gcTime`, then the test's own `defaultOptions`. It never saw the app's `netjes({ query })` options, which the plugin writes into the browser bundle only. So tests ran with other defaults than the app. A failed query showed its error after one request, where the app tries it three more times, after 1, 2 and 4 seconds (TanStack Query's defaults). A query that failed once and then succeeded showed an error in the test and the data in the app. An app's own `staleTime`, `retry` or refetch options did not apply. Tests passed for behaviour the app's users do not get.

Taking the app's waits along is no answer either: under TanStack Query's defaults they add 7 seconds to every query that keeps failing, more than a test's timeout.

## Decision

- `netjes()` sets `test.provide['netjes:query']` to the `query` option's `defaultOptions`: `{}` when there are none, and nothing with `query: false`. It returns only that key, which Vite merges next to the app's own `provide` keys.
- Netjes's setup file, which the plugin adds to every app's setup files ([ADR 0027](0027-testing-library-advances-vitest-fake-timers.md)), reads the value with `inject()` and keeps it under a global symbol, `Symbol.for('netjes.testing.queryDefaults')`, where `createTestApp` reads it. Vitest loads the setup file by its path and the tests import `netjes/testing` through the package, so the two need not share module instances; a global does not depend on that.
- `createTestApp` merges four sets of defaults, each over the ones before it: the Netjes defaults, the app's, the harness's (`retryDelay: 0` for queries and mutations, `gcTime: Infinity` for queries), and the test's `defaultOptions`. The harness's `retry: false` is gone.
- So the app's `staleTime`, `retry`, refetch options and mutation `retry` apply in tests, an app-wide `retryDelay` or `gcTime` does not, and a test's `defaultOptions` and a query's own options still win over all of them.

## Alternatives considered

- **Keep `retry: false` and document it.** Tests would go on passing for behaviour users do not get.
- **Keep the app's waits too.** About 7 seconds for each query that keeps failing, under TanStack Query's defaults.
- **Call `inject()` in `netjes/testing`.** The harness would import Vitest, and fail to load under any other runner.
- **Import `virtual:netjes/query` in the harness.** It fails without the plugin, depends on Vite processing `netjes` itself under Vitest (the plugin's `ssr.noExternal`, and the Vitest major), and exports only the query shell built from the options, not the options.
- **The app's defaults over the harness's.** The app's waits come back.
- **Add the setup file only to apps that set no `setupFiles`**, as the cleanup file is. Apps with setup files of their own would keep the old defaults, and nothing would tell them.

## Consequences

- A failed query or mutation is tried as often as in the app, with no wait between the tries. A test that expects the error after one request sets `defaultOptions: { queries: { retry: false } }`, the old behaviour.
- A test that checks the waits themselves passes a `retryDelay` in its `defaultOptions`, a number or a function, and moves fake timers on.
- An app whose `staleTime` is below the Netjes default of one minute now refetches seeded queries in its tests as they mount, as it does in the browser: the test answers that request, with a mock server for instance, or sets `defaultOptions: { queries: { staleTime: 60_000 } }`.
- `netjes()` sets `test.provide['netjes:query']` in every app's Vitest config, unless `test` or `query` is `false`. An app's own `provide` keys are kept.
- A `queryClient` a test passes to `createTestApp` is used as it is, with none of these defaults.
