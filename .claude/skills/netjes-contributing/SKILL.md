---
name: netjes-contributing
description: Use when making any change to Netjes itself (the C# library, the npm package, the CLI, the template or the examples), from creating the branch to asking for a merge. Covers branch and worktree setup, test-first work, docs, ADRs and commit rules.
---

# Making a change to Netjes

## 1. Branch

- One branch per change, off `main`: `feat/…`, `fix/…`, `refactor/…`, `docs/…`, `test/…`, `chore/…`.
- For parallel work, use a worktree per branch: `git worktree add ../netjes-worktrees/<name> -b <branch> main`, then `npm ci && npm run build` in it. Contributors need Node.js `^22.22.2 || ^24.15.0 || >=26.0.0` (`netjes info` checks it) and the .NET SDK named in `global.json` (10.0.403 or newer). Worktrees share one machine, so only one of them runs an end-to-end suite at a time (see `netjes-verification`).
- New scope found along the way gets its own branch. Note it and finish the current one first.

## 2. Find the shape before the code

- Read the part of [docs/architecture.md](../../../docs/architecture.md) that covers the area, and the ADRs it links.
- Look at both sides. Most features have a C# half (an endpoint convention, the payload, `PageRenderer`) and a JS half (`router.tsx`, `route-tree.tsx`, the Vite plugin, the sidecar). The wire format between them is the payload (`src/Netjes/Rendering/PagePayload.cs` and `packages/netjes/src/payload.ts`); change both together.
- When the code resists the change, make a structural `refactor:` commit first, with the suites green before and after.

## 3. Test first

1. Write one test for the next behaviour:
   - .NET: xUnit in `tests/Netjes.Tests`, usually an endpoint test through the test app helpers, with names like `Thing_Scenario_Outcome`.
   - JS: Vitest in `packages/netjes/test`, using Testing Library and MSW.
   - Example behaviour: the example's own tests.
2. Run it and read the failure. It must fail on the behaviour, not on a compile error or a missing import.
3. Make it pass with the smallest change, then run the area's tests.
4. For a fix, the test reproduces the bug. For security-relevant code, add tests for the bypasses too: encodings, casing, alternative code paths, and both the C# and JS sides.
5. Repeat. Commit after each green step.

Tests that pin internal details are hard to keep. Assert what an app or a browser can observe: status, headers, payload, rendered DOM.

## 4. Docs and decisions

- In the same branch, update the guide that covers the area (`docs/guides/`), the reference pages (`docs/reference/`: the React API, the C# API, options, the CLI and the Vite plugin), `docs/architecture.md` where the mechanism changed, and the package READMEs (`packages/netjes/README.md`, `src/Netjes/README.md`, `src/Netjes.Images.SkiaSharp/README.md`). [docs/README.md](../../../docs/README.md) lists every page; a new page goes in it. A change apps build on also goes in the agent skill (`packages/netjes/skills/netjes/`) and the template's `_AGENTS.md` (the app's `AGENTS.md`).
- After changing any of those, run `npm run docs:llms` to regenerate `llms.txt` and `llms-full.txt`, and commit them with the change. It reads only the docs git tracks (and names the ones it left out), so `git add` a new document first.
- A decision that a newcomer would not guess, or that was chosen over a real alternative, gets an ADR in `docs/decisions/NNNN-title.md`: Context, Decision, Alternatives considered, Consequences, half a page. Never edit an accepted ADR; write a new one that supersedes it.
- Call out every change a running app can notice (a new default, a limit, a header, a removed or renamed API) in the docs. From 0.0.1 on, add it to `CHANGELOG.md` under `## [Unreleased]`, with what an app has to do about it. For a breaking change, also call it out in the merge commit.
- Write for outside readers: no personal names, no links to private material, no tool or session references.

## 5. Commits

- Conventional subjects, imperative, at most 72 characters, no trailing period. Add a body when the reason isn't obvious from the diff.
- Stage explicit paths and check `git diff --cached --stat`; never `git add .`. Never `--no-verify`.
- Generated files (`generated/netjes.ts`, the golden files, `llms.txt` and `llms-full.txt`) are regenerated, never edited by hand, and committed with the change that caused them. `UPDATE_GOLDEN=1 dotnet test --project tests/Netjes.Tests` rewrites the typegen golden files, `packages/netjes/test/fixtures/client-render/shell.html`, the client-render shell that the .NET and Vitest suites both check, and the public API listings in `tests/Netjes.Tests/PublicApi/`; review the diff, and make a new public type or member a decision, not a default.

## 6. Ready for review

The branch is ready when `netjes-verification`'s gate passes and the diff contains nothing unrelated. A reviewer checks four things:
- the change does what it claims (ideally by reproducing it);
- the new tests fail with the change reverted;
- nothing else regressed;
- the docs are accurate.

Merge with a merge commit (`--no-ff`) for a branch of several commits, then delete the branch and its worktree.
