# 0051 – A page package has a generated file of its own, and its links carry its mount

Status: accepted; amends [ADR 0006](0006-types-and-zod-from-csharp-contracts.md) and [ADR 0040](0040-links-from-csharp-are-a-href-type.md)

## Context

The generated file is written by the running app, from its endpoints and its route table ([ADR 0006](0006-types-and-zod-from-csharp-contracts.md)). A page package's pages need the same: the schemas of the props its loaders send, typed references to its server actions, and typed links between its own pages. But the package is built and published before any app picks its prefix, and the app's file cannot ship with the package.

Three things in the app's file stand in the way of a second file:

- Every generated file augments `Register` in `declare module 'netjes'`, which types the `href` of the core `<Link>`. Two different augmentations in one program fail with TS2717, and the app type-checks its own file next to whatever it imports from the package.
- Its `Href` schema checks a root-relative URL against the app's `routePatterns` ([ADR 0040](0040-links-from-csharp-are-a-href-type.md)). A package's file knows only the package's pages, so a link that C# sends, which is the app's URL with the mount in it, would fail the props parse.
- `route()` is a pure function of its pattern and values. The mount's URL is not known when the package is built, and under `/[tenant]/help` it differs per request. Server renders run concurrently in one Node process and share module state, so a prefix kept in a module would leak from one render into another.

## Decision

- The app's file routes every page, the packages' included: `routePatterns`, `Route`, `RouteParams`, `route()` and `Href` hold each package page at its URL where it is mounted, with the prefix's parameters first (`route('/[tenant]/help/articles/[slug]', { tenant, slug })`). Its maps (`pages`, `forms`, `layouts`, `api`, `EndpointParams`, `serverActions`) hold only what the app maps itself. An app that mounts nothing gets the same file, byte for byte.
- A package's file is written in package mode by the app the package is developed in, through `.WriteTypes(path)` on the mount, when that app starts in Development and by `netjes generate`, and `netjes generate --check` fails when it is out of date. The package ships it. It holds the schemas of what the package maps through its mount, and its routes, keyed below the mount (`/articles/[slug]`, `app/(center)`) and without the prefix's parameters, so it is the same file wherever an app mounts the package.
- The package's file declares no `module 'netjes'`. Its `Href` is the core type, which the app's file registers, and its `isHref` checks the shape of a URL alone, by the rules of a C# `Href` (`isHrefShape` in `netjes`). Its `sharedProps` is `z.unknown()`. Its `route()` returns a `MountHref`, and it exports `Link`, `Form`, `SubmitButton`, `useRouter`, `usePathname` and `useHref` from `mounted<MountHref>(name)`, so a package's page links to its own pages as if the package were mounted at the root. `endpointUrl()` gives a URL below the mount, which the file's `useHref` resolves. These names are taken in package mode only, so no app file renames anything.
- Each page payload carries `mounts`: every mounted package's URL for the request, by npm name and without the base path (`/help`, `/acme/help`), each prefix value encoded as one segment. It is `null` for a package whose prefix has a parameter the request has no value for, or one that is empty, `.` or `..`. The field is left out when nothing is mounted.
- `mounted(name)` reads the mount from the payload of the page being rendered, through React context, and puts it in front of each href, then the base path, as the core components do. `usePathname` strips it. Only a path below the mount and a link within the page (`?…`, `#…`) are accepted: another host (`//`, `/\`), a scheme and a dot segment throw, as they would climb out of the mount. Under a `null` mount they throw, naming the package, except `usePathname`, which gives the whole path.
- On the server, `PagePackageMount.Url(path, values)` gives the same URL for redirects, emails and cookie paths, by the same rules, and `PagePackageMounts` lists the mounts for a package's services.

## Alternatives considered

- **One file for the app and its packages.** It cannot ship with the package, and it would differ in every app that mounts it.
- **A package file that registers its routes too.** TS2717 as soon as the app's code and the package's are in one program, and the core `<Link>` can be typed by one set of routes only.
- **The prefix written into the package's file at build time.** The package is built before an app picks the prefix, and a prefix with a value differs per request.
- **A mount held in module state, set by the router.** Concurrent server renders share the module, and a page of one tenant would link below another's.
- **Hrefs built by hand in the package (`useMount() + '/articles'`).** Every link is a chance to forget the prefix, and nothing checks the path below the mount.
- **The package's `Href` checked against its own routes.** A link from C#, which is app-absolute, fails, and so does a link to the app's own pages.

## Consequences

- The package's pages link to each other with typed, mount-relative hrefs, and to the app's pages, and to anything a C# loader sends, with the core `Link`.
- A page outside a mount whose prefix has a value, such as the app's home page next to `/[tenant]/help`, has a `null` mount, and the package's components throw there. Components of a package that render on such pages take their links from C#.
- An update of a package that adds pages changes the app's file, since it routes them: `netjes generate --check` fails until `netjes generate` runs.
- A page the app replaces with `.Override` renders with the package's loader, so its props schema is in the package's file, which the app's page imports.
- Exports that the app's file must hold for a package, such as schemas both read by key, need a registration of their own; they are not part of this decision.
