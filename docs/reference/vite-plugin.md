# Vite plugin

`netjes()` from `netjes/vite` is the Vite plugin every Netjes app uses. This page lists its options and everything it adds to the Vite config, for when you need to change the build, turn part of the default stack off, or understand what the dev server serves.

```ts
// vite.config.ts
import netjes from 'netjes/vite';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [netjes()],
});
```

The plugin returns a list of plugins: `@vitejs/plugin-react`, the Netjes plugin itself, with page packages a plugin that adds them to the app's Tailwind CSS build, and `@tailwindcss/vite` unless Tailwind is off. Put any plugins of your own next to it in `plugins`.

## Options

```ts
netjes({
  appDir: 'app',
  basePath: '/docs',
  tailwind: true,
  query: { defaultOptions: { queries: { staleTime: 5 * 60_000 } } },
  test: true,
  packages: ['@netjes-examples/help-center'],
});
```

| Option | Type | Default | What it does |
|---|---|---|---|
| `appDir` | `string` | `'app'` | The folder of page files, relative to the Vite root. It must match `Netjes:AppDirectory` in the .NET app. |
| `basePath` | `string` | none | The path the app is mounted under with `app.UsePathBase(...)`, such as `'/docs'`. Built asset URLs get it (Vite's `base`); links, forms, redirects and router calls get it from the server at runtime. It must match the base path in `Program.cs`. |
| `tailwind` | `boolean` | `true` | Adds `@tailwindcss/vite`. Import Tailwind with `@import "tailwindcss";` in a stylesheet that a layout imports. With `false`, `tailwindcss` and `@tailwindcss/vite` need not be installed. |
| `query` | `boolean \| { defaultOptions?: DefaultOptions }` | `true` | The built-in TanStack Query client, which loaders seed with `.WithQuery(key, data)`. `false` removes it, and TanStack Query with it, from the bundle. An object configures the client; its `defaultOptions` are merged over the Netjes defaults (`staleTime` of one minute, so seeded data is not fetched again at once). Under Vitest, unless `test` is `false`, the plugin hands the same `defaultOptions` to `createTestApp()`, which keeps all of them but `retryDelay` and the queries' `gcTime` ([Vitest defaults](#vitest-defaults)). |
| `test` | `boolean` | `true` | Vitest defaults, below. `false` adds none. |
| `packages` | `readonly string[]` | `[]` | The [page packages](#page-packages) the app mounts, by npm name. Each must be installed, and needs Tailwind on. |
| `react` | `Options` of `@vitejs/plugin-react` | none | Passed to `@vitejs/plugin-react`, which the plugin includes. |

`query` options are written into the browser bundle as literals, so they can be plain values only: numbers (`Infinity` too), strings, booleans, `null`, arrays and plain objects. A function, such as a `retry` callback, fails when the config loads, with an error naming the option; set those per query, as in `useQuery({ retry })`.

With Tailwind on, a missing `@tailwindcss/vite` fails the config with a message saying to install it (`npm install -D tailwindcss @tailwindcss/vite`) or to pass `tailwind: false`. `packages` together with `tailwind: false` fails the config too: page packages style their pages with Tailwind classes.

## What it adds

### The page module map

The plugin provides `virtual:netjes/pages`, a map of every convention file under `appDir`: `page`, `layout`, `template`, `loading`, `error`, `not-found`, `forbidden`, `unauthorized`, `global-error` and `default`, as `.tsx` or `.jsx`, and of every convention file of the [page packages](#page-packages) it lists. The client entry and the server entry load pages from it by the file id the server names in each payload, under the key `/` and that id: `/app/blog/page.tsx`, `/@netjes-examples/help-center/app/page.tsx`. The module also exports `appDir` as the plugin has it, which `renderDocument` in `netjes/testing` builds a page's route and segments from ([the document](../guides/testing.md#the-document)), and `packages`, each listed package's name and folder of pages (`[]` when there are none). Other files in `appDir` are colocated code and not routes, except a route handler, which stops the build ([Builds](#builds)).

In development the map is rebuilt when Vite's watcher reports a page file added or removed. When the server routes a page file the map does not have yet, the Node process that renders pages asks the plugin to build the map again from the files on disk and renders once more, so a new page works on its first request.

### Page packages

A page package is an npm package that brings pages, layouts and boundaries for the app to mount at a prefix, with a C# half that the .NET app mounts. Its `package.json` marks it with a `netjes` field and names the Netjes versions it supports in `peerDependencies`:

```json
{
  "name": "@netjes-examples/help-center",
  "version": "1.2.0",
  "peerDependencies": { "netjes": "^0.0.4" },
  "netjes": { "pages": "app", "sources": ["app", "src"], "css": "styles/help-center.css", "nuget": "Netjes.Examples.HelpCenter" }
}
```

| Field | Default | What it is |
|---|---|---|
| `pages` | `'app'` | The folder of its convention files. |
| `sources` | `pages`, and `src` when the package has it | The folders that hold its Tailwind classes. |
| `css` | none | A stylesheet the app's Tailwind build imports, such as the package's `@theme` tokens. |
| `nuget` | none | The NuGet package of its C# half. |

Every path is relative and stays inside the package: no `..` or `.`, no leading `/`, no `\`, no drive letter. A package ships its pages as TypeScript, which the app's build compiles; the package type-checks them in its own CI, since the app's `tsc` does not see them.

For each package in `packages`, the plugin:

- finds it in the nearest `node_modules`, by its real path, so a package that a workspace or `npm link` links in is read where it lies;
- adds its convention files to the page module map, each imported by its real path, under its file id: the package's name, its folder of pages and the file's path there (`@netjes-examples/help-center/app/(center)/articles/[slug]/page.tsx`). The id is the same wherever the package is installed;
- adds an `@import` of its `css`, unless the app's stylesheet imports that file already (by `<name>/<css>` or by a relative path), and an `@source` for each of its `sources`, after the `@import "tailwindcss"` of the app's stylesheet, on the same line. Tailwind's own detection leaves out `node_modules` and every folder outside the app. Both paths are relative to the stylesheet, with `/`, so the package need not export its CSS. The import may have options, as in `@import "tailwindcss" source(none)`;
- writes it into the client build's record ([Builds](#builds)), and stops both builds on a route handler in it, named by its file id;
- sets up the dev server for it ([Resolution and dependencies](#resolution-and-dependencies), [The dev server](#the-dev-server)).

The config fails, in one line that names the package and the fix, for a package that is not installed (`npm install <name>`), one whose `package.json` has no `netjes` field or no `peerDependencies.netjes`, a path in the field that leaves the package, a `css` file or a `sources` folder the package lacks, a name listed twice, and a name that equals the app's folder of pages or its first folder (`app`, or `src` for `src/app`), whose ids would look like the app's.

In development, when the .NET app renders a page of an installed page package that `packages` leaves out, the render fails with `netjes: no module for <id> in this build: add '<name>' to netjes({ packages }) in vite.config.ts`, naming the app's Vite config.

### The query client

`virtual:netjes/query` holds the TanStack Query shell: a `QueryClient` per render on the server and per page load in the browser, hydrated from each payload's seeded queries. With `query: false` the module exports nothing and pages render without it.

### Builds

`vite build` builds the client:

- input: the Netjes client entry; output: `dist/client/`, emptied first;
- a manifest in `dist/client/.vite/manifest.json`, from which the .NET app learns the routes and the assets of each page in production. The app never serves the `.vite/` folder: a request for it answers 404;
- with `packages`, a build record in `dist/client/.vite/netjes.json`, where the .NET app finds the page packages in production, which has no `node_modules` to read them from. It lists each package's name, version, `peerDependencies.netjes` and folder of pages, and maps each of its convention files' ids to the file's key in the manifest. Without packages there is no record.

```json
{
  "packages": [
    {
      "name": "@netjes-examples/help-center",
      "version": "1.2.0",
      "netjes": "^0.0.4",
      "pages": "app",
      "files": { "@netjes-examples/help-center/app/page.tsx": "node_modules/@netjes-examples/help-center/app/page.tsx" }
    }
  ]
}
```

`vite build --ssr` builds the server bundle:

- input: the Netjes server entry; output: `dist/server/server-entry.js`, as an ES module, with every dependency bundled in, so production needs no `node_modules`;
- the script of the Node process that renders pages, copied to `dist/server/netjes-sidecar.mjs`;
- no copy of `public/`.

Both builds stop on a route handler in `appDir` or in a page package's folder of pages: a file named `route` plus one extension (`route.ts`, `route.js`; a test such as `route.test.ts` is not one), outside private folders (`_lib/`) and slots (`@modal/`). The error names the first in ordinal order, with the message the app stops with in development: route handlers are C# in Netjes, and an API is a minimal API endpoint (`app.MapGet`, `app.MapPost`; [What is not there](../guides/routing.md#what-is-not-there)).

`netjes build` runs both, after regenerating `generated/netjes.ts`, and `dotnet publish` runs `npm run build`, which the template defines as `vite build && vite build --ssr`.

### Resolution and dependencies

- `react`, `react-dom` and, with the query client, `@tanstack/react-query` are deduplicated, so the app and `netjes` share one copy even when `netjes` is linked from elsewhere (a `file:` dependency or `npm link`). Under Vitest, `@testing-library/react` is too. With page packages, `netjes` and `zod` are as well, which a linked package would otherwise resolve from its own folder.
- Dependencies are pre-bundled from the page files (there is no `index.html` to scan), including `react`, `react-dom/client`, TanStack Query and, when it is installed, `web-vitals`. `netjes` itself is left out: it imports the plugin's virtual modules (the page module map and the query shell), and the app's pages and the plugin's entries must share one copy of each of its modules. For the same reasons Vite processes it for server rendering in development too (`ssr.noExternal`).
- Page packages are left out of pre-bundling and processed for server rendering in development, as `netjes` is, so a page that imports a package by name and the package's own pages share one instance of each of its modules. Their convention files are pre-bundling entries too, and each of their CommonJS dependencies is pre-bundled through the package (`'<package> > <dependency>'`), as Vite asks for a dependency of a package it does not pre-bundle.
- `appType` is `'custom'`: the .NET app serves the documents, and Vite serves modules.

### TypeScript setup

`netjes` ships its modules as JavaScript with declaration files (`.d.ts`), so the app's `tsc` checks the app's own code against the declarations of `netjes`, not against its sources. The app may add stricter compiler options, such as `exactOptionalPropertyTypes` or `noPropertyAccessFromIndexSignature`: the declarations pass under them, with `skipLibCheck` on or off. They resolve under `moduleResolution` `bundler` (the template's), `node16` and `nodenext`, which read the package's `exports`.

The TypeScript sources ship in the package too: the declaration maps and source maps point at them, so an editor's go to definition and a stack trace lead to the TypeScript.

A new app has three configs. `tsconfig.json` lists no files and points to the two others, as in create-vite's React-TS template. `tsconfig.node.json` checks `vite.config.ts`, `playwright.config.ts`, `e2e/` and `scripts/` with Node's types. `tsconfig.app.json` checks every other `.ts` and `.tsx` file in the app, the Vitest tests included, without them, so a component that reads `process` fails the check. `npm run typecheck` runs `tsc -p` on those two, since `tsc -b` would write `.tsbuildinfo` files. `tsc -p tsconfig.json` and a bare `tsc` check nothing and exit 0, since that config lists no files. [Add Netjes to an existing app](../getting-started.md#add-netjes-to-an-existing-app) shows all three ([ADR 0048](../decisions/0048-a-new-app-has-a-csharp-test-project-and-a-playwright-suite.md)).

### Vitest defaults

Under Vitest, unless `test` is `false`, the plugin sets these, each only if the app's own `test` config leaves it unset, with two exceptions, which it adds to what the app sets: Netjes's own setup file, in `setupFiles`, and `provide['netjes:query']`.

| Setting | Value |
|---|---|
| `environment` | `'jsdom'`, when `jsdom` is installed |
| `exclude` | `node_modules`, `dist`, `bin`, `obj`, `e2e` (for Playwright) and `.git` folders |
| `setupFiles` | In every app, Netjes's own setup file, which lets Testing Library work with Vitest's fake timers ([Fake timers](../guides/testing.md#fake-timers)) and hands `createTestApp()` the app's query defaults; Vite puts it after the app's own setup files. Before it, when `@testing-library/react` is installed and the app sets no `setupFiles`, a setup file that unmounts what each test rendered (Testing Library's `cleanup`, `netjes/testing/setup`); an app that sets them lists `netjes/testing/setup` there itself |
| `provide` | `'netjes:query'`: the `defaultOptions` of the `query` option, `{}` when there are none, which Netjes's setup file reads with `inject()`. `createTestApp()` merges them over the Netjes defaults, with its own `retryDelay: 0`, and `gcTime: Infinity` for queries, over them ([Queries that fail](../guides/testing.md#queries-that-fail)). Not set with `query: false`. The app's own `provide` keys are kept. In Vitest's browser mode the value travels as JSON, so an `Infinity` in it arrives as `null` |

It sets no `include`, so Vitest's own pattern (`*.test.*` and `*.spec.*` files) applies outside those folders. Tests then render with `createTestApp()` from `netjes/testing`; see [Testing](../guides/testing.md).

Under Vitest 5, an inline project in `test.projects` that changes no Vite options shares the Vite server of the config that declares it (`sharedViteServer`, on by default), and Vitest builds its `test` options from that config's as written, before any plugin adds to them. Such a project gets none of the plugin's `environment`, `exclude` and setup files, so its tests get neither the [fake-timer bridge](../guides/testing.md#fake-timers) nor the app's query defaults in `createTestApp()`. Set `sharedViteServer: false` in the root's `test` options, and Vitest resolves the config for each project, with the plugin's settings.

## The dev server

In development the .NET app starts a Node process that runs the Vite dev server with this config, and renders pages through it. The browser loads modules and hot updates from that server, on its own port.

- It listens on port 5173, which `Netjes:ViteDevPort` changes, with `strictPort`: the browser loads modules and hot updates from that port, so when something else has it the dev server does not move to another one. The app logs that the port is in use and how to move Vite (`Netjes__ViteDevPort=5174`), in one line, and stops with exit code 1; `netjes dev` checks the port before it starts the app.
- When it does not start for another reason, such as an error in `vite.config.ts` or a package the config imports that is not installed, the app logs what Vite reported, its code frame included, and what to do (`npm install` for a package it cannot find), in one log entry, and stops with exit code 1.
- It binds to `localhost`. `NETJES_VITE_HOST=0.0.0.0` in the environment of `npm run dev` exposes it to the network, as the template's development container does; `NETJES_VITE_ORIGIN` sets the origin the browser uses, when it reaches the server by another address.
- It may read files from the workspace root, unless your config sets `server.fs.allow`, from the `netjes` package, and from each page package, by its real path.
- It watches the pages and sources of each page package that lies outside `node_modules` (linked by a workspace or `npm link`), and a page that comes or goes there rebuilds the page module map.

The dev server's root is the app's folder, which also holds the .NET side, so the plugin denies these to the dev server, on top of Vite's own defaults (`.env`, `.env.*`, certificates and `.git`):

- `appsettings*.json`, `launchSettings.json`, `secrets.json`, `nuget.config` and `*.user` files;
- `*.cs`, `*.csproj`, `*.pdb` and `*.dll` files, `Directory.Build.*` and `global.json`;
- dev certificates and keys (`*.pfx`, `*.p12`, `*.key`) and SQLite databases (`*.db`, `*.sqlite`, `*.sqlite3`);
- Compose files (`compose*.yml`, `docker-compose*.yml`, and `.yaml`);
- the `bin/`, `obj/` and `dist/server/` folders below the workspace root, and below each page package, wherever it lies: a package linked from outside the workspace holds its C# half's build output there.

A `server.fs.deny` of your own is added to that list. Everything else under the allowed folders is readable by anyone who can reach the port, which is why the server binds to localhost: expose it only on a network you trust.
