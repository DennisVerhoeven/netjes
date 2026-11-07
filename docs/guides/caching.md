# Caching

This guide is for app developers who want pages served from a cache, or need to know what browsers and proxies keep. It covers the `Cache-Control` Netjes sends by default, `CachePage` on ASP.NET Core output caching, the rules that apply to a page under any output caching, what differs for the endpoints that are not pages (JSON APIs, text files), revalidation, draft mode, and what to watch for with CDNs and cookies. The client's own cache of server state (TanStack Query) is in [data loading](data-loading.md).

## What browsers keep by default

Page responses, documents and client payloads alike, go out with `Cache-Control: no-store, private`, unless the page set the header itself or, outside the Development environment, the output cache stores the response. That is what Next.js sends for its dynamic pages, and for the same reason: a page carries per-user data (shared props, an antiforgery token), and browsers show a page they kept on Back and Forward without asking the server again. A kept page would show a signed-in user's page on Back after they signed out.

Back past a full page load therefore fetches the page again, unless the browser's back/forward cache restores it (Chrome restores `no-store` pages while the site's cookies are unchanged). A page that should stay in the browser's cache sets its own `Cache-Control`, in its loader or an endpoint filter, and then can come back on Back after a sign-out.

The response that sets the antiforgery cookie, which is a visitor's first page, goes out with ASP.NET Core's own `Cache-Control: no-cache, no-store` and `Pragma: no-cache` instead, so no browser or proxy keeps it either. Later pages reuse that cookie and get the default.

Some responses always get `no-store` in place of the page's own header: draft pages and the redirects loaders return in draft mode (`no-store, private`), the [client-rendered fallback](rendering-and-streaming.md#when-no-sidecar-is-ready), and the page rendered again for a failed form post, which holds what was typed (`no-store`): a server action's post without JavaScript, and a page action's post with or without it. Server action answers and `/_netjes/antiforgery` go out `no-store` too. All of them also go out without the headers a CDN reads ahead of `Cache-Control` ([CDNs and proxies](#cdns-and-proxies)).

Static files are ASP.NET Core's: in production, the content-hashed files Vite writes under `/assets` are sent with `Cache-Control: public, max-age=31536000, immutable`, and the other files in `public/` with no `Cache-Control`.

## Output-cached pages

`CachePage` caches a page on ASP.NET Core output caching, like incremental static regeneration in Next.js: the page is rendered at most once per `duration` and served from the cache in between.

```csharp
builder.Services.AddOutputCache();
// ...
app.UseNetjes();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseOutputCache(); // after UseNetjes and after authentication

app.MapPageLoader("/blog/[slug]", (string slug, PostStore store) => /* ... */)
    .CachePage(TimeSpan.FromSeconds(30), "posts");
```

