# Caching, streaming and static export

## Output-cached pages

```csharp
builder.Services.AddOutputCache();
// ...
app.UseNetjes();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseOutputCache();          // after UseNetjes and after UseAuthentication, so signed-in users bypass it

app.MapPageLoader("/blog/[slug]", (string slug, PostStore posts) => ...)
    .CachePage(TimeSpan.FromMinutes(5), "posts");
```

- A stored page is served to every visitor, shared props included. Cache only pages that are the same for everyone: their loaders and shared props must not depend on cookies or other per-visitor state (Netjes warns once per page when a cached render reads `Request.Cookies`). Signed-in users and requests with an `Authorization` header bypass the cache.
- Evict from an action with `ServerAction.Ok().Revalidate("posts")` or `.RevalidatePath("/blog/hello")`, and from anywhere else (a webhook, a background job) with `IPageCache.RevalidateTagAsync(tag)` / `RevalidatePathAsync(path)`.
- Cached pages carry no antiforgery token: forms on them fetch one first, so they need JavaScript.
- A Content-Security-Policy is off unless the app sets one. A document with a CSP nonce is never stored, so cached pages under a policy need a hash-based one (`ContentSecurityPolicy` without `{nonce}`), and under such a policy a page must not stream. Build it from `NetjesContentSecurityPolicy.ScriptHashes`, the hashes of Netjes's inline scripts, never from a pasted hash, which a Netjes upgrade can make stale: `$"script-src 'self' {NetjesContentSecurityPolicy.ScriptHashes}; object-src 'none'; base-uri 'self'"`. Keep `'self'` for the built app's module scripts, and leave out `'strict-dynamic'`, which needs a nonce. In the Development environment the module scripts come from the Vite dev server, so add its origin there, and only there, from a variable:

  ```csharp
  builder.Services.AddNetjes(options =>
  {
      var devServer = builder.Environment.IsDevelopment() ? $" http://localhost:{options.ViteDevPort}" : "";
      options.ContentSecurityPolicy =
          $"script-src 'self'{devServer} {NetjesContentSecurityPolicy.ScriptHashes}; object-src 'none'; base-uri 'self'";
  });
  ```

  When browsers reach the dev server at another address, the one set in `NETJES_VITE_ORIGIN`, list that origin instead. In Development a policy also needs these, or set it outside Development only:

  - With `connect-src`, or `default-src` without `connect-src`: Vite's HMR socket, `ws://` (`wss://` for an https origin) and the dev server's host and port, such as `ws://localhost:{options.ViteDevPort}`, which neither `'self'` nor a nonce covers.
  - With `style-src-elem` or `style-src`, or `default-src` without them: the document's nonce or `'unsafe-inline'`, for the CSS that Development inlines.
  - With `img-src` or `font-src`, or `default-src` without them: the dev server's origin, for the images and fonts Vite serves.

  Neither the nonce-based nor the hash-based policy needs `'unsafe-eval'`: zod parses without compiled code in the browser, from zod 4.4.0 on. Under a hash-based policy, give forms server actions through `useAction` and `<Form action={formAction}>`: any other function action, and any function on `<SubmitButton formAction>`, makes React add an inline script whose hash follows the React version and is not in `ScriptHashes`. In JavaScript, such as a static host's headers for exported pages, the same value is `CSP_SCRIPT_HASHES` from `netjes`. A nonce-based policy that the app writes itself takes its nonce from `HttpContext.GetCspNonce()`, the one Netjes puts on the document. Netjes logs a warning, once per policy, when the policy on a document refuses what the document needs (images and fonts aside).
