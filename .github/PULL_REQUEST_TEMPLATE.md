## What changed

<!-- A short summary of the change. -->

## Why

<!-- The problem it solves. Link the issue (Fixes #123). -->

## How it was tested

<!-- The failing test you wrote first, and the suites you ran (see AGENTS.md), with results. -->

- [ ] `dotnet test Netjes.slnx -c Release`
- [ ] `npm test` and `npm run typecheck`
- [ ] End-to-end suites (Blog, TaskBoard), if an example or the router, renderer or CLI changed
- [ ] Security-relevant code has tests for the bypasses, not only the happy path (see AGENTS.md)
- [ ] `npx netjes generate --check` passes in the examples, if typegen or the C# types changed
- [ ] `scripts/check-packages.sh`, if the CLI, template or packaging changed

## Docs

- [ ] The guides (`docs/guides/`), the reference pages (`docs/reference/`), `docs/architecture.md` and the package READMEs updated where they cover the change, and `npm run docs:llms` run
- [ ] `CHANGELOG.md` has an entry under `## [Unreleased]` for anything a running app can notice
- [ ] ADR added in `docs/decisions/` if the decision is one a newcomer would not guess

## Behaviour changes

<!-- Anything a running app can notice: a new default, a limit, a header, a removed or renamed API. Write "None" if there is nothing. -->
