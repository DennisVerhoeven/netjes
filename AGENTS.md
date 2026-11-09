# Working on Netjes

Netjes renders React pages from an ASP.NET Core app. C# owns routing, loaders, server actions, authorization and caching; a supervised Node sidecar renders React; the browser hydrates and navigates. Read [docs/architecture.md](docs/architecture.md) before changing anything structural, and [docs/decisions/](docs/decisions/README.md) for why it is built this way. These instructions are for anyone changing Netjes itself, people and coding agents alike.

## Where things are

| Path | What |
|---|---|
| `src/Netjes/` | The ASP.NET Core library: routing (`Routing/`), rendering and the payload (`Rendering/`), the sidecar pool (`Sidecar/`), typegen (`TypeGen/`), images, export, server actions |
| `src/Netjes.Images.SkiaSharp/` | The optional SkiaSharp image processor |
| `packages/netjes/` | The npm package: the Vite plugin (`vite/`), the client router and components (`src/`, compiled into `dist/`, which the package exports), the sidecar (`sidecar/server.mjs`), the CLI (`cli/`), the app template (`template/`), the skill for agents building apps (`skills/netjes/`) |
| `tests/Netjes.Tests/` | xUnit tests for the library: endpoints, rendering, typegen golden files, sidecar lifecycle, and the packaging (`Packaging/`: the build targets, the template's project file, the template's and the examples' Docker images and the template's Compose files) |
| `tests/TaskBoard.Tests/`, `tests/Blog.Tests/` | The examples' server tests (TaskBoard against Postgres in Testcontainers), the mounted help center's included |
| `examples/Blog/`, `examples/TaskBoard/` | Example apps with Vitest and Playwright suites; TaskBoard has Docker Compose for dev and production |
| `examples/HelpCenter/` | A page package that both examples mount: its npm half (`@netjes-examples/help-center`, a workspace) and its NuGet half (`HelpCenter.csproj`). The Blog writes its `generated/netjes.ts`; its version is the repository's, which the release checks |
| `docs/` | The tutorial, guides and reference pages ([docs/README.md](docs/README.md) lists them), architecture, decisions (ADRs), roadmap, benchmarks |
| `bench/netjes/` | The benchmark, run on demand (`run.sh`): a load generator with no dependencies and a small app ([docs/benchmarks.md](docs/benchmarks.md)) |
| `assets/` | The Netjes mark, the wordmark and the lockups of the two (for light and for dark grounds), as SVG, and the PNGs `render.mjs` makes from the mark and the social preview (the NuGet packages' icon, the repository's social preview), committed so packing needs no browser; `render.mjs` also copies the mark to the template's `public/favicon.svg` |
| `.github/`, `scripts/` | CI (`workflows/ci.yml`), the release workflow and its checks (`workflows/release.yml`, `scripts/release.mjs`, [RELEASING.md](RELEASING.md)), Dependabot, the package check (`scripts/check-packages.sh`) |

## Build and test

After cloning, and after changing `packages/netjes`: `npm ci && npm run build`. The examples and their tests import the package's build in `packages/netjes/dist/`, so an edit to `packages/netjes/src/` reaches them once it is built again; `npm run build --workspace=packages/netjes -- --watch` rebuilds on every change, and the examples' development containers (`docker compose up`) run it themselves. Requirements: .NET SDK 10.0.403 or newer (`global.json`; apps need only a 10.x SDK), Node.js `^22.22.2 || ^24.15.0 || >=26.0.0` (the `engines` of the package and the template), Docker for the Postgres-backed tests and the tests of the template's and the examples' production images and of the template's Compose files. Those carry `[Trait("Category", "Docker")]`, and `dotnet test Netjes.slnx -c Release --filter-not-trait Category=Docker` leaves them out.

```bash
dotnet test Netjes.slnx -c Release             # every .NET test project; also compiles the examples and the benchmark app
npm test                                       # Vitest: the package and both examples
npm run typecheck                              # strict TypeScript in every workspace, and in scripts/ from its JSDoc types
cd examples/Blog && npm run test:e2e           # Playwright, dev and production servers
cd examples/TaskBoard && npm run test:e2e      # Playwright, with a throwaway Postgres
scripts/check-packages.sh                      # pack, scaffold, test (Vitest and C#), type-check (also under create-vite's flags and noPropertyAccessFromIndexSignature), mount the packed example page package, publish and serve a new app, and run its Playwright suite
npm run docs:check                             # llms.txt and llms-full.txt match the docs, and every link resolves
cd bench/netjes && npm test                    # the benchmark harness (not a workspace, so not in the root npm test)
cd bench/netjes && npm run typecheck           # the harness's JavaScript, strict, from its JSDoc types
node --test scripts/test/*.test.mjs            # the scripts' tests: release checks, llms.txt, the link check, the docs checks, LF line endings, the package check's browser install
```

