# netjes

The npm half of [Netjes](https://github.com/DennisVerhoeven/netjes), a React framework for building apps and sites on ASP.NET Core: pages in `app/` with the Next.js App Router conventions, C# loaders and server actions, server rendering, client navigation. This package holds the Vite plugin, the client router and components, the Node sidecar that renders pages for the .NET app, the `netjes` command and the app template. The ASP.NET Core half is the [`Netjes`](https://www.nuget.org/packages/Netjes) NuGet package, released at the same version.

## Start an app

You need Node.js `^22.22.2 || ^24.15.0 || >=26.0.0` (Node.js 22.22.2 or a later 22, 24.15.0 or a later 24, or 26 or newer) and the .NET 10 SDK.

```bash
npx netjes new my-app
cd my-app
npm run dev          # http://localhost:5080
```

`netjes new` writes an ASP.NET Core app with React pages, Tailwind CSS, TanStack Query, zod, Vitest and Docker files, and references `netjes` and `Netjes` at the same exact version.

## Add it to a project

```bash
npm install netjes@0.0.1 react react-dom zod @tanstack/react-query
npm install -D vite tailwindcss @tailwindcss/vite
```

```ts
// vite.config.ts
import netjes from 'netjes/vite';
import { defineConfig } from 'vite';

export default defineConfig({ plugins: [netjes()] });
```

Tailwind CSS and the TanStack Query client are on by default; `netjes({ tailwind: false, query: false })` turns them off, and then they need not be installed. `vitest`, `jsdom`, `@testing-library/react` and `web-vitals` are optional peers, used when installed. Use the version of `netjes` that matches the app's `Netjes` NuGet package; `npx netjes info` checks it.

## Entry points

| Import | What it holds |
|---|---|
| `netjes` | Components and hooks: `Link`, `Form`, `Image`, `Script`, `definePage`, `useAction`, `useRouter` and the rest |
| `netjes/vite` | The Vite plugin |
| `netjes/testing` | `createTestApp()`, for rendering pages and components in Vitest |
| `netjes/vitals` | `useReportWebVitals` and `<WebVitals />` |
| `netjes/zod` | `deferred(schema)`, for values that loaders stream with `Page.Defer` |
| `netjes` (bin) | The command: `new`, `dev`, `build`, `start`, `generate`, `routes`, `export`, `info`, `agents` |

Each import is an ES module with its type declarations, so an app's `tsc` reads the declarations and checks only the app's own code, also under stricter compiler options such as `exactOptionalPropertyTypes` or `noPropertyAccessFromIndexSignature` (see [TypeScript setup](https://github.com/DennisVerhoeven/netjes/blob/main/docs/reference/vite-plugin.md#typescript-setup)). The TypeScript sources are in the package too, for the source maps: an editor's go to definition and a stack trace lead to them.

## Documentation

- [Getting started](https://github.com/DennisVerhoeven/netjes/blob/main/docs/getting-started.md)
- [All guides](https://github.com/DennisVerhoeven/netjes/blob/main/docs/README.md)
- [React API](https://github.com/DennisVerhoeven/netjes/blob/main/docs/reference/react-api.md)
- [Vite plugin](https://github.com/DennisVerhoeven/netjes/blob/main/docs/reference/vite-plugin.md)
- [The netjes command](https://github.com/DennisVerhoeven/netjes/blob/main/docs/reference/cli.md)

Report bugs in the [issue tracker](https://github.com/DennisVerhoeven/netjes/issues), and security problems privately, as [SECURITY.md](https://github.com/DennisVerhoeven/netjes/blob/main/SECURITY.md) describes. MIT licensed.
