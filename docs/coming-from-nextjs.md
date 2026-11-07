# Coming from Next.js

This page is for developers who know the Next.js App Router. It maps each Next.js concept to its Netjes counterpart, then explains where the two differ. Netjes follows the App Router's file conventions in `app/` and its React APIs where it can, and does the server's part in ASP.NET Core and C#.

## Concept by concept

### Files and folders

| Next.js | Netjes |
|---|---|
| `app/` with `page.tsx` | The same. Page and segment files are `.tsx` or `.jsx`; every other file in `app/` is colocated code. [Routing](guides/routing.md) |
| `layout.tsx` | The same, and it stays mounted across navigations. A root layout renders its content, not `<html>` and `<body>`: Netjes renders the document around it ([ADR 0003](decisions/0003-react-renders-the-whole-document.md)). It also puts `suppressHydrationWarning` on `<html>` and `<body>` itself, which a Next.js app writes into its root layout when a script sets an attribute on them before hydration ([ADR 0026](decisions/0026-netjes-html-and-body-skip-reacts-attribute-check.md)) |
| `template.tsx`, `loading.tsx`, `error.tsx`, `not-found.tsx`, `forbidden.tsx`, `unauthorized.tsx`, `global-error.tsx` | The same files. Built-in pages stand in for a missing `not-found.tsx` or `error.tsx`. `global-error.tsx` belongs at the root of `app/`; anywhere else it stops the app at startup |
| `[slug]`, `[...slug]`, `[[...slug]]`, `(group)`, `_private` | The same. Each page becomes an ASP.NET Core route (`[slug]` is `{slug}`), so ASP.NET Core's matcher decides precedence |
| Parallel routes: `@slot` folders, `default.tsx` | The same: the layout gets each slot as a prop |
| Intercepting routes: `(.)`, `(..)`, `(...)` | The same, inside a `@slot` folder. Intercepting folders outside a slot are not supported |
| `route.ts` | A minimal API endpoint (`app.MapGet`, `app.MapPost`). A `route.ts` file in `app/` stops the app in development and fails the production build, saying so. `MapGet` answers `GET` only, where a route handler's `GET` also answers `HEAD`: map both with `MapMethods(pattern, [HttpMethods.Get, HttpMethods.Head], handler)` ([HEAD requests](guides/routing.md#head-requests)). An endpoint whose URLs a catch-all page matches too, such as `/docs/{**path:regex(\.md$)}` next to `app/docs/[...path]/page.tsx`, needs `.WithOrder(-1)` ([Endpoints next to pages](guides/routing.md#endpoints-next-to-pages)) |
| `middleware.ts` | ASP.NET Core middleware, in `Program.cs` |
| `public/` | `public/` |
| `sitemap.ts`, `robots.ts` | `app.MapSitemap(siteUrl)` and `app.MapRobotsTxt(siteUrl)`, built from the route table. `netjes export` writes both, the sitemap with the pages it exports. [Metadata](guides/metadata.md) |
| `icon.png`, `opengraph-image.tsx`, `manifest.json` | Not yet: set icons and Open Graph images through the Metadata API |
| `instrumentation.ts` | OpenTelemetry in `Program.cs`, with the `Netjes` ActivitySource and Meter. [Deployment](guides/deployment.md) |

### Data

| Next.js | Netjes |
|---|---|
| A server component that fetches its data | A C# page loader: `app.MapPageLoader("/blog/[slug]", (string slug, PostStore posts) => Page.Props(...))`. It binds like a minimal API handler. [Data loading](guides/data-loading.md) |
| A layout that fetches its data | A C# layout loader: `app.MapLayoutLoader("/dashboard", ...)`. It is skipped on navigations within its section while its route values and user stay the same |
| `getServerSideProps` (Pages Router) | `MapPageLoader` |
| `params` and `searchParams` | Loader parameters in C#; `useParams()` and `useSearchParams()` in React, each with an optional schema |
| `cookies()`, `headers()` | `HttpContext` or `HttpRequest` as a loader parameter, with the values passed on as props |
| `React.cache`, request memoization | Scoped services: one instance per request |
| Streaming a promise to a client component | `Page.Defer(task)` in the loader, read with `use()` inside a Suspense boundary, typed with `deferred(schema)`. [Rendering and streaming](guides/rendering-and-streaming.md) |
| TanStack Query with `HydrationBoundary` | Built in: `.WithQuery(key, data)` on the loader's result seeds the query, so `useQuery` renders it on the server |
| `generateStaticParams` | `.StaticParams(...)` on the loader, for `netjes export` |
| `NEXT_PUBLIC_` environment variables | The `Netjes:PublicConfig` configuration section, read with `usePublicConfig(schema)` at runtime ([ADR 0009](decisions/0009-public-config-at-runtime.md)) |
| `draftMode()` | `IDraftMode` in loaders, `useDraftMode()` in React |

