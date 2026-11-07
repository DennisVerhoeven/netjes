# 0048 – A new app has a C# test project and a Playwright suite, both outside the app's build

Status: accepted

## Context

A new app had Vitest tests and left C# and end-to-end tests to the testing guide, without saying where they go. Four of its settings then worked against the guide. The Web SDK compiles everything below the project, so a `tests/App.Tests` folder compiled into the app and failed its build, and that project's `obj/` JSON files became content. The `tsconfig.json` named three folders and `vite.config.ts`, so TypeScript anywhere else went unchecked, and it had no way to give a `playwright.config.ts` Node's types without giving them to components too. The build targets kept only `tsconfig.json` out of the output, so a `tsconfig.app.json` went along. And `netjes agents` copies one `AGENTS.md` into any app, so naming a test command there would send an agent to a command that fails in an app without that suite.

## Decision

- **C# tests.** `netjes new` writes `tests/<App>.Tests`: a project that references the app's, `AppFactory.cs`, a `WebApplicationFactory<Program>` in the `Testing` environment with Netjes in development mode, a stand-in for the renderer and `TypesFile` set to `null`, and `HomePageTests.cs`. It runs under plain `dotnet test`, with no Node and no client build. It references `xunit.v3.mtp-off`, the VSTest variant of xUnit v3, and the guide says what to change in a repository whose `global.json` selects Microsoft.Testing.Platform.
- **Playwright.** It writes `playwright.config.ts`, `e2e/home.spec.ts` and a `test:e2e` script, with `@playwright/test` as a dev dependency and no browser until `npx playwright install chromium`. The suite runs against a development and a production server, and automates the check with JavaScript turned off that `AGENTS.md` asks for.
- **Outside the app's build.** The template's project file adds `tests/**` and `e2e/**` to `DefaultItemExcludes`, so those root folders are reserved, in any case on macOS and Windows. `.dockerignore` leaves them out of the image's context, and `netjes info` does not count the root `tests/` folder as code the generated types come from.
- **TypeScript.** `tsconfig.json` lists no files and points to `tsconfig.app.json`, every `.ts` and `.tsx` file without Node's types, and `tsconfig.node.json`, the files that run in Node, with them and with `@types/node`. `npm run typecheck` runs `tsc -p` on each. The build targets keep every `tsconfig.*.json` out of the output.
- **Names.** The eleven types the test project uses by their simple names are refused as app names, since a root namespace of that name hides them from the test project.
- **AGENTS.md.** `_AGENTS.md` marks the parts about each suite as sections. `netjes new` keeps them all. `netjes agents` keeps the C# part when `tests/<Name>.Tests/<Name>.Tests.csproj` exists and the Playwright part when `playwright.config.ts` exists, and removes the markers. The skill, which is copied as it is, words those checks for any app.

## Alternatives considered

- **`xunit.v3` on Microsoft.Testing.Platform, with a `global.json` in the app.** That file would shadow the SDK version of a repository the app sits in, and it changes what `dotnet test` takes on its command line. Without one, `xunit.v3` fails under `dotnet test` on the .NET 10 SDK.
- **xUnit v2.** It is in maintenance mode, and the guide's samples use v3's API.
- **`extern alias` for the app's reference**, which would let an app be named `Task`. Every test file would need the alias, and the `<App>.Tests` namespace, which is what clashes, would have to go.
- **A test project next to the app's folder, or a solution file at its root.** Every `netjes` command expects one `.csproj` in the current folder, and `dotnet build` and `dotnet watch` fail with MSB1011 when a solution sits next to the project.
- **`tests/**` in the package's build targets.** It would force that layout on every app; the template's project file holds it instead, and the guide gives other apps the line.
- **`references` in a `tsconfig.json` that also has its own settings.** TypeScript 7 fails `tsc -p` on that with TS6306 and TS6310.
- **A config in `e2e/` next to an app config at the root.** `vite.config.ts` and `scripts/` would open as loose files in an editor.
- **`tsc -b`.** It writes `.tsbuildinfo` files.
- **Playwright only in the guide, or behind `netjes new --e2e`.** The first leaves AGENTS.md's check with JavaScript turned off to be done by hand. The second means two template variants to test, and nobody finds a flag before the app exists.
- **Wording in AGENTS.md that hedges everywhere** ("if the app has ..."). Every reader of a new app would read conditions that are always true.

## Consequences

- A new app has a place for each kind of test, and its `AGENTS.md` can tell an agent to run them.
- `netjes new task` and ten others stop working. An existing app keeps working as it is, and takes on what it wants in the order the changelog gives. Copying the configs can show type errors in files the old config did not check.
- A new app installs more: about 10 MB for `@playwright/test`, and the test project's NuGet packages on its first `dotnet test`. The template's versions of them are updated by hand, since Dependabot leaves the template out.
- The package check runs the C# tests and the Playwright suite of a scaffolded app, which adds minutes to that job.
- Whether an editor follows the references of a solution-style root under TypeScript 7 was not checked: `tsc -p` on each config was. An editor that does not follow them would open a config-less view of `vite.config.ts` and `e2e/`.