`CachePage(duration, params tags)` works on anything that takes endpoint conventions: a page loader, `MapPages()` for every file page, a group, or a minimal API that returns `Page.Render`. Each endpoint it is put on carries the duration and tags as `CachedPageMetadata`, which app code and tests can read ([endpoint metadata](../reference/csharp-api.md#endpoint-metadata)).

The order in the pipeline matters:

- `UseOutputCache()` after `UseNetjes()`. Before it, the output cache runs before routing, so no endpoint's policy applies, `CachePage` included, and Netjes logs a warning naming the order.
- `UseOutputCache()` after `UseAuthentication()`, so the output cache can see who is signed in. ASP.NET Core's default policy neither serves nor stores a response for a signed-in user or a request with an `Authorization` header.

### In the Development environment

In the Development environment no page is output-cached, under `CachePage` or any other policy: every request renders the page, nothing is stored, and an output-cached page goes out `Cache-Control: no-store, private` unless it set its own header. Code changes between any two requests there (Vite serves the sources, the sidecar imports them again, hot reload swaps loaders), and a stored page would go on serving markup and styles from before an edit, which React then fails to hydrate with the new modules. Everything else is as in production: the keys, the antiforgery token left out of a page the cache could store, and the warnings below. The app logs once, at Information, that pages are not output-cached there. Endpoints that are not pages, images included, are cached as anywhere, except those `CachePage` marks, such as the JSON endpoints of a group it is put on: the page rules apply to them, so they run on each request too ([ADR 0023](../decisions/0023-pages-are-not-output-cached-in-development.md)).

So `UseOutputCache()` can stay in every environment. Data that is slow or costly to load, such as a remote API that bills per call, is cached in the service that loads it (`IMemoryCache`, `HybridCache`), which helps in production too. To see what the output cache stores, run the built app (`npx netjes build`, then `npx netjes start`), or test it in a host in another environment with Netjes in development mode ([testing](testing.md#the-app-over-http)): the environment decides, not `Netjes:Mode`.

### What is stored

Outside the Development environment, the output cache stores a page's response when:

- the request is a `GET` or `HEAD` without a signed-in user or an `Authorization` header,
- the status is 200 and the response sets no cookie,
- the document carries no CSP nonce (a stored nonce would be every visitor's; Netjes logs a warning once per page, see [security](security.md#content-security-policy)),
- the request is not in draft mode, not the page rendered for a failed form post, and not the client-rendered fallback,
- and its query string is not one the page's `CacheQueryKeys` leaves out ([below](#query-strings-a-page-does-not-read)).

A render that is not stored is one visitor's page, and gets the default `no-store, private`, as does every output-cached page in the Development environment. Netjes judges that when the response starts: a refusal the output cache makes later, such as for a body over its `MaximumBodySize`, leaves the response without `Cache-Control`. Stored responses carry no `Cache-Control` of their own.

### How entries are keyed

Each entry is keyed on:

- the scheme, the host, and the path and query string exactly as sent, case included. Loaders get route and query values as sent, so `/s/Ab` and `/s/aB` can be different pages, and each query string is a page of its own (`?tag=a&tag=b` and `?tag=b&tag=a` are two entries). A literal segment in another case (`/About` for `app/about/page.tsx`), and under `TrailingSlash.Never` a trailing slash, is redirected before the output cache sees the request, so such a spelling gets no entry of its own when a browser sends it. A request target that browsers do not send, with `.` or `..` segments (`/./About`) or in absolute form (`GET http://example.com/About`), is not redirected: it is answered, and its page can be stored under `/About`, as before ([One URL per page](routing.md#one-url-per-page)).
- whether it is a document or a client payload, and for a payload, which of the page's layouts the client already holds (it then leaves their props out).
- the client build. During a rolling deploy, instances on the old and the new build do not serve each other's entries.

The cache's size limit counts response bodies, not keys, and every host and query string makes a new entry. In production, pin `AllowedHosts` to your host names and size the cache:

```csharp
builder.Services.AddOutputCache(options => options.SizeLimit = 200 * 1024 * 1024); // bytes; ASP.NET Core's default is 100 MB
```

#### Query strings a page does not read

Each query string is an entry of its own, so a link tagged `?utm_source=feed` stores another copy of the page, and so does a made-up `?a=1` or `?&&`. A page can name the query keys it reads with `.CacheQueryKeys(...)`. Requests with anything else in their query string then stay out of the output cache:

```csharp
app.MapPageLoader("/blog", (int? page, PostStore posts) => Page.Props(posts.List(page ?? 1)))
    .CachePage(TimeSpan.FromMinutes(5))
    .CacheQueryKeys("page");

app.MapPageLoader("/blog/[slug]", (string slug, PostStore posts) => Page.Props(posts.Find(slug)))
    .CachePage(TimeSpan.FromMinutes(5))
    .CacheQueryKeys(); // reads no query key, so only the URL without a query string is cached
```

A request is cached when it has no query string, or when every segment of its query string, the parts between `&`, has a name in the list. Names are read as `Request.Query` reads them, decoded (`+` as a space, then percent-escapes), and compared without regard to case. With `.CacheQueryKeys("page")`:

| Request | What happens |
|---|---|
| `/blog`, `/blog?page=2`, `/blog?page=2&page=3` | Cached, each an entry of its own |
| `/blog?PAGE=2`, `/blog?p%61ge=2` | Cached, each an entry of its own, apart from `?page=2`: the key is still the query string as sent |
| `/blog?utm_source=feed`, `/blog?page=2&utm_source=feed` | Not cached: another key |
| `/blog?`, `/blog?&`, `/blog?page=2&`, `/blog?&page=2`, `/blog?page=2&&page=3` | Not cached: an empty segment, which `Request.Query` leaves out but which makes another query string |
| `/blog?=2` | Not cached: a segment without a name |

A request that is not cached:

- runs the loaders and renders the page for itself. No stored entry answers it, the entry for `/blog` included, nothing is stored for it, and it does not wait for another request's render.
- goes out `Cache-Control: no-store, private`. A `Cache-Control` the page sets itself is kept, as on any page the output cache does not store.
- keeps its query string. The payload's URL is the URL as sent, so the address bar still shows `?utm_source=feed` and the page's scripts can read it. Nothing is redirected or removed.
- keeps its antiforgery token under output caching other than `CachePage`, like any response the output cache will not store. Under `CachePage` it has none, as always.
- logs no cookie warning when it reads a cookie, since no other visitor gets that render.

Three things to weigh:

- A page with many visits through tagged links renders once for each of those visits, where it rendered once per tagged URL before. Data that is slow to load is cached in the service that loads it.
- A key in the list still stores an entry for each value and each repetition (`?page=1`, `?page=2`, `?page=1&page=1`), bounded by `SizeLimit`.
- A key the page reads and the list leaves out, as a loader parameter or through `useSearchParams()`, still reaches the page, but those requests are not cached, and nothing warns about it.

It takes effect wherever the page rules apply: on a page under any output caching, conventions on `MapPages()` included, and on an endpoint `CachePage` is put on, such as a minimal API returning `Page.Render`. A JSON endpoint under `CachePage` follows the same rule for its entries, and gets no `Cache-Control` from Netjes. It does not turn caching on: a page that nothing output-caches is unchanged, and in the Development environment no page is stored anyway. On an endpoint the page rules do not apply to, it does nothing and does not throw, since a route group can mix pages and APIs; such an endpoint narrows its key with `SetVaryByQuery` ([Keys and the query string](#keys-and-the-query-string)). Of two calls on one endpoint, the last decides, and on a layout loader it applies to each page below the layout unless the page makes its own call. ([ADR 0047](../decisions/0047-cached-pages-name-the-query-keys-they-read.md) explains why it works this way.)

### What a stored page leaves out

A response the output cache may store carries no antiforgery token, since the stored copy would hand one visitor's token to everyone it is served to. Pages under `CachePage` never carry one. Before a server action call or a `<Form>` post, the client fetches a token from `/_netjes/antiforgery`, and again after each action or navigation, since signing in or out changes it. So forms on cached pages need JavaScript: without it the post has no token and is refused.

### Shared props and cookies

A stored entry is every visitor's, shared props included. Shared props and the loaders of cached pages must not depend on cookies or other per-visitor state. Shared props built from the signed-in user are fine, since signed-in users never get a cached page.

As a backstop, a render the output cache will store that reads `Request.Cookies` (draft mode's cookie aside) logs a warning once per page: the render was stored and will be served to every visitor. It is still stored, because refusing would turn caching off behind any middleware that reads a cookie on each request (cookie authentication, sessions, a cookie culture provider). Reads of the raw `Cookie` header are not seen. Read per-visitor values on the client, or leave the page uncached. `UseAuthentication()` placed after `UseOutputCache()` also triggers the warning. In the Development environment, where nothing is stored, the warning says instead that outside Development the render is stored.

### Under any output caching

The keys and rules above hold however a page is output-cached, not only with `CachePage`:

- `.CacheOutput()` plain, named (`.CacheOutput("pages")`) or built (`.CacheOutput(p => p.Expire(...))`),
- an `[OutputCache]` attribute on the loader,
- conventions on `MapPages()`,
- a base policy of the app (`options.AddBasePolicy(...)`).

The differences: under those, a response the output cache will not store keeps its antiforgery token, while `CachePage` pages never carry one. And an endpoint that is no page but renders one (a minimal API returning `Page.Render` or `Page.Redirect`) gets these keys only from `CachePage`. Under other output caching its pages are not stored, and Netjes logs a warning once per endpoint.

A policy's own keys still add to these: `SetVaryByQuery("page")` cannot narrow the key, since the query string as sent is part of it already. To keep other query strings from adding entries, name the keys the page reads with [`CacheQueryKeys`](#query-strings-a-page-does-not-read), which keeps those requests out of the cache rather than serving them a stored entry. ([ADR 0016](../decisions/0016-page-cache-rules-under-any-output-caching.md) explains why the rules apply under every policy.)

`CachePage` and `.CacheOutput(...)` on one endpoint do not combine. Of the output-cache policies on an endpoint, ASP.NET Core applies the last, so a `.CacheOutput(...)` after `CachePage` replaces the policy `CachePage` set: the later policy decides how long the page is kept and how its entries are tagged, and revalidating one of `CachePage`'s tags no longer evicts the page (`RevalidatePath` still does). The rest of `CachePage` stays: the page carries no antiforgery token, and its `CachedPageMetadata` still holds the duration and tags `CachePage` was given, not those that apply.

## Endpoints that are not pages

The sections above are about pages. This section is about the minimal API endpoints an app maps next to them to return data or files: a JSON API, a text file such as `llms.txt`, a webhook. Netjes adds no `Cache-Control` to their answers, and its page rules do not apply to the entries the output cache keeps for them: ASP.NET Core's own keys and rules decide ([ADR 0016](../decisions/0016-page-cache-rules-under-any-output-caching.md) says why the page rules stop at pages). An endpoint that `CachePage` is put on, directly or through its route group, gets the page rules for its entries, and one that renders a page with `Page.Render` is covered above; this section is about the rest. The differences from a page, each explained below:

| | A page | An endpoint that is not a page |
|---|---|---|
| `Cache-Control` | `no-store, private`, unless the page sets its own or the output cache stores the response | The handler's own; none when the handler sets none |
| The path in an entry's key | As sent | Compared without regard to case: all spellings share one entry |
| The query string in the key | The whole query string, as sent; `CacheQueryKeys` keeps requests with other keys out of the cache | Every key varies the entry, unless the policy narrows it with `SetVaryByQuery` |
| `RevalidatePath` | Evicts the page | Does not evict it: evict by tag |
| The Development environment | Rendered on each request, never stored | Output-cached as anywhere |

### Headers

Netjes adds no `Cache-Control` to the answer of such an endpoint. The header its handler sets goes out as set. When the handler sets none, Netjes adds none, also to the 400 that minimal API binding answers for a value that does not bind (`take=x` for an `int?`). `CachePage` does not change that. A JSON endpoint in a group it is put on gets the page rules for its cache entries, but no `Cache-Control` from Netjes, in the Development environment too: the default `no-store, private` is a page's. Without a `Cache-Control`, a browser or a CDN uses its own defaults, so set one on an answer that may be kept, in the handler or in an endpoint filter:

```csharp
app.MapGet("/api/search", (string q, SearchIndex index, HttpContext http) =>
{
    http.Response.Headers.CacheControl = "public, max-age=300";
    return TypedResults.Ok(index.Search(q));
});
```

Output caching adds no `Cache-Control` either. It stores the response headers that are set before the body is written, and sends them again with every answer it serves, with an `Age` header added. So a stored answer goes out with the `Cache-Control` of the response it was stored from, or with none when that response had none.

For the endpoints Netjes maps itself:

- `/_netjes/image` sends `public, max-age=` the image option `CacheDuration` (7 days) and an `ETag` with an image or a 304 ([images](images.md#the-image-endpoint)). Its error answers, such as the 400 for a width it does not allow, send none.
- `MapSitemap` sends `public, max-age=` its `cacheFor` (an hour by default), and `no-store` on the 404 it answers under a base path that is too long ([metadata](metadata.md#sitemap-and-robots)). `MapRobotsTxt` and `MapLocaleRedirect` ([locales](routing.md#locales)) send none.
- Server action answers and `/_netjes/antiforgery` send `no-store` ([above](#what-browsers-keep-by-default)).

### Keys and the query string

The output cache keys the entry of such an endpoint on the method, the scheme, the host, the path and the query string, by ASP.NET Core's rules, without the build and the other parts of a page's key. A page's entry is keyed on its path and query string as sent. Two things differ for the endpoint.

The path is compared without regard to case, route values included. `/llms.txt` and `/LLMS.TXT` share one entry, and so do `/codes/Ab` and `/codes/aB` for `MapGet("/codes/{code}", ...)`. The handler answers whichever spelling arrives first, and every other spelling is served that answer until the entry expires or is evicted, so a handler that answers two spellings differently, such as one that compares a route value exactly, runs only for the spelling that arrives first. There are two ways to change that:

- A condition on the policy stores one spelling. `.CacheOutput(p => p.With(context => context.HttpContext.Request.Path.Value == "/llms.txt"))` stores the answer for the path `/llms.txt` only. A request for `/LLMS.TXT` is neither served from the cache nor stored in it, so the handler answers each one.
- `AddOutputCache(options => options.UseCaseSensitivePaths = true)` gives each spelling an entry of its own. It applies to every endpoint the output cache handles, not to one.

Every query key varies the entry. `?page=1`, `?page=2` and `?page=1&utm_source=feed` are three entries, and a visitor, a crawler or a link can make up more. The cache's size limit counts response bodies, not keys ([How entries are keyed](#how-entries-are-keyed)), so enough query strings for a large body fill it: a 400 KB text file takes about 100 MB, ASP.NET Core's default limit, once 256 query strings have stored it. While the cache is full, no new response is stored, a page's included, until older entries expire or are evicted. `SetVaryByQuery` narrows the key:

- `p.SetVaryByQuery([])` keeps one entry whatever the query string says. The call has no form without arguments, so an empty array stands for no keys.
- `p.SetVaryByQuery("page")` varies the entry on the keys it lists and ignores the others, so `?page=1` and `?page=1&utm_source=feed` share one.

That is safe for an endpoint whose answer does not depend on the keys it leaves out and whose body does not repeat the URL. A page's payload repeats its URL, which is why a page's key cannot be narrowed this way ([Under any output caching](#under-any-output-caching)); a page names the keys it reads with [`CacheQueryKeys`](#query-strings-a-page-does-not-read) instead. On an endpoint that is no page, `CacheQueryKeys` does nothing unless `CachePage` is put on the endpoint too.

### What is stored, and how to evict it

ASP.NET Core's default policy decides what is stored. It stores the answer to a `GET` or `HEAD` request that has no `Authorization` header and no signed-in user (which is why `UseOutputCache()` goes after `UseAuthentication()`), when the status is 200 and the response sets no cookie. Any other answer reaches the handler again on the next request.

`RevalidatePath` and `IPageCache.RevalidatePathAsync` evict by a tag that the page rules add to the entries of pages, so they never evict the entry of an endpoint that is not a page. Give the policy a tag instead, and evict that: `.Revalidate("docs")` on a server action's result, or `IPageCache.RevalidateTagAsync("docs")` from anywhere else ([Revalidating](#revalidating)).

For example, a text file that is stored once whatever the query string, for ten minutes, and evicted when the docs change:

```csharp
app.MapGet("/llms.txt", (DocsStore docs) => Results.Text(docs.AsText(), "text/plain; charset=utf-8"))
    .CacheOutput(p => p.SetVaryByQuery([]).Expire(TimeSpan.FromMinutes(10)).Tag("docs"));
```

### In the Development environment and in draft mode

In the Development environment these endpoints are output-cached as anywhere: only pages, and the endpoints `CachePage` marks, render on each request there ([In the Development environment](#in-the-development-environment)). A change to what a handler returns shows once its entry has expired or been evicted, or the app has restarted.

Draft mode bypasses them as it bypasses pages: while the draft cookie is on, nothing is served from the cache to that visitor, and nothing that answers the request is stored. The image endpoint is the one exception ([Draft mode](#draft-mode)).

## Revalidating

A server action evicts cached pages with `.Revalidate(tags)` or `.RevalidatePath(paths)` on its result. The client then loads the page on screen again:

```csharp
using Microsoft.AspNetCore.Mvc; // [FromForm]

app.MapAction("posts.publish", async ([FromForm] PublishPost input, PostStore posts) =>
{
    await posts.PublishAsync(input.Slug);
    return ServerAction.Ok().Revalidate("posts").RevalidatePath($"/blog/{input.Slug}");
});
```

- `Revalidate(tags)` evicts every entry tagged with one of these tags: the tags given to `CachePage(duration, tags)`, or by any other output-cache policy.
- `RevalidatePath(paths)` evicts the entries of each path, documents and payloads, with any query string. The path is matched without regard to case or a trailing slash, and without the base path (`/blog/hello`, not `/docs/blog/hello` under `UsePathBase("/docs")`).

Outside actions (a webhook from your CMS, a background job), inject `IPageCache`:

```csharp
app.MapPost("/webhooks/cms", async (CmsEvent change, IPageCache cache) =>
{
    await cache.RevalidatePathAsync($"/blog/{change.Slug}");
    await cache.RevalidateTagAsync("posts");
    return Results.NoContent();
}).RequireAuthorization("cms");
```

Without output caching both do nothing. Eviction is the app's own: with several instances, it reaches the others only through a store they share (see [deployment](deployment.md#several-instances)), and it never reaches a CDN.

## Draft mode

Draft mode lets an editor see unpublished content on pages that are otherwise cached, like Next.js's `draftMode()`. Turn it on from an endpoint you protect, and read it in loaders with `IDraftMode`:

```csharp
app.MapGet("/preview/on", (IDraftMode draft) =>
    {
        draft.Enable();
        return Results.LocalRedirect("/");
    })
    .RequireAuthorization("editors");

app.MapGet("/preview/off", (IDraftMode draft) =>
{
    draft.Disable();
    return Results.LocalRedirect("/");
});

app.MapPageLoader("/blog/[slug]", (string slug, IDraftMode draft, PostStore posts) =>
        posts.Find(slug, includeDrafts: draft.IsEnabled) is { } post ? Page.Props(new PostProps(post)) : Page.NotFound())
    .CachePage(TimeSpan.FromMinutes(5));
```

- `Enable()` sets a cookie signed with ASP.NET Core Data Protection that lasts `NetjesOptions.DraftModeDuration` (one hour). It is `HttpOnly`, and outside Development `SameSite=None; Secure`, so a CMS on another site can show the preview in a frame (allow that with `SecurityHeaders.DraftFrameAncestors`, see [security](security.md#security-headers)).
- While it is on, output caching is bypassed for that visitor under any policy, for [endpoints that are not pages](#endpoints-that-are-not-pages) as well: nothing is looked up, and nothing that answers a draft request is stored, a redirect a loader returns included. Each draft request has a cache key of its own, so editors previewing a page at once do not wait for each other.
- Draft pages go out `Cache-Control: no-store, private`, without the headers a CDN reads ahead of it ([CDNs and proxies](#cdns-and-proxies)). So does a redirect a page or layout loader returns while draft mode is on, since where a draft leads (an unpublished slug, say) is draft content too.
- In React, `useDraftMode()` returns `{ isEnabled }`, for a "Leave preview" banner.
- The image endpoint is the exception: an image is the same for editors and visitors, so editors share its cache.

## CDNs and proxies

Netjes sends no CDN headers. A stored page goes out without `Cache-Control`, so a CDN in front of the app keeps it only under rules you give the CDN, and `Revalidate` and `RevalidatePath` evict the app's own output cache only.

Page responses carry `Vary: X-Netjes-Data, X-Netjes-Segments`: the same URL answers with a document or a client payload, and a payload depends on the layouts the client holds. A cache that keeps pages has to keep those apart, or pass every request with `X-Netjes-Data` through to the app.

Where Netjes sends `no-store` whatever the page set (draft pages and draft redirects, the client-rendered fallback, the page for a failed form post, action answers and antiforgery tokens), it also removes the headers a CDN or proxy reads ahead of `Cache-Control`, in any casing: `CDN-Cache-Control` and any other header whose name ends in `-Cache-Control` (a CDN's own, such as `Cloudflare-CDN-Cache-Control`), `Surrogate-Control`, `Edge-Control` and `X-Accel-Expires`. A CDN that reads one of them would otherwise keep a draft, or go on serving the fallback, whatever `Cache-Control` says. Netjes does this when it renders the response and again as the response starts, after the `Response.OnStarting` callbacks of loaders, actions and middleware registered after `UseNetjes`. A callback of middleware ahead of `UseNetjes` runs after Netjes's, so what it adds still goes out. On every other response, the headers a page sets go out as they are ([ADR 0019](../decisions/0019-forced-no-store-removes-cdn-cache-headers.md)).

## Images

The `<Image>` endpoint (`/_netjes/image`) is output-cached whenever the app uses output caching, for signed-in visitors too, and sends long-lived `Cache-Control` and an `ETag`. See [images](images.md).
