# 0052 – A page package whose versions do not fit stops the app

Status: accepted; extends [ADR 0018](0018-startup-failures-end-in-one-line.md)

## Context

A page package has two halves that an app installs apart, one with `npm install` and one with `dotnet add package`. Its C# loaders send props that the zod schemas of its npm half parse, so halves of two versions fail where the two disagree: an invalid-props error on some pages and not on others, far from its cause. Netjes asks the same of its own pair, and `netjes info` checks that the app's `netjes` and `Netjes` are one version.

A package is also written for some versions of Netjes. npm checks `peerDependencies` when the package is installed, but `--legacy-peer-deps` or `--force` passes over the check, and nothing on the npm side checks the Netjes that the .NET app runs. And the range check in `netjes info` read ranges more loosely than npm: `^0.0.4` held every 0.0.x from 0.0.4 on, where npm holds 0.0.4 alone, and a prerelease counted as its release.

## Decision

- `MapPagePackage` runs three checks, in this order, before it scans the package's files:
  1. The npm half is there: in `node_modules` in development and in the commands, and in the client build's record in production. The two messages name the fix, `npm install <name>@<version>` with the C# half's version, or adding the package to `netjes({ packages })` and building again.
  2. The two halves are one version: the npm half's `version` equals `PagePackage.Version`, the C# assembly's informational version without its `+` suffix.
  3. The running Netjes is in the npm half's `peerDependencies.netjes`, compared without its `+` suffix as well.
- A failed check is a `NetjesStartupException` with one line that names the package, the versions and what to install. As for the other startup failures ([ADR 0018](0018-startup-failures-end-in-one-line.md)), an app that is its own program logs it as one critical entry and exits with code 1, and a test host's start throws it.
- The supported range is written once, in `peerDependencies.netjes`, which npm also reads. The .NET app reads it from `node_modules`, or in production from the build record, into which the client build copies it.
- The Vite plugin checks the same range against the installed `netjes`: `vite build` fails on it, and in development the sidecar reports it as the Vite dev server failing to start.
- Ranges are read as npm reads them, on both sides: `^0.0.4` is `>=0.0.4 <0.0.5`; a prerelease is in a range only when one of its comparators names a prerelease of the same version; comparators joined by spaces, `||`, hyphen ranges, `x` and partial versions are read; and a range that is none, such as `latest`, holds no version. `netjes info` reads ranges this way too. One set of cases, `packages/netjes/test/fixtures/version-ranges.json`, is checked by the xUnit and the Vitest suites.
- `netjes info` never runs the app ([ADR 0007](0007-a-node-cli-that-asks-the-app.md)), so it cannot see what is mounted. It reports the page packages the app depends on instead, with their versions, and fails on a peer range that leaves out the installed `netjes` and on a `PackageReference` of a package's NuGet half at another exact version.

## Alternatives considered

- **A range on `PagePackage` in C#.** Two places to keep in step for one fact, one of which npm never reads.
- **A range derived from the Netjes the C# half was compiled against.** That says which version the package was built with, not which versions it works with: too strict after a compatible release, too loose after a breaking one.
- **Halves of different versions with compatible ranges of their own.** The schemas and the records they describe change together, so a version of each half would only add a matrix to reason about.
- **A warning instead of stopping.** The app would start and fail on some pages, with a parse error that does not name the package.
- **A semver library.** A runtime dependency for about sixty lines of our own code on each side ([ADR 0005](0005-a-small-default-stack.md)).

## Consequences

- A package releases both halves together, at one version. The example page package in this repository takes the repository's version, and the release checks its `package.json`, its lockfile entry and its peer range.
- An app that updates one half of a package, or Netjes past the package's range, stops at startup with the line that says what to install, in development, in production and in the commands.
- Before 1.0, a caret range on a 0.0.x version holds one Netjes release, so each Netjes release needs a release of the package, unless the package states a wider range such as `>=0.0.4 <0.1.0`.
- A peer range of `netjes` that `netjes info` passed before can now fail it.
