# 0058 – A package makes its own mount in its tests, and renders its pages through it

Status: accepted; extends [ADR 0049](0049-page-packages-are-mounted-into-the-route-table.md) and [ADR 0051](0051-a-page-package-has-a-generated-file-of-its-own.md)

## Context

A page package's C# services build URLs from where the app mounted the package (`PagePackageMount.Url`) and find the mount in `PagePackageMounts`. Both types had no public constructor, so a package could test such a service only by starting a whole app that mounts it. Each package that wanted a cheap test wrote a fake of its own paths, a second place that had to parse and fill the same prefixes (`/[tenant]/(desk)/help`) and could drift from the real one.

A package also renders its own pages from code that is no page loader: a rate limiter's rejection handler or an authentication event shows the sign-in form again. `Page.Render(pattern, props)` finds the page by the pattern the app mounted it at, which only the app chose. A package has no form below its mount for this, as its `MapPageLoader` has, so it built the mounted pattern itself from the mount's prefix, with the prefix's values as placeholders, in a second copy of the mount's rules.

## Decision

- `PagePackageMount.ForTests(package, prefix)` makes a mount without an app. It parses the prefix as `MapPagePackage` does and throws for one that call would refuse, so a test cannot use a mount an app could not have. `PagePackageMounts.ForTests(params mounts)` makes the singleton over such mounts, in order, with the same lookup, and refuses one package twice as `MapPagePackage` does.
- `PagePackageMount.Render(path, props)` renders the package's page for `path`, a pattern below the mount in file syntax (`/sign-in`, `/articles/[slug]`; `/` is the mount's own page). It is `Page.Render` for the mounted pattern: the mount puts its prefix, without route groups and with its `[value]` segments as in the route table, in front of `path`. The values of the prefix come from the request as they do for any page of the app. It refuses a `path` that is no pattern (no leading `/`, `//`, a query or a fragment), because a mount has no use for anything else and a URL there is a mistake.

## Alternatives considered

- **A public constructor on `PagePackageMount`.** The type then looks like something a package creates in production, where the only mounts that mean anything are the app's. A named factory says what it is for.
- **Only the mount, not the list.** A service that takes `PagePackageMounts` could then not be tested, and the lookup by name is the part a package's service uses.
- **Resolving a pattern below the mount inside `Page.Render`.** `Page.Render` has no notion of a package and an endpoint of the app may render any page; a method on the mount leaves `Page.Render` as it is and names the package in the code that reads it.
- **Checking in `Render` that the page exists.** The route table is read when the request renders, as for `Page.Render`, which answers an unknown pattern as it always did; a second check at the call would run before the app has mapped its pages.

## Consequences

- A package's tests build a mount at a literal prefix and at one with a value, and run the package's URL-building and rendering code against it, with no host.
- A package's code renders its own pages by the pattern below its mount and no longer writes the app's prefix or copies the mount's rules.
- Two public members on `PagePackageMount`, one on `PagePackageMounts`, no change for an app, and the public API listing grows by them.
