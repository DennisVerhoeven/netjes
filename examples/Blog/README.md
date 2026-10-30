# Blog

A small blog built with Netjes: posts with tags and likes, a docs section, a guestbook and a streaming demo. It is the example to read for the parts of Netjes that most sites use: C# loaders, a layout loader, a catch-all route, a form that works without JavaScript, TanStack Query seeded from C#, streaming, images, metadata and a static export. It builds against `src/Netjes` and `packages/netjes` in this repository. [TaskBoard](../TaskBoard) is the larger example, with authentication and a database.

## Run it

From the repository root, once, and again after changing `packages/netjes`:

```bash
npm ci && npm run build
```

Then:

```bash
cd examples/Blog
dotnet run            # http://localhost:5080, with the Vite dev server on 5173
```

`dotnet run` starts the app in Development: the app starts the Vite dev server, so pages update as you edit them. In production mode it serves the build instead:

```bash
npm run build
ASPNETCORE_ENVIRONMENT=Production dotnet run --no-launch-profile   # http://localhost:5000
```

With Docker, from the repository root:

```bash
docker compose up                                                     # development: dotnet watch and Vite, http://localhost:8080
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d # the production image, on port 8080
```

The development container binds its ports to localhost only, and `appsettings.Development.json` limits `AllowedHosts` to `localhost`, `127.0.0.1` and `[::1]`; add a name there to reach the dev app by another host. The production container runs as a non-root user on a read-only filesystem and keeps the Data Protection keys, which sign antiforgery tokens, in a named volume.

## What is where

| Feature | Files |
|---|---|
| The home page: the latest posts, and a hero `<Image>` resized by SkiaSharp | `Features/Posts/PostPages.cs`, `app/page.tsx`, `public/hero.png` |
| The post index: a `?tag=` filter that reaches the C# loader, and a title from C# (`.WithMetadata`) | `Features/Posts/PostPages.cs`, `app/blog/page.tsx` |
| A post: `.CachePage` for 30 seconds, likes seeded into TanStack Query (`.WithQuery`), related posts streamed with `Page.Defer` and read with `deferred()`, and `.StaticParams` for the export. The output cache keeps only the payloads of client navigations here: the Blog's Content-Security-Policy uses nonces, so each document renders on every request, and the app logs a warning that says so ([what is stored](../../docs/guides/caching.md#what-is-stored)) | `Features/Posts/PostPages.cs`, `app/blog/[slug]/page.tsx`, `components/RelatedPosts.tsx` |
| Likes: a JSON API read through TanStack Query, and the `posts.like` server action, whose result goes into the query cache, with `useOptimistic` | `Features/Posts/PostLikesApi.cs`, `components/LikeButton.tsx` |
| The docs: a layout loader for the table of contents, which runs once per visit to the section; a catch-all route; `loading`, `error` and `not-found` files inside the docs layout; a title template of its own | `Features/Docs/DocsPages.cs`, `app/docs/` |
| The guestbook: a page action (`MapPageAction`) with validation, which works without JavaScript, a rate limit, and `.ExcludeFromExport()` | `Features/Guestbook/`, `app/guestbook/page.tsx` |
| Streaming: a page that is interactive before its slow part arrives | `Features/Streaming/StreamingDemo.cs`, `app/streaming/page.tsx` |
| Shared props: the site name and the navigation, typed as `sharedProps` | `Program.cs`, `SiteProps.cs`, `components/SiteHeader.tsx` |
| A nonce-based Content-Security-Policy, web vitals reported to .NET metrics, the sitemap and `robots.txt`, output caching and a health check | `Program.cs`, `app/layout.tsx` |
| A base path (`PathBase=/base`, like Next.js's `basePath`) and forwarded headers from known proxies only | `Program.cs` |
| Not-found and error pages | `app/not-found.tsx`, `app/error.tsx` |

## The guestbook's limits

The guestbook keeps the newest 200 entries in memory. Signing is anonymous, so it is rate limited: 10 signatures a minute per client address, then 429. An IPv6 visitor's whole `/64` counts as one address. Behind a reverse proxy, list the proxy's network in `ForwardedHeaders:KnownNetworks`, so the limit applies to the visitor's address and not the proxy's; with no networks listed, the app ignores `X-Forwarded-For`. `Guestbook:SignsPerMinute` sets another limit; a value that is not a positive whole number is ignored. The end-to-end servers raise it, because every test signs from 127.0.0.1.

## Tests

```bash
npm test                                     # Vitest: components and the guestbook page, with createTestApp
npm run typecheck                            # TypeScript against the generated types
npx netjes generate --check                  # fails when generated/netjes.ts is out of date
dotnet test --project ../../tests/Blog.Tests # the guestbook store's cap and the rate limit
npm run test:e2e                             # Playwright, below
npm run export                               # netjes export, into out/
```

`npm run test:e2e` builds the app and runs Playwright against five servers it starts:

| Project | What it runs | Port |
|---|---|---|
| `development` | `dotnet run` in Development, with the Vite dev server on 5190 | 5090 |
| `production` | `npm run build`, then the app in Production | 5091 |
| `basepath` | Development under the base path `/base`, Vite on 5192 | 5092 |
| `fallback` | Production with a Node process that exits after startup and never comes back, so documents render in the browser | 5093 |
| `export` | `netjes export` of the production build, served from its files | any free port |

The `E2E_DEV_PORT`, `E2E_PROD_PORT`, `E2E_VITE_PORT`, `E2E_BASE_PORT`, `E2E_BASE_VITE_PORT`, `E2E_FALLBACK_PORT` and `E2E_EXPORT_PORT` variables move them. Outside CI, Playwright reuses a server that already listens on a port; `CI=1` always starts fresh ones. The suites need Chromium once: `npx playwright install chromium`.
