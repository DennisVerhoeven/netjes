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
- A Content-Security-Policy is off unless the app sets one. A document with a CSP nonce is never stored, so cached pages under a policy need a hash-based one (`ContentSecurityPolicy` without `{nonce}`), and under such a policy a page must not stream.
- Keys include the host and the full URL, case included. In production pin `AllowedHosts` and set the output cache's `SizeLimit`.
- Pages the output cache does not store go out `Cache-Control: no-store, private`, for visitors and signed-in users alike, unless the page sets its own header.
- Draft pages and draft redirects (a loader's `Page.Redirect` in draft mode), the client-rendered fallback, the page for a failed form post, action answers and antiforgery tokens always go out `no-store`, and without the headers a CDN reads ahead of `Cache-Control` (`CDN-Cache-Control`, any other `*-Cache-Control`, `Surrogate-Control`, `Edge-Control`, `X-Accel-Expires`), whatever the page set.

## TanStack Query

Netjes creates one `QueryClient` per server render and per page load. `.WithQuery(key, data)` on a loader result seeds it, so `useQuery` with that key has data on the first render and fetches nothing until the data is stale (default `staleTime` one minute). Data the browser fetched more recently is kept. After an action, `.Invalidate(key)` refetches and `.WithQuery(key, data)` sets the new value. Configure the client with `netjes({ query: { defaultOptions } })` in `vite.config.ts`.

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

`npx netjes export [dir]` builds, runs the app once in Production and writes every page as static HTML (default `out/`). Dynamic pages need `.StaticParams(...)` (`.StaticParams<PostStore>(s => s.All.Select(p => new { slug = p.Slug }))`, arrays for catch-all parameters); pages that need a server take `.ExcludeFromExport()`. The export fails before writing anything for a dynamic page without static params, a page with a page action, or a page that requires authorization. Exported pages navigate with full page loads and carry no antiforgery token.

## Sitemap and robots

`app.MapSitemap(siteUrl)` serves `/sitemap.xml` from the route table (dynamic pages through `.StaticParams`, pages that need authorization or call `.ExcludeFromSitemap()` left out) and `app.MapRobotsTxt(siteUrl, disallow)` serves `/robots.txt`.

Leave `/_netjes/` out of `disallow`: `<Image>` serves every image through `/_netjes/image`, so a crawler that may not fetch it cannot index your images. The other endpoints there take posts or hand out an antiforgery token, which a crawler has no use for. Under a base path, `disallow` paths are written as given, so include the base path in them. See [Metadata](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/metadata.md).

## More

[Caching](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/caching.md), [Rendering and streaming](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/rendering-and-streaming.md) and [Static export](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/static-export.md) in the guides.
