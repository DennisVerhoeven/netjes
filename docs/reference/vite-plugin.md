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

The plugin returns a list of plugins: `@vitejs/plugin-react`, the Netjes plugin itself, and `@tailwindcss/vite` unless Tailwind is off. Put any plugins of your own next to it in `plugins`.

## Options

```ts
netjes({
  appDir: 'app',
  basePath: '/docs',
  tailwind: true,
  query: { defaultOptions: { queries: { staleTime: 5 * 60_000 } } },
  test: true,
});
```

| Option | Type | Default | What it does |
|---|---|---|---|
| `appDir` | `string` | `'app'` | The folder of page files, relative to the Vite root. It must match `Netjes:AppDirectory` in the .NET app. |
| `basePath` | `string` | none | The path the app is mounted under with `app.UsePathBase(...)`, such as `'/docs'`. Built asset URLs get it (Vite's `base`); links, forms, redirects and router calls get it from the server at runtime. It must match the base path in `Program.cs`. |
| `tailwind` | `boolean` | `true` | Adds `@tailwindcss/vite`. Import Tailwind with `@import "tailwindcss";` in a stylesheet that a layout imports. With `false`, `tailwindcss` and `@tailwindcss/vite` need not be installed. |
| `query` | `boolean \| { defaultOptions?: DefaultOptions }` | `true` | The built-in TanStack Query client, which loaders seed with `.WithQuery(key, data)`. `false` removes it, and TanStack Query with it, from the bundle. An object configures the client; its `defaultOptions` are merged over the Netjes defaults (`staleTime` of one minute, so seeded data is not fetched again at once). |
| `test` | `boolean` | `true` | Vitest defaults, below. `false` adds none. |
| `react` | `Options` of `@vitejs/plugin-react` | none | Passed to `@vitejs/plugin-react`, which the plugin includes. |

`query` options are written into the browser bundle as literals, so they can be plain values only: numbers (`Infinity` too), strings, booleans, `null`, arrays and plain objects. A function, such as a `retry` callback, fails when the config loads, with an error naming the option; set those per query, as in `useQuery({ retry })`.

With Tailwind on, a missing `@tailwindcss/vite` fails the config with a message saying to install it (`npm install -D tailwindcss @tailwindcss/vite`) or to pass `tailwind: false`.

## What it adds

### The page module map

The plugin provides `virtual:netjes/pages`, a map of every convention file under `appDir`: `page`, `layout`, `template`, `loading`, `error`, `not-found`, `forbidden`, `unauthorized`, `global-error` and `default`, as `.tsx` or `.jsx`. The client entry and the server entry load pages from it by the path the server names in each payload. Other files in `appDir` are colocated code and not routes, except a route handler, which stops the build ([Builds](#builds)).

In development the map is rebuilt when Vite's watcher reports a page file added or removed. When the server routes a page file the map does not have yet, the Node process that renders pages asks the plugin to build the map again from the files on disk and renders once more, so a new page works on its first request.

### The query client

`virtual:netjes/query` holds the TanStack Query shell: a `QueryClient` per render on the server and per page load in the browser, hydrated from each payload's seeded queries. With `query: false` the module exports nothing and pages render without it.

### Builds

`vite build` builds the client:

- input: the Netjes client entry; output: `dist/client/`, emptied first;
- a manifest in `dist/client/.vite/manifest.json`, from which the .NET app learns the routes and the assets of each page in production. The app never serves the `.vite/` folder: a request for it answers 404.

`vite build --ssr` builds the server bundle:

- input: the Netjes server entry; output: `dist/server/server-entry.js`, as an ES module, with every dependency bundled in, so production needs no `node_modules`;
- the script of the Node process that renders pages, copied to `dist/server/netjes-sidecar.mjs`;
- no copy of `public/`.

Both builds stop on a route handler in `appDir`: a file named `route` plus one extension (`route.ts`, `route.js`; a test such as `route.test.ts` is not one), outside private folders (`_lib/`) and slots (`@modal/`). The error names the first in ordinal order, with the message the app stops with in development: route handlers are C# in Netjes, and an API is a minimal API endpoint (`app.MapGet`, `app.MapPost`; [What is not there](../guides/routing.md#what-is-not-there)).

`netjes build` runs both, after regenerating `generated/netjes.ts`, and `dotnet publish` runs `npm run build`, which the template defines as `vite build && vite build --ssr`.

### Resolution and dependencies

- `react`, `react-dom` and, with the query client, `@tanstack/react-query` are deduplicated, so the app and `netjes` share one copy even when `netjes` is linked from elsewhere (a `file:` dependency or `npm link`). Under Vitest, `@testing-library/react` is too.
- Dependencies are pre-bundled from the page files (there is no `index.html` to scan), including `react`, `react-dom/client`, TanStack Query and, when it is installed, `web-vitals`. `netjes` itself is left out: it imports the plugin's virtual modules (the page module map and the query shell), and the app's pages and the plugin's entries must share one copy of each of its modules. For the same reasons Vite processes it for server rendering in development too (`ssr.noExternal`).
- `appType` is `'custom'`: the .NET app serves the documents, and Vite serves modules.

### TypeScript setup

`netjes` ships its modules as JavaScript with declaration files (`.d.ts`), so the app's `tsc` checks the app's own code against the declarations of `netjes`, not against its sources. The app may add stricter compiler options, such as `exactOptionalPropertyTypes` or `noPropertyAccessFromIndexSignature`: the declarations pass under them, with `skipLibCheck` on or off. They resolve under `moduleResolution` `bundler` (the template's), `node16` and `nodenext`, which read the package's `exports`.

The TypeScript sources ship in the package too: the declaration maps and source maps point at them, so an editor's go to definition and a stack trace lead to the TypeScript.

### Vitest defaults

Under Vitest, unless `test` is `false`, the plugin sets these, each only if the app's own `test` config leaves it unset:

| Setting | Value |
|---|---|
| `environment` | `'jsdom'`, when `jsdom` is installed |
| `exclude` | `node_modules`, `dist`, `bin`, `obj`, `e2e` (for Playwright) and `.git` folders |
| `setupFiles` | A setup file that unmounts what each test rendered (Testing Library's `cleanup`), when `@testing-library/react` is installed |

It sets no `include`, so Vitest's own pattern (`*.test.*` and `*.spec.*` files) applies outside those folders. Tests then render with `createTestApp()` from `netjes/testing`; see [Testing](../guides/testing.md).

## The dev server

In development the .NET app starts a Node process that runs the Vite dev server with this config, and renders pages through it. The browser loads modules and hot updates from that server, on its own port.

- It listens on port 5173, which `Netjes:ViteDevPort` changes, with `strictPort`: the browser loads modules and hot updates from that port, so when something else has it the dev server does not move to another one. The app logs that the port is in use and how to move Vite (`Netjes__ViteDevPort=5174`), in one line, and stops with exit code 1; `netjes dev` checks the port before it starts the app.
- When it does not start for another reason, such as an error in `vite.config.ts` or a package the config imports that is not installed, the app logs what Vite reported, its code frame included, and what to do (`npm install` for a package it cannot find), in one log entry, and stops with exit code 1.
- It binds to `localhost`. `NETJES_VITE_HOST=0.0.0.0` in the environment of `npm run dev` exposes it to the network, as the template's development container does; `NETJES_VITE_ORIGIN` sets the origin the browser uses, when it reaches the server by another address.
- It may read files from the workspace root, unless your config sets `server.fs.allow`, and from the `netjes` package.

The dev server's root is the app's folder, which also holds the .NET side, so the plugin denies these to the dev server, on top of Vite's own defaults (`.env`, `.env.*`, certificates and `.git`):

- `appsettings*.json`, `launchSettings.json`, `secrets.json`, `nuget.config` and `*.user` files;
- `*.cs`, `*.csproj`, `*.pdb` and `*.dll` files, `Directory.Build.*` and `global.json`;
- dev certificates and keys (`*.pfx`, `*.p12`, `*.key`) and SQLite databases (`*.db`, `*.sqlite`, `*.sqlite3`);
- Compose files (`compose*.yml`, `docker-compose*.yml`, and `.yaml`);
- the `bin/`, `obj/` and `dist/server/` folders below the workspace root.

A `server.fs.deny` of your own is added to that list. Everything else under the allowed folders is readable by anyone who can reach the port, which is why the server binds to localhost: expose it only on a network you trust.