### Server actions and forms

| Next.js | Netjes |
|---|---|
| `'use server'` functions | `app.MapAction("posts.like", delegate)`, a C# endpoint at `/_netjes/actions/posts.like`, called from React through the generated `serverActions['posts.like']` ([ADR 0011](decisions/0011-server-actions-as-csharp-endpoints.md)). [Actions and forms](guides/actions-and-forms.md) |
| `useActionState` | `useAction(action)`, which wraps it: `[state, formAction, isPending]` |
| `useFormStatus`, `useOptimistic` | React's own; they work inside `<Form action={formAction}>` |
| `<form action={fn}>` | `<Form action={formAction}>`, which also posts without JavaScript |
| Validating input with zod in the action | DataAnnotations or `IValidatableObject` on the action's input, checked before it runs; `ServerAction.Invalid(field, message)` for other errors |
| `redirect()` in an action | `ServerAction.Redirect(url)` |
| `revalidatePath`, `revalidateTag` | `ServerAction.Ok().RevalidatePath(path)` and `.Revalidate(tags)`; `IPageCache` outside actions |
| `refresh()` | `ServerAction.Ok().Refresh()`, or `useRouter().refresh()` |
| `serverActions.bodySizeLimit`, `serverActions.allowedOrigins` | `Netjes:ActionBodySizeLimit` (1 MB by default) and `Netjes:AllowedActionOrigins`, which admits same-site origins only: a cross-site post is refused even when its origin is listed. [Options](reference/options.md) |
| A form posting to an API route | `app.MapPageAction("/guestbook", delegate)` with `<Form method="post">`: the page's own POST handler, which renders the page again |

### Navigation

