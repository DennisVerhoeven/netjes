# 0050 – A page package ships sources that the app builds

Status: accepted; amends [ADR 0004](0004-vite-for-dev-server-and-builds.md)

## Context

A page package's pages render inside the app's: they read the payload through `netjes`'s React contexts, use the app's router, TanStack Query client and zod, and wear the app's styles. A package could ship them compiled, as most npm packages ship their code, with a stylesheet built from its own Tailwind config. Then the package's chunks carry or import their own copies of the modules the app's chunks hold, and two copies of `netjes` mean two routers and two sets of contexts, in which the package's pages find nothing. Its stylesheet repeats Tailwind's base and the utilities the app has already, in an order that decides which wins, and it cannot follow the app's theme.

Vite does not help by default either. Its dev server pre-bundles what lies in `node_modules` and treats it as external for server rendering, so a package's pages and the app's pages would load `netjes` through two module graphs. `import.meta.glob`, which made the page module map ([ADR 0004](0004-vite-for-dev-server-and-builds.md)), drops files in `node_modules` and keys a linked package by its path through the link. Tailwind's detection leaves out `node_modules` and every folder outside the app, so no class that only a package uses gets a rule.

## Decision

- A page package ships its pages and components as TypeScript (`.tsx`) and its styles as Tailwind classes, and the app's Vite build compiles both, with the app's settings and the app's one copy of each module.
- The npm half says what it ships in a `netjes` field of its `package.json`: `pages`, the folder of its convention files (`app` by default); `sources`, the folders that hold its classes (the pages folder, and `src` when it has one); `css`, a stylesheet with its design tokens; and `nuget`, the name of its C# half. Every path is relative and stays inside the package. `peerDependencies.netjes` is required ([ADR 0052](0052-a-page-package-whose-versions-do-not-fit-stops-the-app.md)).
- `netjes({ packages })` lists the packages the app builds. For each, the plugin finds the package in the nearest `node_modules` by its real path, and adds one `import()` of the real path of each convention file to `virtual:netjes/pages`, keyed by the file's id ([ADR 0049](0049-page-packages-are-mounted-into-the-route-table.md)). The module also exports `packages`, `[]` without any, since Rollup refuses an import of a name a module does not export. Both builds stop on a `route.ts` in a package, as in the app.
- Tailwind: a plugin of its own, ahead of Tailwind's, adds an `@import` of each package's `css` and an `@source` for each of its `sources` after the `@import "tailwindcss"` of the app's stylesheet, both relative to the stylesheet and written with `/`. It skips an import the stylesheet has already. The app's Tailwind version and theme apply to the package, whose pages use the tokens of its `css`, CSS variables with a prefix of the package's own (`--help-*`) that an app sets to restyle it. `tailwind: false` together with packages stops the config.
- Development: each package goes in `optimizeDeps.exclude` and `ssr.noExternal`, as `netjes` does, with its convention files in `optimizeDeps.entries` and each of its CommonJS dependencies pre-bundled through it (`'<package> > <dependency>'`); `netjes` and `zod` are deduplicated, which a linked package would resolve from its own folder; its real path is in `server.fs.allow`, with its `bin/`, `obj/` and `dist/server/` folders denied; and a package outside `node_modules`, linked by a workspace, is watched, so a page added to it rebuilds the module map.
- The app's `tsc` does not see a package's pages, which only the virtual module reaches. A package type-checks them in its own CI, under the strictest flags an app may set: `strict`, `exactOptionalPropertyTypes` and `noPropertyAccessFromIndexSignature`.

## Alternatives considered

- **Compiled JavaScript and a built stylesheet.** Two copies of the shared modules, Tailwind's rules twice, and no theme from the app. A package would also have to know the app's base path and asset handling to emit its own chunks.
- **The app writes the `@source` and `@import` lines itself.** It works, and every app repeats it for every package, with paths into `node_modules` that differ between an installed and a linked package and between operating systems. The entry in `packages` is the one place an app names a package's npm half.
- **A glob of the package's folder.** It drops `node_modules`, and keys a linked package by the link, so the keys would differ between development in a workspace and an installed package.
- **Packages without Tailwind.** Supporting them means a second way to ship styles and a second order of precedence against the app's. Netjes turns Tailwind on by default, and a package's own tokens keep it from depending on the app's theme.
- **Type-checking the package's pages in the app's `npm run typecheck`.** `tsc` would have to be pointed into `node_modules` past the virtual module, and the app's compiler flags would judge code the app does not own. The package's CI checks it once, under every flag.

## Consequences

- One copy of each shared module, and one stylesheet for the app and its packages, in development and in the build. The app's Tailwind must be a version the package's classes are written for, Tailwind 4 today.
- A package's pages are compiled and bundled with the app's, so they add to its build time, and they ship in the client bundle whether or not the app mounts them or replaces one of them.
- CSS that a package writes by hand stays scoped under its own root element, since the app's pages share the stylesheet.
- A package linked from outside the app's workspace, a checkout next to it, holds its C# half's build output in its folder; the dev server denies those folders, as it denies the app's.
- Yarn Plug'n'Play, which has no `node_modules` folder, is not supported.
