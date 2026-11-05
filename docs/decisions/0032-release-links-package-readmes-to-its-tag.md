# 0032 – The release links the package READMEs to its tag

Status: accepted

## Context

The npm package and both NuGet packages carry a README, which npmjs.com and nuget.org show on each version's page. Neither site resolves a relative link, so the three READMEs (`packages/netjes/README.md`, `src/Netjes/README.md` and `src/Netjes.Images.SkiaSharp/README.md`) link the docs with absolute URLs to this repository's files on GitHub, on `main`. The README of a published version therefore sent its readers to whatever `main` said later, which can describe APIs that version does not have. `netjes --help` did the same: it ended with a link to the CLI reference on `main`.

A release is a tag on a commit of `main` whose CI run passed, and that run checked every link to this repository's files on `main` against the files of that commit (`npm run docs:check`).

## Decision

- The READMEs in the repository keep linking `main`. GitHub shows them there with links that resolve, the link check covers those links, and `llms-full.txt`, which includes the three READMEs, links `main` throughout.
- The release workflow moves those links to the tag just before it packs: `node scripts/release.mjs links <version>` changes the ref of every `github.com/DennisVerhoeven/netjes/blob/main/…`, `tree/main/…` and `raw.githubusercontent.com/DennisVerhoeven/netjes/main/…` link in the three READMEs from `main` to `v<version>`, and leaves the rest of each file as it is. Every check and test has run on the tag's own sources by then, and the change stays in the job's checkout. Each moved link points at the file the link check found in the tag's commit.
- `links` writes nothing when a README links this repository on another ref, and reads the files back afterwards. After the pack, the workflow compares the README in each package with the moved one (`cmp`), so a package whose README still links `main` stops the job before anything is published.
- `node scripts/release.mjs check`, which the release runs first and a test runs on the repository in every pull request, refuses a link to this repository in those READMEs on any ref but `main`, and says to link `main`. Another test runs `links` on copies of the READMEs in every pull request.
- The READMEs link the security policy at GitHub's policy page (`/security/policy`), which has no ref, so a published README shows the current policy rather than the one at its tag.
- `netjes --help` links the CLI reference at the tag of the installed version.
- The files that `netjes new` and `netjes agents` copy into apps, the `netjes` skill and `AGENTS.md`, keep linking `main`. They stay in an app after it upgrades Netjes, and pinned links would then point at the version that wrote them. Linking the installed version needs a way to refresh them, which the roadmap lists.

## Alternatives considered

- **Tag links in the READMEs in the repository**, changed by hand next to the version pins at each release. `main`'s READMEs would link the last release, so a link to a page added since then would pass the link check, which reads the current files, and lead nowhere on GitHub. Every change would have to write tag links, and `llms-full.txt` would mix two refs.
- **Keep linking `main`.** A published README goes on describing APIs its version may not have.
- **A documentation site with a version per release.** More to build and run than Netjes needs for this; the tag's files on GitHub are a versioned copy of the docs already.
- **Move the links in the package check (`scripts/check-packages.sh`) too.** It would change tracked files in every contributor's checkout. The test that runs `links` on copies of the READMEs checks the same in every pull request.
- **Read the README out of each package in Node.** `unzip -p`, `tar -xO` and `cmp` do it in three lines.
- **Move links on every ref, not only `main`.** A link to an older tag or a commit, written on purpose, would move without a word. Refusing it makes the author link `main`.

## Consequences

- The READMEs on npm and NuGet link the docs at their version's tag (`blob/v<version>/…`), and the security policy at `/security/policy`. Those of 0.0.2 and earlier keep their links to `main`.
- Only the packed copies link the tag. The READMEs in the repository link `main`, also in the tag's own files: a reader of the tag's README on GitHub follows `main`.
- A link to this repository in a package README on any ref but `main` fails `npm test` and the release check.
- `scripts/check-packages.sh` packs the READMEs as they are, with links on `main`; it publishes nothing.
- A release workflow run for a tag cut before this change fails at the move, since that tag's `scripts/release.mjs` has no `links` command.