| Next.js | Netjes |
|---|---|
| `next/link` | `<Link>`: client navigation, prefetching on hover and focus, and a `ref` to its `<a>`. Unlike `next/link`, it sets `aria-current="page"` on a link to the page on screen, by its path and the query parameters it names, and never on a `#` link. [Links](guides/routing.md#links) |
| `useRouter()` from `next/navigation` | `useRouter()`: `push`, `replace`, `prefetch`, `refresh`, `back`, `forward` |
| `usePathname`, `useSearchParams`, `useParams`, `useSelectedLayoutSegment(s)` | The same names, from `netjes` |
| `notFound()`, `forbidden()`, `unauthorized()` | `Page.NotFound()`, `Page.Forbidden()`, `Page.Unauthorized()`, returned from a page or layout loader |
| `redirect()`, `permanentRedirect()` | `Page.Redirect(url)` and `Page.Redirect(url, permanent: true)`; `Page.LocalRedirect(url)` for a URL taken from the request |
| The client router cache | The router shows the last 50 pages again on back and forward without a request, and forgets them when an action signs someone in or out ([ADR 0014](decisions/0014-signing-in-or-out-clears-what-back-shows.md)) |
| `basePath` | `app.UsePathBase("/docs")` and `netjes({ basePath: '/docs' })` |
| i18n routing | A `[lang]` folder with `AddNetjesLocalization`, `UseNetjesLocalization` and `MapLocaleRedirect`; `useLocale()` in React |
| `redirects`, `rewrites`, `headers` in `next.config.js` | ASP.NET Core's URL Rewriting middleware, YARP and middleware. [Redirects, rewrites and headers](guides/redirects-rewrites-headers.md) |

### Metadata, images and scripts

| Next.js | Netjes |
|---|---|
| `export const metadata` | The same, on pages and layouts: title templates, description, Open Graph, Twitter, robots, canonical. Twitter tags that are not set fill in from Open Graph and the page's title and description, as in Next.js |
| `generateMetadata` | `.WithMetadata(new PageMetadata { Title = post.Title })` on the loader's result, merged over the files' metadata |
| `next/head` | `metadata`, or React 19's own `<title>` and `<meta>` |
| `next/script` | `<Script strategy="afterInteractive">`, `lazyOnload` or `beforeInteractive`. A `beforeInteractive` script can stand in any component the server renders in the shell, not only the root layout: inline code runs where it stands, before what follows it is painted, and external scripts run in order once the shell is parsed, before the page's code and hydration ([code that runs before the first paint](guides/metadata.md#code-that-runs-before-the-first-paint)) |
| `next/image` | `<Image>` and the `/_netjes/image` endpoint: configured widths, allow-listed remote hosts, WebP with `Netjes.Images.SkiaSharp`. [Images](guides/images.md) |
| `images.remotePatterns`, `deviceSizes`, `imageSizes` | `Netjes:Images:RemoteHosts` and `Netjes:Images:Widths` |
| `next/dynamic` | `dynamic(() => import('./Chart.tsx'), { ssr: false })`, and `clientOnly(Component)` |
| `next/font` | Not built in: load fonts with CSS, as in any Vite app |

### Caching

| Next.js | Netjes |
|---|---|
| Static by default | Dynamic by default: every page renders per request, and goes out `no-store`, since pages carry per-user shared props and antiforgery tokens. [Caching](guides/caching.md) |
| ISR, `export const revalidate` | `.CachePage(TimeSpan.FromSeconds(30))` on the loader, on ASP.NET Core output caching. In the Development environment every request renders the page, as `next dev` renders routes on each request; where Next.js keeps fetch results there (`force-cache`, `serverComponentsHmrCache`), cache slow data in your services. [Caching](guides/caching.md#in-the-development-environment). Each query string is cached as a page of its own, so `?utm_source=feed` stores another copy: name the keys a page reads with `.CacheQueryKeys(...)`, and other query strings render per request ([query strings a page does not read](guides/caching.md#query-strings-a-page-does-not-read)) |
| `export const dynamic` and other segment config | Endpoint conventions on the loader: `.CachePage(...)`, `.StaticParams(...)`, `.RequireAuthorization()` |
| On-demand revalidation in a route handler | `IPageCache.RevalidatePathAsync(path)` and `RevalidateTagAsync(tag)`, from any endpoint or background job |
| `unstable_cache`, the fetch cache | .NET's own caching (`IMemoryCache`, `HybridCache`) in your services |

### Tooling

| Next.js | Netjes |
|---|---|
| `create-next-app` | `npx netjes new my-app`. [Getting started](getting-started.md) |
| `next dev`, `next build`, `next start` | `netjes dev`, `netjes build`, `netjes start`; `dotnet publish` runs the build and ships it. [The netjes command](reference/cli.md) |
| `output: 'export'` | `netjes export`. Exported pages navigate with full page loads, and `useSearchParams()` sees no query string there: read `window.location.search` in an effect ([in the browser](guides/static-export.md#in-the-browser)). The export warns about loaders that read the query string. A `GET` endpoint marked `.IncludeInExport()` is written as a file, like a route handler with `dynamic = 'force-static'`. [Static export](guides/static-export.md) |
| `next.config.js` | `NetjesOptions` in C# (and the `Netjes` configuration section) for the server, `netjes()` options in `vite.config.ts` for the build. [Options](reference/options.md), [Vite plugin](reference/vite-plugin.md) |
| Typed routes | Generated from the page files: `route('/blog/[slug]', { slug })`, with the query as its third argument and `{ hash }` as its fourth for a `#fragment`, and `<Link href>` checks its URL. Endpoints get typed URLs from their C# handlers: `endpointUrl('GET /api/posts/{slug}/likes/', { slug })` ([Endpoint URLs](guides/data-loading.md#endpoint-urls)) |
| Fast Refresh, Turbopack | Vite with React Fast Refresh ([ADR 0004](decisions/0004-vite-for-dev-server-and-builds.md)) |
| Tailwind CSS setup | On by default in `netjes()`: `@import "tailwindcss"` in a stylesheet the root layout imports |
| Jest or Vitest, `next-router-mock` | Vitest defaults in `netjes()`, and `createTestApp()` from `netjes/testing`. A new app also has a C# test project in `tests/<App>.Tests`, for loaders and actions, and a Playwright suite in `e2e/`. [Testing](guides/testing.md) |
| `useReportWebVitals` | `useReportWebVitals` from `netjes/vitals`; `<WebVitals />` sends them to .NET metrics |
| CSP nonces through middleware | `NetjesOptions.ContentSecurityPolicy` with `{nonce}`, and `useNonce()`. [Security](guides/security.md) |
| A CSP without nonces, for static or cached pages | `NetjesOptions.ContentSecurityPolicy` without `{nonce}`, with `NetjesContentSecurityPolicy.ScriptHashes` in its `script-src`, or `CSP_SCRIPT_HASHES` from `netjes` for a policy written in JavaScript. Pages under it can be output-cached, but must not stream. [Security](guides/security.md#hash-based) |

## The real differences

### No React Server Components

In Next.js a server component can query a database and render without shipping its code. Netjes has no server components: data comes from C# loaders as JSON props, and every component is a client component that renders on the server and hydrates in the browser ([ADR 0010](decisions/0010-app-router-with-csharp-layout-loaders.md)). So:

- Data access, secrets and authorization live in C#, never in a component. A component cannot reach anything the loader did not send.
- `'use client'` and `'use server'` mean nothing to Netjes; a `'use client'` line left in a component has no effect.
- Every component's code reaches the browser. Keep heavy, server-only work in C#, and load large client-only parts with `dynamic()`.
- A slow value does not hold the page up: return it with `Page.Defer(task)` and the document streams, with `loading.tsx` or a `<Suspense>` fallback until it arrives.

### Server actions are C# endpoints

A server action is a named minimal API endpoint, not a function whose identity the bundler encodes. Its name, such as `notes.add`, is stable across builds and deploys, and everything ASP.NET Core does for an endpoint applies to it: authorization, rate limiting, filters, logging. Every action requires an antiforgery token and a same-origin request, and Netjes checks the DataAnnotations of its input before it runs. The React side calls it through a reference generated from the C# signature, and a `<Form>` that uses it posts as a plain HTML form when JavaScript is off.

### Typed contracts generated from C#

There is no shared TypeScript between server and client to keep in step by hand. `generated/netjes.ts` is written from the C# types, through the same System.Text.Json contracts the app serializes with: zod schemas for page and layout props, shared props and JSON endpoints, typed server action references, the app's routes and the URLs of its endpoints ([ADR 0006](decisions/0006-types-and-zod-from-csharp-contracts.md)). `definePage` parses the props with the schema, so a payload that does not match fails with an `Invalid props` error before the component renders. The app rewrites the file in development; you commit it, and `netjes generate --check` fails CI when it is stale.

### ASP.NET Core owns the request

Routing is ASP.NET Core endpoint routing, built from the files in `app/` ([ADR 0002](decisions/0002-server-owns-routing.md)). The client router never matches URLs itself: it asks the server for every page. That puts the rest of the request in ASP.NET Core too:

- Authentication is ASP.NET Core authentication. `.RequireAuthorization()` on a page loader, a layout loader or `MapPages()` protects pages, and a failed check renders `unauthorized.tsx` or `forbidden.tsx`.
- A layout loader is skipped on navigations within its section, like Next.js's partial rendering, so it sees only the route values of its own URL. An access check on a deeper route value, such as a project id below `/projects`, belongs in the page's loader.
- Middleware, rate limiting, output caching, CORS, health checks and OpenTelemetry are the ASP.NET Core features you already know.
- Configuration is .NET configuration: `appsettings.json`, environment variables, secrets stores. Public values reach the browser at runtime, so one build runs in every environment.

### Rendering runs in Node, next to .NET

The server renders React in a pool of Node processes that the .NET app starts, health-checks and restarts ([ADR 0001](decisions/0001-react-renders-in-node-sidecars.md)). You deploy one ASP.NET Core app with Node.js installed next to it, in a container or on a server. There is no edge or serverless target. In production, while no Node process is ready, documents go out for the browser to render, so pages keep working through a restart ([ADR 0013](decisions/0013-client-rendered-fallback.md)).

### Pages are dynamic until you cache them

Next.js prerenders what it can. Netjes renders every page per request and sends it `no-store`, because pages carry the visitor's shared props and an antiforgery token. Caching is opt-in per page with `.CachePage(...)`, and a cached page must not depend on who is asking ([ADR 0016](decisions/0016-page-cache-rules-under-any-output-caching.md)).

### Smaller differences

- On a client navigation the router waits for the next page's payload, then shows it; it does not show the target's `loading.tsx` first. `loading.tsx` shows while a document or a navigation streams deferred values.
- After a client navigation to another page, the router announces the page in a polite live region, by its title, else its first `h1`, else its path, and starts the focus over at the top of the document, as a page load does; the element a `#fragment` names takes the focus instead ([Focus and announcements](guides/routing.md#focus-and-announcements)). Next.js 16.4 announces the page in an assertive region (`role="alert"`), by its title or else its first `h1`, and leaves the focus where it was: its source defers resetting the focus on navigation, and earlier versions moved it to the new segment's first element when that element took the focus.
- `router.push`, `router.replace` and `router.refresh()` return promises, which resolve once the router has handed the new page to React; Next.js's return nothing. React can still show the old page then, so code that needs the new page runs in an effect, as in Next.js ([`useRouter`](guides/routing.md#userouter)).
- `openGraph`, `twitter`, `alternates` and `icons` in `metadata` merge key by key down the layouts, the page and the C# metadata, where Next.js replaces each of these objects whole at every level that sets it. A page's `openGraph: { title }` keeps a layout's `siteName` and images, and a layout's `twitter.images` still make the card `summary_large_image` on a page that sets `twitter: { title }`. A page replaces a layout's key by setting it, `images: []` for none ([Metadata](guides/metadata.md#titles-and-tags-from-page-files)).
- A `title.default` goes through the nearest `template` of the layouts above it, on a layout and on a page, as in Next.js. Some rules for title templates differ from Next.js ([Metadata](guides/metadata.md#titles)):
  - A layout's `template` also applies to the page in the layout's own folder. With `{ default: 'Site', template: '%s · Site' }` in `app/layout.tsx` and `{ default: 'Docs', template: '%s · Docs · Site' }` in `app/docs/layout.tsx`, the page `app/docs/page.tsx` with `title: 'Intro'` is `Intro · Docs · Site` in Netjes. In Next.js it is `Intro · Site`, because a layout's template applies only to the segments below the layout.
  - A layout whose `title` sets no `template` leaves the template from the layouts above it in place for the titles below it. In Next.js such a layout removes that template for the segments below it.
  - Only the first `%s` in a template is replaced. Next.js replaces every `%s`.
- A URL that spells a page's folders in another case (`/About` for `app/about/page.tsx`) answers 308 with the file's spelling, `/about`, where Next.js, which matches a route in its file's case only, answers 404. A trailing slash is answered as it is by default; `TrailingSlash.Never` sends `/about/` to `/about`, as Next.js's default `trailingSlash: false` does ([One URL per page](guides/routing.md#one-url-per-page)).
- `notFound()`, `redirect()` and the like are not thrown from React components: the loader decides, in C#, before anything renders.
- `error.digest` has no counterpart yet. Where Next.js masks a server error's message, Netjes marks the stand-in with `error.withheld`, and `error.status` holds the status of the server's answer ([error pages](guides/routing.md#error-not-found-forbidden-and-unauthorized-pages)).
- There is no Pages Router. An app with Pages Router files in `pages/` stops at startup and says what to do with each: a page moves to its `app/` name (a `.js` page to a `.jsx` file), `404.tsx` to `app/not-found.tsx`, `500.tsx` and `_error.tsx` to `app/error.tsx`, `_app.tsx` and `_document.tsx` to the root layout, and an API route in `pages/api/` becomes a minimal API endpoint ([Routing](guides/routing.md#what-is-not-there)).

## Moving an app over

1. Create an app with `npx netjes new` and move the components across; the `app/` file names are the same. Playwright specs go in `e2e/`, which the new app's `playwright.config.ts` runs against a development and a production server.
2. Rewrite each server component's data fetching, `getServerSideProps` or `generateMetadata` as a C# loader, returning a record of props.
3. Turn `'use server'` functions into `MapAction` endpoints, and route handlers into minimal APIs, mapped for `GET` and `HEAD` where a handler exports `GET` ([HEAD requests](guides/routing.md#head-requests)).
4. Move `middleware.ts` and the `next.config.js` redirects, rewrites and headers into `Program.cs`.
5. Replace `NEXT_PUBLIC_` variables with `Netjes:PublicConfig`.
6. Run `npm run routes`: it prints the route table as ASP.NET Core sees it, with warnings for loader parameters that miss a route value, for pages that match the same URLs, for endpoints that tie with a page, and for form inputs that refuse a post without a checkbox.
