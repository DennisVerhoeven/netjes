---
name: netjes-verification
description: Use whenever a Netjes change has to be proven to work, before calling a branch done, before a merge, or after merging several branches together. Lists the suites, the end-to-end rules, the image and package checks, and how to treat a flaky test.
---

# Proving a Netjes change works

Evidence beats memory. A branch is done when these pass on its final commit and the output shows it.

## The gate

Run what the change touches; before a merge to `main`, run all of it.

| Check | Command | Notes |
|---|---|---|
| Install and build | `npm ci && npm run build` | Builds the package's `dist/`, which the examples and their tests import, then the examples' production builds. Needed before the other suites after any change in `packages/netjes`. |
| .NET | `dotnet test Netjes.slnx -c Release` | Netjes.Tests, Blog.Tests and TaskBoard.Tests, and it compiles the examples and the benchmark app. TaskBoard needs Docker (Testcontainers); the tests that need it carry `[Trait("Category", "Docker")]`, which `--filter-not-trait Category=Docker` leaves out, as CI does on macOS and Windows. Its `SidecarCrashTests` needs the production build (`npm run build`) and `pgrep`, so it runs on Linux and macOS and is skipped elsewhere; it listens on a free port or `TASKBOARD_TEST_PORT`. Report the total, failed and skipped counts. |
| JS | `npm test` | The package and both examples. |
| Types | `npm run typecheck` | Strict TypeScript in every workspace, and in `scripts/` from its JSDoc types. |
| Generated types | `npx netjes generate --check` in each example | Fails when `generated/netjes.ts` is stale. The golden files (typegen, the client-render shell in `packages/netjes/test/fixtures/client-render/shell.html`, and the public API listings in `tests/Netjes.Tests/PublicApi/`) fail their tests when stale; `UPDATE_GOLDEN=1` rewrites them. |
| Docs | `npm run docs:check` | Fails when `llms.txt` or `llms-full.txt` no longer match the docs, or when a link in a tracked Markdown file points at a file, folder or heading that is not there. |
| Blog end to end | `cd examples/Blog && CI=1 npm run test:e2e` | Dev, production and base-path servers, a production server with no sidecar ready (the client-render fallback), and a `netjes export` of the production build served from its files. |
| TaskBoard end to end | `cd examples/TaskBoard && CI=1 npm run test:e2e` | Starts and removes a throwaway Postgres (8432, or `E2E_DB_PORT`). |
| Benchmark harness | `cd bench/netjes && npm test && npm run typecheck` | The load generator's tests and its JSDoc type check; `bench/netjes` is not a workspace, so the root commands leave it out. `BENCH_TEST_PORT_BASE` puts its test servers on fixed ports. |
| Packages | `scripts/check-packages.sh` | Packs both packages, scaffolds an app from them, then tests, type-checks (also under the flags of create-vite's React-TS template, and with `noPropertyAccessFromIndexSignature` added), publishes and serves it (port 5399, or `CHECK_PACKAGES_PORT`). |
| Script tests | `node --test scripts/test/*.test.mjs` | The checks the release workflow runs before it publishes (`scripts/release.mjs`), and the tests of the `llms.txt` generator and the link checker. `npm test` runs them too. |

CI (`.github/workflows/ci.yml`) runs the whole gate on every pull request and push to `main`, in parallel jobs: `build-and-test`, `e2e (Blog)`, `e2e (TaskBoard)` and `packages` on Linux, and `build-and-test (macos-latest)` and `build-and-test (windows-latest)`, which run the build, the type checks, `npm test`, the generated-types and docs checks and the .NET tests without the Docker ones. A failed end-to-end job uploads its Playwright report and traces as an artifact. Run what the change touches locally first; CI is the record that the whole gate passed.

## End-to-end rules

- Outside CI the configs reuse any server that is already listening, so two runs on the same ports would test each other's code. `CI=1` always starts fresh servers. To run several suites at once, give each run its own checkout (a run builds into `dist/`, `obj/` and the Vite caches under `node_modules/`, and `scripts/check-packages.sh` empties and rebuilds `packages/netjes/dist/`, which every suite imports) and its own ports: `E2E_DEV_PORT`, `E2E_PROD_PORT`, `E2E_VITE_PORT`, `E2E_BASE_PORT`, `E2E_BASE_VITE_PORT`, `E2E_FALLBACK_PORT` (default: one above `E2E_BASE_PORT`) and `E2E_EXPORT_PORT` (default: any free port) for Blog, `E2E_DEV_PORT`, `E2E_PROD_PORT`, `E2E_VITE_PORT` and `E2E_DB_PORT` for TaskBoard. The throwaway Postgres is the container `taskboard-e2e-db-<E2E_DB_PORT>` (`taskboard-e2e-db` on the default 8432), removed when the run ends.
- Before a run, check that its ports are free: `ss -ltn | grep -E ':(5090|5091|5092|5093|5190|5192|8090|8091|8190|8432)\b'` for the defaults.
- The suites need Chromium once: `npx playwright install chromium`.

## Infrastructure changes

A change to a `Dockerfile`, a compose file or `.dockerignore` needs a real image build. Build with a throwaway tag, run it on a free port, request a page, and remove the image afterwards:

```bash
docker build -f examples/Blog/Dockerfile -t netjes-check-blog .
docker run -d --rm -p 127.0.0.1:20581:8080 netjes-check-blog
curl -sI http://127.0.0.1:20581/
docker rmi -f netjes-check-blog
```

Validate a compose file with `docker compose -f … config`. A running container isn't proof that it is healthy: check the health endpoint (`/healthz`).

## Proving a fix, not just a pass

- Revert the fix, keep the test, and watch the test fail. Restore the fix and watch it pass. A test that passes either way proves nothing.
- For a security fix, also try the original attack and its variants against the fixed code.

## A test that fails sometimes

Don't rerun it until it passes. Run it alone, then under load (the full suite, or several runs in parallel), and find the race. Common causes are a shared static cache, a process still exiting, a port or file left behind, or a time budget in a test. Fix the cause, and make the test deterministic: count work instead of measuring time, and wait on a signal instead of sleeping.

Rule out the host first. A Playwright trace in which Chromium aborts requests with `net::ERR_NETWORK_CHANGED` means the host's network changed during the run (a container starting or stopping, a VPN or Wi-Fi change); it is not a race in Netjes. In development the aborted requests are often the page's Vite modules, so the page never hydrates and the test times out waiting for something the client would have done.