- Keys include the host and the full URL, case included. In production pin `AllowedHosts` and set the output cache's `SizeLimit`.
- Pages the output cache does not store go out `Cache-Control: no-store, private`, for visitors and signed-in users alike, unless the page sets its own header.
- In the Development environment pages are never output-cached: each request renders the page, nothing is stored, and an output-cached page goes out `no-store, private` unless it sets its own `Cache-Control`, so `UseOutputCache()` can stay in every environment, with no Development guard. Cache slow or billed upstream data in the service that loads it (`IMemoryCache`, `HybridCache`). Try caching on the built app (`npx netjes build && npx netjes start`); a `WebApplicationFactory` test of it runs in the `Testing` environment with `Netjes:Mode=Development`.
- Draft pages and draft redirects (a loader's `Page.Redirect` in draft mode), the client-rendered fallback, the page for a failed form post, action answers and antiforgery tokens always go out `no-store`, and without the headers a CDN reads ahead of `Cache-Control` (`CDN-Cache-Control`, any other `*-Cache-Control`, `Surrogate-Control`, `Edge-Control`, `X-Accel-Expires`), whatever the page set.

## Endpoints that are not pages

JSON APIs, text files and webhooks are not pages, unless `CachePage` is put on them or on their group. The page rules do not apply to their cache entries.

- Netjes adds no `Cache-Control` to their answers, including the 400 that minimal API binding answers, and none under `CachePage` either (the page rules decide entries, not headers): the handler's own goes out as set, and the output cache sends the headers set before the body is written again with every stored answer, with `Age`, and adds none. Netjes's own endpoints: `/_netjes/image` (`public, max-age=` the image `CacheDuration` with an image or a 304, none with an error), `MapSitemap` (`public, max-age=` its `cacheFor`), action answers and `/_netjes/antiforgery` (`no-store`); `MapRobotsTxt` and `MapLocaleRedirect` send none.
- ASP.NET Core's keys apply. The path in any case shares one entry, route values included (`/codes/Ab` and `/codes/aB`): `AddOutputCache(o => o.UseCaseSensitivePaths = true)` gives each spelling its own entry, app-wide, and `.CacheOutput(p => p.With(c => c.HttpContext.Request.Path.Value == "/llms.txt"))` stores that one spelling only. Every query key varies the entry: `SetVaryByQuery([])` keeps one entry whatever the query (there is no call without arguments), `SetVaryByQuery("page")` varies on the keys it lists. Both are safe only when the answer does not depend on the keys they leave out and its body does not repeat the URL.
- Only the answer to a `GET` or `HEAD` without an `Authorization` header or a signed-in user is stored, when it is a 200 that sets no cookie. `RevalidatePath` never evicts these entries: give the policy a tag (`.CacheOutput(p => p.Tag("docs"))`) and use `.Revalidate("docs")` or `IPageCache.RevalidateTagAsync("docs")`.
- They stay output-cached in the Development environment, where only pages are not. Draft mode bypasses them, the image endpoint apart.

## TanStack Query

Netjes creates one `QueryClient` per server render and per page load. `.WithQuery(key, data)` on a loader result seeds it, so `useQuery` with that key has data on the first render and fetches nothing until the data is stale (default `staleTime` one minute). Data the browser fetched more recently is kept. After an action, `.Invalidate(key)` refetches and `.WithQuery(key, data)` sets the new value. A fresh query is not fetched again on a mount or a window focus, whatever `Cache-Control` its endpoint sent; the `fetch` of a refetch can be answered by the browser's HTTP cache, and `staleTime: 0` leaves a query to HTTP caching. Configure the client with `netjes({ query: { defaultOptions } })` in `vite.config.ts`.

## Streaming slow data

```csharp
public sealed record TaskPageProps(TaskDetail Task, Deferred<IReadOnlyList<ActivityItem>> Activity);

app.MapPageLoader("/tasks/[id]", (string id, IDbContextFactory<AppDb> contexts, HttpContext http) =>
    Page.Props(new TaskPageProps(LoadTask(id), Page.Defer(ActivityAsync(contexts, id, http.RequestAborted)))));
```

```tsx
// The generated schema types `activity` as a promise, validated when it arrives (deferred() from netjes/zod).
<Suspense fallback={<p>Loading activity…</p>}>
  <ActivityFeed activity={activity} />
</Suspense>

function ActivityFeed({ activity }: { activity: PromiseLike<ActivityItem[]> }) {
  const items = use(activity);
  return <ol>{items.map((item) => <li key={item.id}>{item.summary}</li>)}</ol>;
}
```

- The shell goes out first, with `loading.tsx` or the Suspense fallback where a value is pending, and each value streams in when its task completes. Client navigations stream the same way.
- A deferred task runs alongside the rest of the request: give it its own `DbContext`, never the request's, and pass `http.RequestAborted`. Either create one from `IDbContextFactory<T>`, as above, or use the overload that runs the work in its own DI scope, started once the page's payload is written: `Page.Defer(services => services.GetRequiredService<AppDb>().ActivityAsync(id, http.RequestAborted))`.
- Without JavaScript, a fallback that is still pending stays. Crawlers (`CrawlerUserAgents`), apps with `StreamDocuments = false` and `netjes export` get the complete document.
- A boundary that is ready when the shell goes out is written in place, so it shows without JavaScript too.

## Static export

`npx netjes export [dir]` builds, runs the app once in Production and writes every page as static HTML (default `out/`). `dir` must be inside the app: `netjes export` refuses any other folder before it builds. Dynamic pages need `.StaticParams(...)` (`.StaticParams<PostStore>(s => s.All.Select(p => new { slug = p.Slug }))`, arrays for catch-all parameters); pages that need a server take `.ExcludeFromExport()`. The export fails before writing anything for a dynamic page without static params, a page with a page action, or a page that requires authorization. Exported pages navigate with full page loads and carry no antiforgery token.

The export requests every URL without a query string, and a static host serves each file for every query string. A page whose loader, or a layout loader above it, reads the query string is still exported, as the page without one, and the export logs a warning in `Netjes.Export.QueryString`. To export a page for each value, make it a route parameter with `.StaticParams(...)` (`/blog/tags/[tag]`, not `/blog?tag=`); to keep the page on a server, exclude it. A loader parameter with no default value and a type that is not nullable (`int page`, `string q`) requires the value: without one the page answers 400 and the export fails, so give it a default value or a nullable type (`int page = 1`, `string? q`) to export the page without one. On an exported page `useSearchParams()` sees no query string either, whatever the address bar shows: read `window.location.search` in an effect there, and parse it with the same schema.

## Sitemap and robots

`app.MapSitemap(siteUrl)` serves `/sitemap.xml` from the route table (dynamic pages through `.StaticParams`, pages that need authorization or call `.ExcludeFromSitemap()` left out) and `app.MapRobotsTxt(siteUrl, disallow)` serves `/robots.txt`.

Leave `/_netjes/` out of `disallow`: `<Image>` serves every image through `/_netjes/image`, so a crawler that may not fetch it cannot index your images. The other endpoints there take posts or hand out an antiforgery token, which a crawler has no use for. Under a base path, `disallow` paths are written as given, so include the base path in them. See [Metadata](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/metadata.md).

## More

[Caching](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/caching.md), [Rendering and streaming](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/rendering-and-streaming.md) and [Static export](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/static-export.md) in the guides.
