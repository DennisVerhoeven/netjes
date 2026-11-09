# 0049 – Page packages are mounted into the route table

Status: accepted; amends [ADR 0002](0002-server-owns-routing.md) and [ADR 0010](0010-app-router-with-csharp-layout-loaders.md)

## Context

An app that wanted pages from a library, such as a help center or a back office, copied the library's page files into its own `app/`. Each update of the library meant copying them again, and the copies drifted from the library's C# side, whose loaders and actions they read. Razor class libraries and Rails engines solve this for their frameworks: the library brings its screens, and the app mounts them.

The route table had one source. In development `FileSystemRouteTableProvider` scanned `app/`; in production `ManifestRouteTableProvider` read the convention files from the keys of the Vite client manifest; the page module map was one `import.meta.glob` of `app/`. The table built segments by folder, route groups included, and a layout loader names a folder when two groups put layouts at one URL. The value of a route group's prefix (`app.MapGroup("/{tenant}")`) never reached the table, so `route()` and `Href` did not know it. An unknown URL hit the fallback endpoint, which has no route, and rendered the root `not-found`.

## Decision

- A page package is a pair of one version: an npm package with the pages, layouts, boundaries and components, and a NuGet package with a `PagePackage` descriptor and an extension that mounts it and maps its loaders and actions. An app mounts it with one C# call, `app.MapHelpCenter("/help")`, which calls `MapPagePackage(package, prefix)`, and one entry in `netjes({ packages })`. Installing a dependency adds no page: both halves are named by the app.
- The route table takes several sources: the app's `app/` at the root and the pages folder of each mounted package under its prefix. They merge by folder, as the table already keys segments, and not by URL segment. The prefix is written in folder syntax, of literal URL segments, `[name]` values and `(group)` route groups: `/help`, `/(marketing)/help`, `/support/(desk)/help`, `/[tenant]/help`, or `/`. A catch-all, an `@slot`, a `_private` folder, an intercepting folder, `.`, `..`, `%`, `\`, an empty segment, a parameter name that one of the package's pages uses again, and a folder that differs from an app folder only in case are refused. The prefix's values are part of the merged table, so `route()`, `Href` and the loaders know them.
- Merged by folder, a package's layout at its mount wraps the app's own pages in that folder, and the package's route group `(center)` wraps an app page in `app/help/(center)/`. A package's group names and convention files are therefore part of its public contract under semver.
- Each file has an id: the npm name, the package's pages folder and the file's path there, `@acme/help-center/app/(center)/articles/[slug]/page.tsx`. It is the same in development and production, installed, hoisted or linked, and it is what the payload, errors, `netjes routes` and telemetry name. App files keep `app/...`, and a package named like the app directory or its first folder is refused. The module map key is `/` and the id, which the client and server entries already look up.
- Conflicts within one source, between two packages' pages of one shape, and between two sources' segment files of one convention in one folder throw while the table is built, naming both files. An app page of the same shape as a package's page is held instead, and `MapPages()` throws it, so that `.Override(pattern)` on the mount can resolve it first: the app's file renders in place of the package's, with the package's loader, page action and conventions. Only pages can be replaced.
- Conventions on the mount (`RequireAuthorization()`, `ExcludeFromExport()`, `ExcludeFromSitemap()`) apply to the package's own endpoints: its file pages, its loaders, page actions and server actions, and what is mapped through it. They do not reach the app's pages under the prefix, which the layout loaders above them cover, as before; an app layout loader above the mount covers the package's pages too. For a visitor the mount refuses, a 401 or 403 stops at the package's first folder, so no layout loader of the package runs for them.
- An unknown URL below a mount, and `Page.NotFound()` from an app page whose folders do not reach the mount (a root catch-all), render the deepest mount's nearest `not-found`, inside its layouts. Each layout loader on the way, and each folder with the package's files under its mount's conventions, is authorized first; from the first that refuses the visitor, nothing loads, and the nearest `not-found` above renders. This amends ADR 0010, under which the root `not-found` rendered for every unknown URL.
- In production the routes come from the build. The client build writes a record, `dist/client/.vite/netjes.json`, with each package's name, version, peer range on `netjes`, pages folder and NuGet name, and the manifest key of each of its files by id. Like the manifest it is published, never served and never exported. A file in the record without a manifest key stops startup.
- Server actions keep one namespace, `/_netjes/actions/{name}`, which the client posts to. A package starts its actions' names with its own (`help.vote`), names are compared ignoring case, and `MapAction` in a route group with a prefix throws, since it mapped an action at a URL the client never calls.
- `MapPagePackage` is called on the app, before `MapPages()` and once per package. Inside a `MapGroup` it throws.

## Alternatives considered

- **Copying the page files into the app**, as apps did. Every update is a merge by hand, and the copies drift from the C# side they read.
- **Merging by URL segment.** Route groups are folders: a package's group layout would have no place in the merged tree, and two groups' layouts at one URL, which the conventions allow, could not be told apart.
- **Mounting inside a `MapGroup`.** The group's prefix and its values stay out of the route table, so `route()`, `Href` and the payload would not know where the pages are, as the routing guide already says of groups; and the file pages are mapped by the page data source, not by the group.
- **Finding packages in `node_modules` by their `netjes` field.** Installing a dependency, or a dependency of one, would add URLs to the app.
- **Letting the app's file win without a word.** A package update that adds a page would take a URL from the app, or the app would take one from the package, with no sign of it. The conflict stops the app instead, and `.Override` says which file is meant.
- **Overrides of layouts and boundaries.** A package's layouts load and protect what its pages read; replacing them is a fork of the package. Pages only, named one by one.
- **Mount conventions that reach the app's pages under the prefix.** An app page's protection would depend on where a package is mounted, and conventions would apply by URL rather than by endpoint. A layout loader already covers every page below it, from either source.
- **The manifest alone in production.** A linked package's manifest key is a relative path (`../help-center/app/page.tsx`) that names no package, and production has no `node_modules` to read a version from.

## Consequences

- An app that mounts nothing gets the same route table, payloads and generated file, byte for byte, and one fallback endpoint as before.
- A package update that adds a page of a shape the app has, or a `loading.tsx` where the app has one, stops the app at startup until it overrides the page or moves its own. The `overlap` route warning compares a literal page with a parameter page across sources, which within one source it leaves alone.
- Each page payload carries `mounts`, the prefix's URL for the request, from which a package's links are built ([ADR 0051](0051-a-page-package-has-a-generated-file-of-its-own.md)).
- A package's pages ship in the client bundle whether or not they are mounted or replaced; a package listed in the build but not mounted is a route warning in production.
- Mounting inside a `MapGroup`, mounting one package twice, and packages that ship `public/` files are not supported.