CI (`.github/workflows/ci.yml`) runs all of these on every pull request and every push to `main`, on Linux and Node.js 22.22.2, the oldest supported version. On macOS and Windows it runs the build, the type checks, `npm test`, `netjes generate --check`, the docs check and the .NET tests without the Docker ones; the end-to-end suites, the package check and the benchmark harness run on Linux only.

- Every port the Playwright configs use can be moved, so several runs can share a machine, each in its own checkout (a run builds into `dist/`, `obj/` and the Vite caches under `node_modules/`, and `scripts/check-packages.sh` empties and rebuilds `packages/netjes/dist/`, which every suite imports). Defaults: Blog 5090-5093, 5190 and 5192, TaskBoard 8090, 8091, 8190 and its Postgres on 8432, and in `scripts/check-packages.sh` 5399 (`CHECK_PACKAGES_PORT`) and, for the new app's suite, 5396-5398 (`CHECK_PACKAGES_E2E_DEV_PORT`, `CHECK_PACKAGES_E2E_PROD_PORT` and `CHECK_PACKAGES_E2E_VITE_PORT`). Set `E2E_DEV_PORT`, `E2E_PROD_PORT`, `E2E_VITE_PORT`, `E2E_BASE_PORT`, `E2E_BASE_VITE_PORT`, `E2E_FALLBACK_PORT` and `E2E_EXPORT_PORT` (Blog; the fallback port defaults to one above `E2E_BASE_PORT`, the export port to any free one), and `E2E_DB_PORT` (TaskBoard). TaskBoard's throwaway Postgres is the container `taskboard-e2e-db`, or `taskboard-e2e-db-<port>` off the default port, so runs never share a database. Outside CI a server that is already listening is reused; `CI=1` always starts fresh servers.
- `SidecarCrashTests` in TaskBoard.Tests runs the built app (`npm run build` first) on a free port, which `TASKBOARD_TEST_PORT` pins, and finds its sidecar with `pgrep`: it runs on Linux and macOS and is skipped elsewhere.
- `generated/netjes.ts` in each example is generated from the C# types: run `npx netjes generate` in the example (or start it in Development; `--check` fails when it is stale), never edit it by hand. Golden files are rewritten with `UPDATE_GOLDEN=1 dotnet test --project tests/Netjes.Tests`: the typegen ones, `packages/netjes/template/generated/netjes.ts`, the generated file that `netjes new` ships, `packages/netjes/test/fixtures/client-render/shell.html`, the client-render shell that the .NET and Vitest suites both check, and `tests/Netjes.Tests/PublicApi/*.txt`, the public API of the Netjes and Netjes.Images.SkiaSharp assemblies (a new public type or member shows up there, so a change to the API is a decision in the diff). Review the diff. `packages/netjes/test/fixtures/segments/cases.json` is edited by hand: it states the route and segments the server sends for page and boundary files, which `SegmentFixtureTests` checks against the server and `render-document.test.tsx` against `renderDocument`'s copy of the rules, so a new kind of segment file needs a case there.
- A test that fails only under load is a bug to fix, not to retry: find the race.
- `llms.txt` and `llms-full.txt` are generated from the docs by `scripts/llms.mjs` (its reading order is the list at the top): run `npm run docs:llms` after changing a document, never edit them by hand. It reads only the docs git tracks, so `git add` a new one first. A document under `docs/`, or a Markdown file the docs index links, that its list does not name is listed under More (decision records stay out). `npm run docs:check` also runs `scripts/check-links.mjs`, which fails on a relative link, an anchor or a link to this repository on GitHub that points at nothing in the tracked files. It also fails on a relative or `blob/main` link to a folder that holds a `README.md`: link the README (`examples/Blog/README.md`, not `examples/Blog`), since a site or a tool built from the Markdown maps files to pages, not folders, and `llms-full.txt` turns a folder link into a link to GitHub's folder page. A link to a folder without a README passes, and so does a `tree/main` link, which asks for that page on purpose.
- The docs checks (`scripts/test/docs.test.mjs`, which `npm test` runs) fail when the two indexes of the decision records (`docs/decisions/README.md` and `docs/README.md`) do not list every record in order by its own title, when the getting-started guide's TypeScript configs differ from the template's, when a guide is in no `###` group under `## Guides` in `docs/README.md` or in two, when a line in a tracked Markdown file outside test fixtures (`CHANGELOG.md` and the decision records included) that links four or more guides, or the Guides entries of `SECTIONS` in `scripts/llms.mjs`, list them in another order than the index, and on a `;` or a lone `#` in a Mermaid sequence diagram: either ends the message there, so write `#59;` for a `;` and `#35;` for a `#`, or reword.

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
