# Working on Netjes

Netjes renders React pages from an ASP.NET Core app. C# owns routing, loaders, server actions, authorization and caching; a supervised Node sidecar renders React; the browser hydrates and navigates. Read [docs/architecture.md](docs/architecture.md) before changing anything structural, and [docs/decisions/](docs/decisions/) for why it is built this way. These instructions are for anyone changing Netjes itself, people and coding agents alike.

## Where things are

| Path | What |
|---|---|
| `src/Netjes/` | The ASP.NET Core library: routing (`Routing/`), rendering and the payload (`Rendering/`), the sidecar pool (`Sidecar/`), typegen (`TypeGen/`), images, export, server actions |
| `src/Netjes.Images.SkiaSharp/` | The optional SkiaSharp image processor |
| `packages/netjes/` | The npm package: the Vite plugin (`vite/`), the client router and components (`src/`, compiled into `dist/`, which the package exports), the sidecar (`sidecar/server.mjs`), the CLI (`cli/`), the app template (`template/`), the skill for agents building apps (`skills/netjes/`) |
| `tests/Netjes.Tests/` | xUnit tests for the library: endpoints, rendering, typegen golden files, sidecar lifecycle |
| `tests/TaskBoard.Tests/`, `tests/Blog.Tests/` | The examples' server tests (TaskBoard against Postgres in Testcontainers) |
| `examples/Blog/`, `examples/TaskBoard/` | Example apps with Vitest and Playwright suites; TaskBoard has Docker Compose for dev and production |
| `docs/` | The tutorial, guides and reference pages ([docs/README.md](docs/README.md) lists them), architecture, decisions (ADRs), roadmap, benchmarks |
| `bench/netjes/` | The benchmark, run on demand (`run.sh`): a load generator with no dependencies and a small app ([docs/benchmarks.md](docs/benchmarks.md)) |
| `assets/` | The Netjes mark, the wordmark and the lockups of the two (for light and for dark grounds), as SVG, and the PNGs `render.mjs` makes from the mark and the social preview (the NuGet packages' icon, the repository's social preview), committed so packing needs no browser; `render.mjs` also copies the mark to the template's `public/favicon.svg` |
| `.github/`, `scripts/` | CI (`workflows/ci.yml`), the release workflow and its checks (`workflows/release.yml`, `scripts/release.mjs`, [RELEASING.md](RELEASING.md)), Dependabot, the package check (`scripts/check-packages.sh`) |

## Build and test

After cloning, and after changing `packages/netjes`: `npm ci && npm run build`. The examples and their tests import the package's build in `packages/netjes/dist/`, so an edit to `packages/netjes/src/` reaches them once it is built again; `npm run build --workspace=packages/netjes -- --watch` rebuilds on every change, and the examples' development containers (`docker compose up`) run it themselves. Requirements: .NET SDK 10.0.403 or newer (`global.json`; apps need only a 10.x SDK), Node.js `^22.22.2 || ^24.15.0 || >=26.0.0` (the `engines` of the package and the template), Docker for the Postgres-backed tests. Those carry `[Trait("Category", "Docker")]`, and `dotnet test Netjes.slnx -c Release --filter-not-trait Category=Docker` leaves them out.

```bash
dotnet test Netjes.slnx -c Release             # every .NET test project; also compiles the examples and the benchmark app
npm test                                       # Vitest: the package and both examples
npm run typecheck                              # strict TypeScript in every workspace, and in scripts/ from its JSDoc types
cd examples/Blog && npm run test:e2e           # Playwright, dev and production servers
cd examples/TaskBoard && npm run test:e2e      # Playwright, with a throwaway Postgres
scripts/check-packages.sh                      # pack, scaffold, test, type-check (also under create-vite's flags and noPropertyAccessFromIndexSignature), publish and serve a new app
npm run docs:check                             # llms.txt and llms-full.txt match the docs, and every link resolves
cd bench/netjes && npm test                    # the benchmark harness (not a workspace, so not in the root npm test)
cd bench/netjes && npm run typecheck           # the harness's JavaScript, strict, from its JSDoc types
node --test scripts/test/*.test.mjs            # the scripts' tests: release checks, llms.txt, the link check, LF line endings
```

CI (`.github/workflows/ci.yml`) runs all of these on every pull request and every push to `main`, on Linux and Node.js 22.22.2, the oldest supported version. On macOS and Windows it runs the build, the type checks, `npm test`, `netjes generate --check`, the docs check and the .NET tests without the Docker ones; the end-to-end suites, the package check and the benchmark harness run on Linux only.

