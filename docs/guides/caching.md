# Caching

This guide is for app developers who want pages served from a cache, or need to know what browsers and proxies keep. It covers the `Cache-Control` Netjes sends by default, `CachePage` on ASP.NET Core output caching, the rules that apply to a page under any output caching, revalidation, draft mode, and what to watch for with CDNs and cookies. The client's own cache of server state (TanStack Query) is in [data loading](data-loading.md).

## What browsers keep by default

Page responses, documents and client payloads alike, go out with `Cache-Control: no-store, private`, unless the page set the header itself or the output cache stores the response. That is what Next.js sends for its dynamic pages, and for the same reason: a page carries per-user data (shared props, an antiforgery token), and browsers show a page they kept on Back and Forward without asking the server again. A kept page would show a signed-in user's page on Back after they signed out.

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

`CachePage(duration, params tags)` works on anything that takes endpoint conventions: a page loader, `MapPages()` for every file page, a group, or a minimal API that returns `Page.Render`.

The order in the pipeline matters:

- `UseOutputCache()` after `UseNetjes()`. Before it, the output cache runs before routing, so no endpoint's policy applies, `CachePage` included, and Netjes logs a warning naming the order.
- `UseOutputCache()` after `UseAuthentication()`, so the output cache can see who is signed in. ASP.NET Core's default policy neither serves nor stores a response for a signed-in user or a request with an `Authorization` header.

### What is stored

The output cache stores a page's response when:

- the request is a `GET` or `HEAD` without a signed-in user or an `Authorization` header,
- the status is 200 and the response sets no cookie,
- the document carries no CSP nonce (a stored nonce would be every visitor's; Netjes logs a warning once per page, see [security](security.md#content-security-policy)),
- and the request is not in draft mode, not the page rendered for a failed form post, and not the client-rendered fallback.

A render that is not stored is one visitor's page, and gets the default `no-store, private`. Netjes judges that when the response starts: a refusal the output cache makes later, such as for a body over its `MaximumBodySize`, leaves the response without `Cache-Control`. Stored responses carry no `Cache-Control` of their own.

### How entries are keyed

Each entry is keyed on:

- the scheme, the host, and the path and query string exactly as sent, case included. Loaders get route and query values as sent, so `/s/Ab` and `/s/aB` can be different pages, and each query string is a page of its own (`?tag=a&tag=b` and `?tag=b&tag=a` are two entries).
- whether it is a document or a client payload, and for a payload, which of the page's layouts the client already holds (it then leaves their props out).
- the client build. During a rolling deploy, instances on the old and the new build do not serve each other's entries.

The cache's size limit counts response bodies, not keys, and every host and query string makes a new entry. In production, pin `AllowedHosts` to your host names and size the cache:

```csharp
builder.Services.AddOutputCache(options => options.SizeLimit = 200 * 1024 * 1024); // bytes; ASP.NET Core's default is 100 MB
```

### What a stored page leaves out

A response the output cache may store carries no antiforgery token, since the stored copy would hand one visitor's token to everyone it is served to. Pages under `CachePage` never carry one. Before a server action call or a `<Form>` post, the client fetches a token from `/_netjes/antiforgery`, and again after each action or navigation, since signing in or out changes it. So forms on cached pages need JavaScript: without it the post has no token and is refused.

### Shared props and cookies

A stored entry is every visitor's, shared props included. Shared props and the loaders of cached pages must not depend on cookies or other per-visitor state. Shared props built from the signed-in user are fine, since signed-in users never get a cached page.

As a backstop, a render the output cache will store that reads `Request.Cookies` (draft mode's cookie aside) logs a warning once per page: the render was stored and will be served to every visitor. It is still stored, because refusing would turn caching off behind any middleware that reads a cookie on each request (cookie authentication, sessions, a cookie culture provider). Reads of the raw `Cookie` header are not seen. Read per-visitor values on the client, or leave the page uncached. `UseAuthentication()` placed after `UseOutputCache()` also triggers the warning.

### Under any output caching

The keys and rules above hold however a page is output-cached, not only with `CachePage`:

- `.CacheOutput()` plain, named (`.CacheOutput("pages")`) or built (`.CacheOutput(p => p.Expire(...))`),
- an `[OutputCache]` attribute on the loader,
- conventions on `MapPages()`,
- a base policy of the app (`options.AddBasePolicy(...)`).

The differences: under those, a response the output cache will not store keeps its antiforgery token, while `CachePage` pages never carry one. And an endpoint that is no page but renders one (a minimal API returning `Page.Render` or `Page.Redirect`) gets these keys only from `CachePage`. Under other output caching its pages are not stored, and Netjes logs a warning once per endpoint.

A policy's own keys still add to these: `SetVaryByQuery("page")` cannot narrow the key, since the query string as sent is part of it already. ([ADR 0016](../decisions/0016-page-cache-rules-under-any-output-caching.md) explains why the rules apply under every policy.)

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
- While it is on, output caching is bypassed for that visitor under any policy: nothing is looked up, and nothing that answers a draft request is stored, a redirect a loader returns included. Each draft request has a cache key of its own, so editors previewing a page at once do not wait for each other.
- Draft pages go out `Cache-Control: no-store, private`, without the headers a CDN reads ahead of it ([CDNs and proxies](#cdns-and-proxies)). So does a redirect a page or layout loader returns while draft mode is on, since where a draft leads (an unpublished slug, say) is draft content too.
- In React, `useDraftMode()` returns `{ isEnabled }`, for a "Leave preview" banner.
- The image endpoint is the exception: an image is the same for editors and visitors, so editors share its cache.

## CDNs and proxies

Netjes sends no CDN headers. A stored page goes out without `Cache-Control`, so a CDN in front of the app keeps it only under rules you give the CDN, and `Revalidate` and `RevalidatePath` evict the app's own output cache only.

Page responses carry `Vary: X-Netjes-Data, X-Netjes-Segments`: the same URL answers with a document or a client payload, and a payload depends on the layouts the client holds. A cache that keeps pages has to keep those apart, or pass every request with `X-Netjes-Data` through to the app.

Where Netjes sends `no-store` whatever the page set (draft pages and draft redirects, the client-rendered fallback, the page for a failed form post, action answers and antiforgery tokens), it also removes the headers a CDN or proxy reads ahead of `Cache-Control`, in any casing: `CDN-Cache-Control` and any other header whose name ends in `-Cache-Control` (a CDN's own, such as `Cloudflare-CDN-Cache-Control`), `Surrogate-Control`, `Edge-Control` and `X-Accel-Expires`. A CDN that reads one of them would otherwise keep a draft, or go on serving the fallback, whatever `Cache-Control` says. Netjes does this when it renders the response and again as the response starts, after the `Response.OnStarting` callbacks of loaders, actions and middleware registered after `UseNetjes`. A callback of middleware ahead of `UseNetjes` runs after Netjes's, so what it adds still goes out. On every other response, the headers a page sets go out as they are ([ADR 0019](../decisions/0019-forced-no-store-removes-cdn-cache-headers.md)).

## Images

The `<Image>` endpoint (`/_netjes/image`) is output-cached whenever the app uses output caching, for signed-in visitors too, and sends long-lived `Cache-Control` and an `ETag`. See [images](images.md).
