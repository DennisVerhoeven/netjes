# 0010 – The App Router's conventions, with C# layout loaders instead of server components

Status: accepted

## Context

Next.js has two routers. The pages router maps each file in `pages/` to a URL, with one app-wide layout. The App Router in `app/` nests layouts, templates and loading, error and not-found boundaries per folder, adds route groups and parallel and intercepting routes, and lets React Server Components fetch their own data. The App Router covers what the pages router does and more, and it is what Next.js builds on.

Netjes keeps business logic in C#, and React renders in Node sidecars ([ADR 0001](0001-react-renders-in-node-sidecars.md)). Server components would run data access in the sidecar, or call back into .NET for it.

## Decision

- Netjes routes `app/` only, with the App Router's file names. `RouteTable` turns each folder with segment files into a `RouteSegment`: its id, URL prefix and parameters, and its `layout`, `template`, `loading`, `error`, `not-found`, `forbidden` and `unauthorized` files. The payload carries the page's segments, and the client composes layout, template, error boundary and Suspense for each one, as Next.js does.
- A segment's data comes from C#: `MapLayoutLoader(segment, delegate)`, built with `RequestDelegateFactory` so it binds like a minimal API. It binds the route values of its own URL, the query string and services; mapping a loader whose `[FromRoute]` names a route value of a deeper URL throws. Conventions on a layout loader, such as `RequireAuthorization()`, apply to every page endpoint below it, so authorization runs before any loader.
- Client navigations send `X-Netjes-Segments` with the keys of the segments they hold. A key identifies what the loader saw: its folder, the build, the signed-in user (name, identifier and roles) and the route values in its URL prefix. Keys are signed with an HMAC, so a client cannot make up one for a segment it was never given; the secret is random per process unless `Netjes:SegmentKeySecret` sets one for all instances. A loader whose key matches is skipped, and its segment comes back `reused`. Refreshes and posts send no keys.
- Boundaries render on the server when a loader asks for one (`NotFound`, `Forbidden`, `Unauthorized`) or throws: the nearest boundary file above the failing segment, inside the layouts above it. A layout's own boundary files sit inside it, so a failing layout loader renders its parent's. A section whose layout loader's authorization fails stops there: its loader never runs.
- `UseNetjes` stops the app at startup when a `pages/` folder, in the app's root or next to a custom app directory, holds Next.js pages router files: `.tsx`, `.jsx` or `.js` pages, or API routes in `pages/api/`. The message names every special file and up to five other files, and counts the rest. A page gets the `app/` file to move it to (`pages/blog/[slug].tsx` to `app/blog/[slug]/page.tsx`), and a `.js` page a `.jsx` file, since `app/` routes `.tsx` and `.jsx` only. The special files at the root of `pages/` get the root files that take their place: `404` is `not-found`, `500` and `_error` are `error`, and `_app` and `_document` are `layout`. API routes are named as such, to map as minimal API endpoints in C#. `netjes info` checks the `pages/` folder in the app's root by the same rule: it flags the same files but lists up to three pages and three API routes. The check skips tests, anything in a `_` folder and other files whose names start with `_`.

## Alternatives considered

- **React Server Components in the sidecar** (`@vitejs/plugin-rsc`). Pre-1.0, a second data path next to the C# loaders, Flight deserialization vulnerabilities to track, and a hop back to .NET for data.
- **One loader per page only.** Layouts would fetch their shared data again in every page loader, and there would be no place for authorization that covers a whole section.
- **Support the pages router too.** Apps on `pages/` would port unchanged, but every feature would need two designs and two sets of tests, and the precedence rules between the two routers would stay.
- **Read `pages/` as an alias that maps its files to App Router names.** Less work for a port, but two vocabularies in the docs and errors, and unclear cases such as a `pages/_app.tsx` next to an `app/layout.tsx`.
- **Ignore `pages/` without a word.** The least code, but a ported app would answer 404 everywhere with no hint why.

## Consequences

- Segment reuse depends on the key. Data that depends on anything else, such as the query string or a cookie other than the user's, would go stale when a navigation reuses the segment, so it does not belong in a layout loader, as layouts get no `searchParams` in Next.js. An access check on a route value deeper than the layout's own URL belongs in the page loader, since the layout loader neither sees that value nor runs again when it changes.
- Instances behind a load balancer reuse each other's segments only when they share `SegmentKeySecret`; otherwise a navigation that lands on another instance loads its segments in full.
- An app ported from the Next.js pages router moves its files as the startup message says. What `_app.tsx` wrapped around every page goes in the root layout. `_document.tsx` has no file of its own, since Netjes renders the document around the root layout ([ADR 0003](0003-react-renders-the-whole-document.md)); its head tags become metadata, or tags the root layout renders. An `error.tsx` receives `{ error, reset }`, with the status as `error.status`. Other files in `app/` folders are colocated code, not pages.
- The root `not-found.tsx` and `error.tsx` are segment files of the root folder, so the error page also catches errors thrown while rendering in the browser.
- Two files for one convention in one folder, such as `page.tsx` and `page.jsx`, are a route conflict.
- The pages router's hooks (`useRouter().query`, `asPath`, router events, shallow routing) are not planned.