- Every port the Playwright configs use can be moved, so several runs can share a machine, each in its own checkout (a run builds into `dist/`, `obj/` and the Vite caches under `node_modules/`, and `scripts/check-packages.sh` empties and rebuilds `packages/netjes/dist/`, which every suite imports). Defaults: Blog 5090-5093, 5190 and 5192, TaskBoard 8090, 8091, 8190 and its Postgres on 8432. Set `E2E_DEV_PORT`, `E2E_PROD_PORT`, `E2E_VITE_PORT`, `E2E_BASE_PORT`, `E2E_BASE_VITE_PORT`, `E2E_FALLBACK_PORT` and `E2E_EXPORT_PORT` (Blog; the fallback port defaults to one above `E2E_BASE_PORT`, the export port to any free one), and `E2E_DB_PORT` (TaskBoard). TaskBoard's throwaway Postgres is the container `taskboard-e2e-db`, or `taskboard-e2e-db-<port>` off the default port, so runs never share a database. Outside CI a server that is already listening is reused; `CI=1` always starts fresh servers.
- `SidecarCrashTests` in TaskBoard.Tests runs the built app (`npm run build` first) on a free port, which `TASKBOARD_TEST_PORT` pins, and finds its sidecar with `pgrep`: it runs on Linux and macOS and is skipped elsewhere.
- `generated/netjes.ts` in each example is generated from the C# types: run `npx netjes generate` in the example (or start it in Development; `--check` fails when it is stale), never edit it by hand. Golden files are rewritten with `UPDATE_GOLDEN=1 dotnet test --project tests/Netjes.Tests`: the typegen ones, `packages/netjes/test/fixtures/client-render/shell.html`, the client-render shell that the .NET and Vitest suites both check, and `tests/Netjes.Tests/PublicApi/*.txt`, the public API of the Netjes and Netjes.Images.SkiaSharp assemblies (a new public type or member shows up there, so a change to the API is a decision in the diff). Review the diff.
- A test that fails only under load is a bug to fix, not to retry: find the race.
- `llms.txt` and `llms-full.txt` are generated from the docs by `scripts/llms.mjs` (its reading order is the list at the top): run `npm run docs:llms` after changing a document, never edit them by hand. It reads only the docs git tracks, so `git add` a new one first. `npm run docs:check` also runs `scripts/check-links.mjs`, which fails on a relative link, an anchor or a link to this repository on GitHub that points at nothing in the tracked files.

## How changes are made

- Test first. Write one failing test for the behaviour, watch it fail for the right reason, then make it pass. A bug fix starts with the test that reproduces it.
- One branch per change, off `main`, with small conventional commits (`feat:`, `fix:`, `docs:`, `test:`, `refactor:`, `chore:`). Structural and behavioural changes go in separate commits.
- Documentation changes in the same branch as the behaviour: the guides (`docs/guides/`), the reference pages (`docs/reference/`), `docs/architecture.md`, the package READMEs, and for what apps build on, the app skill (`packages/netjes/skills/netjes/`) and the template's `_AGENTS.md`. A decision a newcomer would not guess gets an ADR in `docs/decisions/`; accepted ADRs are superseded by new ones, not edited.
- Behaviour a running app can notice (a new default, a limit, a header, a removed API) is called out in the docs and in the merge. From 0.0.1 on, it also goes in `CHANGELOG.md`, under `## [Unreleased]`, with what an app has to do about it.
- Every change is reviewed and the full suites, including both end-to-end suites, pass before it merges.

## Conventions

- C#: minimal APIs, nullable reference types and warnings as errors, records for DTOs, `ILogger` message templates, async throughout. Keep the public API small; anything apps do not need is `internal`.
- TypeScript: `strict`, no `any`, zod at every boundary (payloads, form input, URL params). Server state goes through TanStack Query.
- Portable code: CI runs on Linux, macOS and Windows. Files are LF in every checkout (`.gitattributes`), and so is what code writes for a file or a comparison: `\n`, not `Environment.NewLine`, `os.EOL` or the default of indented JSON. Paths are built with `Path.Combine` and `path.join`, and route files, manifest keys and paths compared as text use `/`. Import a file by its URL (`pathToFileURL`), and run npm and npx through a shell: on Windows they are `.cmd` files.
- Match the surrounding code: its naming, its comment density, its idioms.
- Security-relevant code (server actions and antiforgery, output-cache keys, the image endpoint, the sidecar token, redirects and export, CSP and security headers) needs tests for the bypasses as well as the happy path: other encodings, casing, alternative code paths, both the C# and the JS side.
- New dependencies need a reason a few lines of our own code cannot meet; runtime dependencies of the packages need an ADR.

The skills in `.claude/skills/` hold the longer procedures: `netjes-contributing` (making a change from branch to merge) and `netjes-verification` (proving a change works).
