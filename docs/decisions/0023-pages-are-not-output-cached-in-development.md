# 0023 – Pages are not output-cached in Development

Status: accepted; extends [ADR 0016](0016-page-cache-rules-under-any-output-caching.md)

## Context

An app sets up output caching and `CachePage` in its `Program.cs`, which runs in every environment. In the Development environment its code is live: Vite serves the page modules from source, the sidecar imports them again after an edit, and hot reload swaps C# loaders. But the one part of a page's cache key that tells builds apart, the client build ([ADR 0016](0016-page-cache-rules-under-any-output-caching.md)), is the constant `development` there, and nothing else in the page rules looks at the environment. A stored page outlived every edit. Its markup, and the styles the sidecar inlines into a development document, no longer matched the modules the browser loaded, and React reported a hydration error that pointed at a component rather than at the cache. A stored page also goes out without `Cache-Control`, so the browser could show it again on Back after an edit.

## Decision

- In the Development environment (`IHostEnvironment.IsDevelopment()`), no page is served from the output cache or stored in it, under any policy. `PageCachePolicy` gives each request it keys, for a page or an endpoint `CachePage` marks, a cache-key value of its own, so no lookup finds a stored entry and no request waits behind another's render, and it refuses storage once the response is known, a loader's redirect for a client navigation included.
- Everything else is as outside Development: the keys are made, a response the cache could store carries no antiforgery token, and the warnings for nonces, unkeyed endpoints and cookies are logged. The cookie warning says that outside Development the render is stored.
- One difference on purpose: such a page goes out `Cache-Control: no-store, private`, unless it set its own header.
- Netjes logs once, at Information, that pages are not output-cached in Development.
- Endpoints that are not pages, such as JSON APIs and images, are output-cached in Development as anywhere, unless `CachePage` marks them: the page rules apply to those ([ADR 0016](0016-page-cache-rules-under-any-output-caching.md)), so they run on each request too.
- The environment decides, not `Netjes:Mode`: a test host that runs development mode in another environment, as `WebApplicationFactory` tests do, stores pages as production does.
- There is no switch to cache pages in Development. Data that is slow or costly to load is cached in the service that loads it (`IMemoryCache`, `HybridCache`), which helps in production too. Next.js does the same: `next dev` renders routes on every request and keeps only fetched data between them.

## Alternatives considered

- **Document it only.** Every app that calls `UseOutputCache()` in every environment keeps the misleading hydration error.
- **Decide by `Netjes:Mode`.** It would turn page caching off in the test hosts that run development mode in the Testing environment, the setup the testing guide shows, so no test could check what an app caches.
- **A new client build version for every edit.** C# hot reload gives no signal to key on.
- **Refuse storage in the renderer, before the page renders.** The response would then carry the visitor's antiforgery token where production leaves it out, and the cookie warning would never be logged in Development.
- **Keep the response without `Cache-Control`.** Back would show a document from before an edit.
- **A `NetjesOptions.CachePagesInDevelopment` switch.** It brings the stale document and its hydration error back for whoever turns it on. It can still be added later without breaking anything.

## Consequences

- In Development, every request for an output-cached page runs its loaders, so a loader that calls a slow or billed service reaches it every time unless the service caches.
- Output-cached pages differ in one header between Development (`no-store, private`) and production (none, when stored). A page that sets its own `Cache-Control` keeps it, and can still come back from before an edit on Back.
- Caching is tried on the built app (`netjes build`, then `netjes start`), or in a test host in another environment. `netjes routes` still lists `CachePage` pages as cached.
- A host that runs development mode with a live Vite dev server outside the Development environment still stores pages, which can then be older than the code. It is rare, and a test host that does so asks for caching.
- Netjes's own test app refuses output caching in the Development environment unless a test asks for it, so a test of what the cache stores cannot pass because nothing was stored.
