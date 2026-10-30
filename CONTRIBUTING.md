# Contributing to Netjes

Thanks for helping. Netjes is small and its design is deliberate, so a short conversation before a big change saves everyone time.

By taking part you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md). Security problems go through [SECURITY.md](SECURITY.md), not the issue tracker.

## Talk first

- **Bugs:** open an issue with the bug report form, with a minimal repro. A pull request with a failing test is even better.
- **Features and API changes:** open an issue (or a discussion) before writing code, and describe the problem you have, not only the change you want. Netjes keeps its public API small, so some good ideas will be turned down or reshaped; better to find out before you build them.
- **Questions:** use GitHub Discussions.
- Small fixes (typos, docs, an obvious bug) can go straight to a pull request.

## Build and test

[AGENTS.md](AGENTS.md) has the requirements, the commands for every suite, and the rules for end-to-end tests and generated files. You need the .NET SDK 10.0.403 or newer and Node.js `^22.22.2 || ^24.15.0 || >=26.0.0`. In short: `npm ci && npm run build`, then run the suites that your change can affect. CI runs all of them on your pull request on Linux, and the .NET and JavaScript tests on macOS and Windows too, apart from the ones that need Docker.

## How a change is made

- **Test first.** Write a failing test for the behaviour, watch it fail for the right reason, then make it pass. A bug fix starts with a test that reproduces the bug. Security-relevant code also needs tests for the bypasses, not only the happy path.
- **One branch per change**, off `main`. Unrelated changes go in separate pull requests.
- **Conventional commits** (`feat:`, `fix:`, `docs:`, `test:`, `refactor:`, `chore:`), small, with structural and behavioural changes in separate commits.
- **Docs in the same change:** the guides and reference pages in `docs/` ([docs/README.md](docs/README.md) lists them), `docs/architecture.md`, the package READMEs. A decision a newcomer would not guess gets an ADR in `docs/decisions/`. Behaviour a running app can notice (a new default, a limit, a header, a removed API) must be called out, in the docs and in [CHANGELOG.md](CHANGELOG.md) under `## [Unreleased]`.
- **Match the surrounding code**: naming, comment density, idioms. Strict TypeScript and warnings-as-errors C# are enforced by the build. New dependencies need a reason a few lines of our own code cannot meet.

The longer procedures are in the repository's skills, written for people and coding agents alike: [netjes-contributing](.claude/skills/netjes-contributing/SKILL.md) (from branch to merge) and [netjes-verification](.claude/skills/netjes-verification/SKILL.md) (proving a change works).

## Reviews

Every pull request is reviewed before it merges. A reviewer checks that the change does what it says, that the new tests fail without it, that nothing else regressed, and that the docs are accurate. Expect questions and requests for changes; reply in the pull request, and push fixes as new commits so the review can follow them. Maintainers may reshape or decline a change that does not fit the project's direction, and will say why.

## Licence of contributions

Netjes is released under the [MIT licence](LICENSE). By submitting a contribution you agree that it is licensed under the same terms (inbound = outbound), and that you have the right to submit it.
