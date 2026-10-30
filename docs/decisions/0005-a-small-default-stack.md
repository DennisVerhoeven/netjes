# 0005 – A small default stack

Status: accepted

## Context

Every app needs the same pieces around its pages: a way to fetch and cache server state in components, a CSS approach, a test runner that can render pages, and validation at the boundaries, page props from C# among them. Left to each app, they are wired up by hand every time, and one cannot be wired up from outside at all: data that components fetch with TanStack Query is server-rendered only if C# can seed it into the render.

Each runtime dependency of the npm package ships to every app's browsers, and every app has to keep in step with it.

## Decision

The default stack is React, TanStack Query, Tailwind CSS and Vitest. In the browser the package needs React and, unless the app turns it off, TanStack Query, both peer dependencies, and nothing else.

- **TanStack Query.** The runtime provides the `QueryClient`: one per request on the server, and one per page load in the browser, kept across client navigations. C# loaders seed it with `Page.Props(...).WithQuery(key, data)`. The payload carries the seeded queries with their keys, data and timestamps, and the runtime hydrates them through TanStack's `HydrationBoundary`, so seeded data is server-rendered and never overwrites data the client fetched more recently. The default `staleTime` is one minute. `netjes({ query: false })` removes the client and TanStack Query from the bundle; `query: { defaultOptions }` configures it.
- **Tailwind CSS.** `netjes()` includes `@tailwindcss/vite`, and `netjes({ tailwind: false })` leaves it out. Tailwind is an optional peer dependency, loaded only when it is on.
- **Vitest.** `netjes()` sets test defaults that the app's own `test` config overrides: jsdom and Testing Library cleanup when they are installed, and no `e2e/` (Playwright), `dist/`, `bin/` or `obj/` folders. `netjes/testing` renders pages and components with a test router and a test `QueryClient`. Vitest, jsdom and Testing Library are optional peers.
- **Validation without a validation library.** zod is the default in the docs, the template and the generated types, but the runtime does not depend on it. `definePage`, `defineLayout`, `useSharedProps`, `useParams`, `useSearchParams` and `usePublicConfig` accept any [Standard Schema](https://standardschema.dev), so an app can use zod, Valibot or ArkType. zod is an optional peer, used only by `netjes/zod` (`deferred(schema)`, [ADR 0012](0012-streamed-documents-and-deferred-values.md)). The parser for the payload from .NET is written by hand: it parses, and never casts.
- The package's one regular dependency, `@vitejs/plugin-react`, runs inside Vite at build and dev time, not in the browser.

"TanStack" here means TanStack Query. TanStack Router would compete with the router the server owns ([ADR 0002](0002-server-owns-routing.md)).

## Alternatives considered

- **Leave the stack to documented recipes.** No coupling to the framework, but every app repeats the setup, and only the runtime can seed queries from C# into a render.
- **A regular dependency on TanStack Query.** It risks two copies, the app's and the package's, and two React contexts. A peer dependency shares one.
- **Depend on zod in the runtime.** Less code in the framework, but it forces one validation library, and its bundle size, on every app, and ties Netjes releases to zod's.
- **CSS modules or plain CSS as the default.** Vite handles both with no setup, so making either the default would add nothing. Tailwind needs a plugin and an entry stylesheet, which is setup worth doing once.

## Consequences

- Apps install `@tanstack/react-query`, a required peer. With `query` off it stays installed, unused.
- Query keys are a contract between C# (`WithQuery(["likes", slug], ...)`) and TypeScript (`likeKeys.detail(slug)`), kept in step by hand.
- The Vite plugin carries test configuration, so `vite.config.ts` is also the Vitest config.
- The payload parser in `packages/netjes/src/payload.ts` is kept in step with `src/Netjes/Rendering/PagePayload.cs` by hand; both files say so.
- Schemas for page props, params and shared props must validate synchronously, since rendering cannot wait.
