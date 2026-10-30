# C# API

This page lists the public API of the `Netjes` and `Netjes.Images.SkiaSharp` NuGet packages: every public type and extension method, grouped by what it is for, each with its signature and what it does. It is a reference for app developers; the guides show how the pieces fit together. Options are listed separately, in the [options reference](options.md).

Extension methods live in the namespaces of what they extend, which a web project imports already: `Microsoft.AspNetCore.Builder` (`UseNetjes`, `MapPages`, `MapPageLoader`, ...), `Microsoft.Extensions.DependencyInjection` (`AddNetjes`, ...), `Microsoft.Extensions.Hosting` and `Microsoft.AspNetCore.Http`; the sitemap's are in `Netjes`. The types are in `Netjes`, which the package's build targets import into projects with implicit usings, and in `Netjes.Images`, `Netjes.Images.SkiaSharp`, `Netjes.Rendering` and `Netjes.Routing`.

The extension methods belong to these static classes, which app code never names:

| Class | Namespace | Methods |
|---|---|---|
| `NetjesServiceCollectionExtensions` | `Microsoft.Extensions.DependencyInjection` | `AddNetjes` on `IServiceCollection` and on `IHealthChecksBuilder` |
| `NetjesApplicationBuilderExtensions` | `Microsoft.AspNetCore.Builder` | `UseNetjes` |
| `NetjesEndpointRouteBuilderExtensions` | `Microsoft.AspNetCore.Builder` | `MapPages`, `MapPageLoader`, `MapLayoutLoader`, `MapPageAction`, `MapAction`, `CachePage` |
| `StaticExportEndpointConventionBuilderExtensions` | `Microsoft.AspNetCore.Builder` | `StaticParams`, `ExcludeFromExport` |
| `NetjesLocalizationServiceCollectionExtensions` | `Microsoft.Extensions.DependencyInjection` | `AddNetjesLocalization` |
| `NetjesLocalizationApplicationBuilderExtensions` | `Microsoft.AspNetCore.Builder` | `UseNetjesLocalization`, `MapLocaleRedirect` |
| `NetjesHostExtensions` | `Microsoft.Extensions.Hosting` | `GetNetjesCommand` |
| `NetjesHttpContextExtensions` | `Microsoft.AspNetCore.Http` | `GetCspNonce` |
| `SitemapEndpointRouteBuilderExtensions` | `Netjes` | `MapSitemap`, `MapRobotsTxt`, `ExcludeFromSitemap` |
| `SkiaImagesServiceCollectionExtensions` | `Microsoft.Extensions.DependencyInjection` (package `Netjes.Images.SkiaSharp`) | `AddNetjesSkiaImages` |

## Setting up

### AddNetjes

```csharp
public static IServiceCollection AddNetjes(this IServiceCollection services, Action<NetjesOptions>? configure = null)
```

Registers Netjes: options bound from the `Netjes` configuration section and then `configure`, antiforgery, the sidecar pool, the renderer, type generation, the route warnings, the export command, draft mode, the image endpoint's services and the output-cache rules for pages. Pair it with `UseNetjes()` and `MapPages()`.

### UseNetjes

```csharp
public static IApplicationBuilder UseNetjes(this IApplicationBuilder app)
```

Adds static files (`public/` in development; the client build in production, with `immutable` caching for the hashed files in `/assets` and Vite's `.vite/` folder never served), routing, and the middleware that renders the nearest `error.tsx` for exceptions and empty error responses of pages. It also stamps every client payload with the server's clock (`X-Netjes-Now`). Call it before authentication and before `UseOutputCache()`. A server action that fails without JavaScript renders its form's page by running the pipeline again from here, so middleware registered before `UseNetjes()` does not run for that render. In production it reads the client build's manifest, and throws when it is missing, saying what to run: an app that does not catch it logs that in one line and exits with code 1. It also stops an app with Next.js pages router files in a `pages/` folder, naming the `app/` file to move each one to, and the API routes in `pages/api/` to map as minimal API endpoints ([Routing](../guides/routing.md#what-is-not-there)).

### MapPages

```csharp
public static IEndpointConventionBuilder MapPages(this IEndpointRouteBuilder endpoints)
```

Maps every page file that has no `MapPageLoader`, a lowest-priority fallback that renders the not-found page, the image endpoint (`/_netjes/image`), the antiforgery token endpoint (`/_netjes/antiforgery`), and with `CollectWebVitals` the vitals endpoint (`/_netjes/vitals`). Conventions on the result, such as `RequireAuthorization()` or `CachePage(...)`, apply to all the file pages. It reads the routes as the app starts, so files in `app/` that break the file conventions stop the app there ([Route errors](#route-errors)).

### AddNetjes (health checks)

```csharp
public static IHealthChecksBuilder AddNetjes(this IHealthChecksBuilder builder)
```

Adds a health check named `netjes` for the Node processes: Healthy when all are ready, Degraded while some restart, Unhealthy when none is ready. See [deployment](../guides/deployment.md#health-checks).

### GetNetjesCommand

```csharp
public static string? GetNetjesCommand(this IHost host)
```

The `netjes` CLI command this run of the app is for (`generate`, `routes` or `export`), or `null` for an ordinary run. `generate` and `routes` answer no requests, so startup work they do not need can be skipped: `if (app.GetNetjesCommand() is null) await SeedAsync(app.Services);`. `export` renders every page, so it needs what the pages need.

## Pages and loaders

### MapPageLoader

```csharp
public static RouteHandlerBuilder MapPageLoader(this IEndpointRouteBuilder endpoints, string pattern, Delegate loader)
```

The C# loader of a page, in place of a server component or `getServerSideProps`. `pattern` is the page's URL in file syntax (`/blog/[slug]`) and must match a page file; otherwise it throws, naming the file it expected and the pages there are. The endpoint answers `GET` and `HEAD` at the page's URL, below the prefix of a route group it is mapped in (`app.MapGroup("/{tenant}")`), whose route parameters it binds as it binds the page's own. The delegate binds like a minimal API `GET` handler (route values, the query string, services, `HttpContext`, `ClaimsPrincipal`, `CancellationToken`, no request body) and returns a `PageResult` or `PageResult<T>`; its return type names the props type for the generated TypeScript. A parameter minimal APIs cannot bind, such as a complex type that is not a registered service and has no `[AsParameters]`, throws here, naming the page, the parameter and the fixes, and so does one that reads the request body (`[FromBody]`, `[FromForm]`, `IFormFile`, `IFormFileCollection`, `IFormCollection`), which a navigation does not have, or an `IEnumerable<int>`, which minimal APIs would take from services and not from the query string, as `int[]` is, or a type that adds accepts metadata (`IAcceptsMetadata`) for itself, with which routing would match the page only for requests with that Content-Type and no navigation would reach it ([binding](../guides/data-loading.md#binding)). The result is a `RouteHandlerBuilder`, so `RequireAuthorization()`, `CachePage()`, `CacheOutput()`, filters and metadata work as on any endpoint, except accepts metadata (`.Accepts<T>()`), which would keep navigations from the page in the same way. Conventions of layout loaders above the page apply to it too. See [data loading](../guides/data-loading.md).

### MapLayoutLoader

```csharp
public static LayoutLoaderBuilder MapLayoutLoader(this IEndpointRouteBuilder endpoints, string segment, Delegate loader)
```

The C# loader of a `layout.tsx`: its props reach the layout component next to `children` and `params`. `segment` is the layout's URL (`/dashboard`, `/[team]`) or, when route groups share a URL, its folder (`app/(shop)`); it throws when no layout or several match. The loader binds like a minimal API `GET` handler, from the route values of the layout's own URL, the query string and services, with no inferred body; a parameter it cannot bind, such as a `[FromRoute]` naming a route value outside the layout's URL, a complex type that is not a registered service and does not bind itself, one that reads the request body (`[FromBody]`, `[FromForm]`, `IFormFile`, `IFormFileCollection`, `IFormCollection`), or an `IEnumerable<int>`, which minimal APIs would take from services, throws here, naming the layout ([what a layout loader binds](../guides/data-loading.md#what-a-layout-loader-binds)). It runs before the page renders, outermost layout first, and is skipped on client navigations while its route values and the user stay the same. `NotFound`, `Forbidden` and `Unauthorized` render the boundary above the layout; `Redirect` redirects the page. Conventions on the result, such as `RequireAuthorization()`, apply to every page below the layout. Access checks on route values deeper than the layout's URL belong in the page's loader. Mapping a second loader for one layout throws.

### LayoutLoaderBuilder

```csharp
public sealed class LayoutLoaderBuilder : IEndpointConventionBuilder
{
    public void Add(Action<EndpointBuilder> convention);
    public void Finally(Action<EndpointBuilder> finallyConvention);
}
```

What `MapLayoutLoader` returns. Each convention is applied to the layout loader and to every page endpoint below the layout, so `RequireAuthorization()` on it protects the whole section before any loader runs.

### MapPageAction

```csharp
public static RouteHandlerBuilder MapPageAction(this IEndpointRouteBuilder endpoints, string pattern, Delegate action)
```

A `POST` handler at a page's URL, for `<Form method="post">` on that page, with or without JavaScript. Bind fields with `[FromForm]`; a delegate minimal APIs cannot bind, such as one with two inferred bodies, throws here, naming the page ([page actions](../guides/actions-and-forms.md#page-actions)), and so does a `[FromForm]` input with several public constructors or none, or an abstract or interface type, which form binding never builds. Return `Page.Redirect(...)` after a change, or `Page.Props(...)` (often `.WithStatus(422)`) to render the page again, with errors you put in the props. Before the delegate runs, the DataAnnotations of its inputs, and then `IValidatableObject`, are checked as for `MapAction`, with or without `AddValidation()`; a failure renders the page again from its loader with status 422, and the page reads the errors and the declared fields' values with `usePageActionState()` ([validation in page actions](../guides/actions-and-forms.md#validation-in-page-actions)). An attribute that cannot be read throws here. A `[FromForm]` input is never `null` unless the parameter is declared nullable: a post with none of its fields gets an empty input, checked like any other. It always requires a valid antiforgery token, also when it binds no form fields, and a same-origin request, so `app.UseAntiforgery()` must be in the pipeline. `.DisableAntiforgery()` turns off both, for a form another site posts back. The server's request size limit applies, not `ActionBodySizeLimit`. A post from the client router whose response sets or deletes a cookie is marked `X-Netjes-Cookies: changed`. See [actions and forms](../guides/actions-and-forms.md).

### Page

```csharp
public static class Page
{
    public static PageResult<TProps> Props<TProps>(TProps props);
    public static PageResult<NoProps> Props();
    public static PageResult Render(string pattern, object? props = null);
    public static PageResult NotFound();
    public static PageResult Forbidden();
    public static PageResult Unauthorized();
    public static PageResult Redirect(string location, bool permanent = false);
    public static PageResult LocalRedirect(string location, bool permanent = false);
    public static bool IsLocalUrl(string? url);
    public static Deferred<T> Defer<T>(Task<T> task);
    public static Deferred<T> Defer<T>(Func<IServiceProvider, Task<T>> load);
}
```

Results for page loaders, layout loaders and page actions.

| Member | What it does |
|---|---|
| `Props(props)` | Render the endpoint's page with these props. The result is typed by them, so the loader's inferred return type names the props for the generated TypeScript and for tests (`result.Props`). Only in endpoints mapped with `MapPageLoader` or `MapPageAction`; it throws elsewhere. |
| `Props()` | The same without props (`{}`). |
| `Render(pattern, props)` | Render the page for `pattern` (`/blog/[slug]`) from any endpoint, such as a minimal API. |
| `NotFound()` | Render the nearest `not-found.tsx`, inside the layouts above it, else the root not-found page, with status 404. |
| `Forbidden()` | Render the nearest `forbidden.tsx`, else the error page, with status 403. |
| `Unauthorized()` | Render the nearest `unauthorized.tsx`, else the error page, with status 401. |
| `Redirect(location, permanent)` | Redirect: 307 (308 when `permanent`) for `GET` and `HEAD`, 303 after a `POST`. The client router follows it without a full page load. Accepts relative URLs and `http`/`https` URLs, and throws for any other scheme and for a `%` before the first `:` or `/`. |
| `LocalRedirect(location, permanent)` | Like `Redirect`, but throws unless `location` is a path on this site. Use it for URLs that come from the request, such as a `returnUrl`. |
| `IsLocalUrl(url)` | Whether `url` is a path on this site by ASP.NET Core's rules (`/path`, not `//host` or `/\host`), after dropping what browsers drop. Check a `returnUrl` with it and fall back instead of throwing. |
| `Defer(task)` | A prop that is still being computed: the page renders without waiting, and the task's result streams to it when it completes. Read it with `use()` inside a Suspense boundary. |
| `Defer(load)` | The same, with the work started once the page's payload is written, in a DI scope of its own: for work that needs scoped services such as a `DbContext`. |

See [rendering and streaming](../guides/rendering-and-streaming.md#slow-data-with-pagedefer) for `Defer`.

### PageResult

```csharp
public sealed class PageResult : IResult
{
    public PageResult WithStatus(int statusCode);
    public PageResult WithMetadata(PageMetadata metadata);
    public PageResult WithQuery(IReadOnlyList<object?> queryKey, object? data);
    public Task ExecuteAsync(HttpContext httpContext);
}
```

What the untyped `Page` methods return.

- `WithStatus(statusCode)` renders with another status, such as 422 when a page action renders the form again with errors.
- `WithMetadata(metadata)` sets the title, description, Open Graph and other head tags from C#, merged over the metadata the page and layout files export (layout title templates still apply), like `generateMetadata`. See [metadata](../guides/metadata.md).
- `WithQuery(queryKey, data)` seeds the page's TanStack Query cache: `useQuery` with that key renders `data` on the server and on the first render in the browser, without fetching. It is fetched again once stale (`staleTime`, one minute by default), and data the browser fetched more recently is kept. It throws for an empty key; chain it for several queries.

### PageResult&lt;TProps&gt;

```csharp
public sealed class PageResult<TProps> : IResult
{
    public TProps? Props { get; }
    public PageResult<TProps> WithStatus(int statusCode);
    public PageResult<TProps> WithQuery(IReadOnlyList<object?> queryKey, object? data);
    public PageResult<TProps> WithMetadata(PageMetadata metadata);
    public Task ExecuteAsync(HttpContext httpContext);
    public static implicit operator PageResult<TProps>(PageResult result);
}
```

What `Page.Props(props)` returns. Every untyped result converts to it, so one loader can return the page or `NotFound()`, `Redirect(...)` and the rest, and its inferred return type is still `PageResult<TProps>`. `Props` holds the props when the result renders the page, and is `null` for a redirect, not-found or status result.

### NoProps

```csharp
public sealed record NoProps;
```

The props type of `Page.Props()`, serialized as `{}`.

### Deferred&lt;T&gt;

```csharp
public sealed class Deferred<T>;
```

The type of a `Page.Defer` prop. Declare props with it (`Deferred<IReadOnlyList<Comment>> Comments`); the generated TypeScript types it as a `deferred()` schema. It serializes as a placeholder, and only inside page and layout props.

## Metadata, sitemap and robots

### PageMetadata

```csharp
public sealed record PageMetadata
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string>? Keywords { get; init; }
    public PageMetadataRobots? Robots { get; init; }
    public PageMetadataAlternates? Alternates { get; init; }
    public PageMetadataOpenGraph? OpenGraph { get; init; }
    public PageMetadataTwitter? Twitter { get; init; }
    public string? ThemeColor { get; init; }
    public IReadOnlyDictionary<string, string>? Other { get; init; }
}

public sealed record PageMetadataRobots(bool Index = true, bool Follow = true);
public sealed record PageMetadataAlternates(string? Canonical = null, IReadOnlyDictionary<string, string>? Languages = null);
public sealed record PageMetadataOpenGraph
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Url { get; init; }
    public string? SiteName { get; init; }
    public string? Type { get; init; }
    public string? Locale { get; init; }
    public IReadOnlyList<PageMetadataImage>? Images { get; init; }
}
public sealed record PageMetadataImage(string Url, int? Width = null, int? Height = null, string? Alt = null);   // implicit conversion from string
public sealed record PageMetadataTwitter
{
    public string? Card { get; init; }   // summary, summary_large_image, app or player
    public string? Site { get; init; }
    public string? Creator { get; init; }
    public IReadOnlyList<PageMetadataImage>? Images { get; init; }   // a string converts to an image with only a URL
}
```

Head tags from C#, passed to `WithMetadata`. Properties left `null` leave the files' metadata as it is. `Other` adds `<meta name content>` tags, and `Alternates.Languages` adds a `<link rel="alternate" hreflang>` for each locale. A `PageMetadataImage` is written as `og:image` (or `twitter:image`) with its `Url`, followed by `og:image:width`, `og:image:height` and `og:image:alt` for an Open Graph image, or `twitter:image:alt` for a Twitter one, whichever are set. The Twitter tags that neither `Twitter` nor the files set fill in from Open Graph and the page's title and description once the two are merged, as in Next.js ([Open Graph and Twitter](../guides/metadata.md#open-graph-and-twitter)). The title, description and robots are also what the [client-rendered fallback](../guides/rendering-and-streaming.md#when-no-sidecar-is-ready) puts in its head.

### MapSitemap, MapRobotsTxt, ExcludeFromSitemap

```csharp
public static IEndpointConventionBuilder MapSitemap(this IEndpointRouteBuilder endpoints, Uri siteUrl, TimeSpan? cacheFor = null);
public static IEndpointConventionBuilder MapRobotsTxt(this IEndpointRouteBuilder endpoints, Uri siteUrl, IReadOnlyList<string>? disallow = null);
public static TBuilder ExcludeFromSitemap<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder;
```

In the `Netjes` namespace (`SitemapEndpointRouteBuilderExtensions`), like Next.js's `app/sitemap.ts` and `app/robots.ts`.

- `MapSitemap` serves `/sitemap.xml`: every page without route parameters, and dynamic pages through their `.StaticParams(...)`, as absolute URLs under `siteUrl` and the base path. Pages that require authorization (their own, or the fallback policy's unless they are `.AllowAnonymous()`) or call `.ExcludeFromSitemap()` are left out. The list of pages is built at most once per `cacheFor` (default an hour) and the response is sent with that `max-age`. Base paths are percent-encoded in the URLs. The XML is stored for the first eight base paths requested in each `cacheFor`; others, which a client can cause through `X-Forwarded-Prefix`, are built from the stored list on each request. Under a base path longer than 256 characters as the XML writes it (percent-encoded, with each `&` as `&amp;`), it answers 404 without building anything.
- `MapRobotsTxt` serves `/robots.txt`: `User-agent: *`, a `Disallow` line for each path in `disallow` (or `Allow: /` without any), and a `Sitemap:` line with the sitemap's absolute URL, under `siteUrl` and the base path like `MapSitemap`'s.

Both endpoints allow anonymous access.

## Server actions

### MapAction

```csharp
public static RouteHandlerBuilder MapAction(this IEndpointRouteBuilder endpoints, string name, Delegate action)
```

A server action, the counterpart of a Next.js `'use server'` function: a `POST` endpoint at `/_netjes/actions/{name}`. `name` is letters, digits, `.`, `-` and `_` (`posts.create`); another character, or a name mapped twice, throws. The delegate binds like a minimal API handler (a JSON body, `[FromForm]` fields, services), and one minimal APIs cannot bind throws here, naming the action, as does a `[FromForm]` input with several public constructors or none, or an abstract or interface type, which form binding never builds. It returns a `ServerActionResult` or `ServerActionResult<T>`, which types the generated `serverActions` reference. Before the delegate runs, the DataAnnotations of its inputs, and then `IValidatableObject`, are checked, and failures come back as `ServerAction.Invalid` field errors, with or without `AddValidation()`; an attribute that cannot be read, on an input or on a derived type it lists with `[JsonDerivedType]`, throws here ([validation](../guides/actions-and-forms.md#validation)). A `[FromForm]` input is never `null` unless the parameter is declared nullable: a post with none of its fields gets an empty input, checked like any other. Every call needs a valid antiforgery token and a same-origin request; `.DisableAntiforgery()` turns off the token check only. Request bodies are limited to `ActionBodySizeLimit` (1 MB) unless the action has its own limit ([body limits](../guides/actions-and-forms.md#body-limits)). Without JavaScript a form posts here too. An action that sets or deletes a cookie, such as signing in or out, makes the client router forget the pages it keeps for Back and Forward, in every tab of the app ([signing in and out](../guides/actions-and-forms.md#signing-in-and-out)).

### ServerAction

```csharp
public static class ServerAction
{
    public static ServerActionResult<TResult> Ok<TResult>(TResult result);
    public static ServerActionResult Ok();
    public static ServerActionResult Invalid(IDictionary<string, string[]> errors);
    public static ServerActionResult Invalid(string field, string message);
    public static ServerActionResult Forbidden();
    public static ServerActionResult NotFound();
    public static ServerActionResult Redirect(string location);
}
```

| Member | What it does |
|---|---|
| `Ok(result)` | Success; the client's call resolves to `{ ok: true, result }`. Typed by the result, for the generated reference. Without JavaScript the browser is redirected back to the form's page. |
| `Ok()` | Success with no result. |
| `Invalid(errors)`, `Invalid(field, message)` | Validation failed: 422 with the errors per field, field names written with the app's JSON naming policy. A call from the client resolves to `{ ok: false, errors }`, and `useAction`'s state also keeps what was typed; without JavaScript the form's page is rendered again with them. |
| `Forbidden()` | 403. A client call throws to the nearest error boundary; a post without JavaScript gets the error page. |
| `NotFound()` | 404, handled like `Forbidden()`. |
| `Redirect(location)` | Success; the client navigates to `location`. The same URL rules as `Page.Redirect`. |

### ServerActionResult and ServerActionResult&lt;TResult&gt;

```csharp
public sealed class ServerActionResult : IResult
{
    public ServerActionResult Revalidate(params string[] tags);
    public ServerActionResult RevalidatePath(params string[] paths);
    public ServerActionResult Refresh();
    public ServerActionResult Invalidate(params IReadOnlyList<object?>[] queryKeys);
    public ServerActionResult WithQuery(IReadOnlyList<object?> queryKey, object? data);
    public Task ExecuteAsync(HttpContext http);
}
```

`ServerActionResult<TResult>` has the same methods, returning `ServerActionResult<TResult>`, and converts from `ServerActionResult`, so one action can return `Ok(result)` or `Invalid(...)`.

| Method | What it does |
|---|---|
| `Revalidate(tags)` | Evicts output-cached pages with these tags, then the client loads its page again. |
| `RevalidatePath(paths)` | Evicts the output-cached pages at these paths (any query string, case and trailing slash aside), then the client loads its page again. |
| `Refresh()` | The client loads its current page again (every loader runs) once the action completes. |
| `Invalidate(queryKeys)` | Invalidates these TanStack Query keys, and those they prefix, on the client, so they are fetched again. |
| `WithQuery(queryKey, data)` | Sets a TanStack Query entry on the client, such as the updated record, without a fetch. Throws for an empty key. |

## Caching and drafts

### CachePage

```csharp
public static TBuilder CachePage<TBuilder>(this TBuilder builder, TimeSpan duration, params string[] tags)
    where TBuilder : IEndpointConventionBuilder
```

Output-caches the page for `duration`, tagged with `tags`, on ASP.NET Core output caching (`AddOutputCache()` and `UseOutputCache()`, after `UseNetjes()` and authentication), like incremental static regeneration. Entries are keyed by the page rules: the path and query as sent, document or payload, the layouts the client holds, the build. Pages under it never carry an antiforgery token. It also keys an endpoint that is no page but renders one (`Page.Render`). See [caching](../guides/caching.md).

### IPageCache

```csharp
public interface IPageCache
{
    ValueTask RevalidateTagAsync(string tag, CancellationToken cancellationToken = default);
    ValueTask RevalidatePathAsync(string path, CancellationToken cancellationToken = default);
}
```

Evicts output-cached pages from anywhere (a webhook, a background job), like `revalidateTag` and `revalidatePath` outside actions. `RevalidatePathAsync` evicts the document and the payloads of the path. Without output caching both do nothing. Inject it.

### IDraftMode

```csharp
public interface IDraftMode
{
    bool IsEnabled { get; }
    void Enable();
    void Disable();
}
```

Draft mode, like Next.js's `draftMode()`. Inject it into loaders to read `IsEnabled`, and call `Enable()` and `Disable()` from an endpoint you protect. `Enable()` sets a signed, `HttpOnly` cookie that lasts `DraftModeDuration`. While it is on, output caching is bypassed and nothing that answers the request is stored, and pages and the redirects loaders return go out `no-store, private`. See [caching](../guides/caching.md#draft-mode).

## Static export

```csharp
public static TBuilder StaticParams<TBuilder>(this TBuilder builder, Func<IServiceProvider, IEnumerable<object>> parameters)
    where TBuilder : IEndpointConventionBuilder;
public static RouteHandlerBuilder StaticParams<TService>(this RouteHandlerBuilder builder, Func<TService, IEnumerable<object>> parameters)
    where TService : notnull;
public static TBuilder ExcludeFromExport<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder;
```

- `StaticParams` lists the route values a dynamic page is exported for, like `generateStaticParams`: one object per page (`new { slug = "hello" }`), with arrays for catch-all parameters. The second overload resolves `TService` for you. `MapSitemap` uses the same list.
- `ExcludeFromExport` leaves the page out of `netjes export`.

See [static export](../guides/static-export.md).

## Localization

```csharp
public static IServiceCollection AddNetjesLocalization(this IServiceCollection services, Action<NetjesLocalizationOptions>? configure = null);
public static IApplicationBuilder UseNetjesLocalization(this IApplicationBuilder app);
public static IEndpointConventionBuilder MapLocaleRedirect(this IEndpointRouteBuilder endpoints);
```

- `AddNetjesLocalization` turns on locale routing: request localization driven only by the `[lang]` route value, with parent-culture fallback off. With it, the payload carries the locale, `<html lang>` follows it, and `useLocale()` returns it.
- `UseNetjesLocalization`, after `UseNetjes()`, sets the request culture from the route value. An unsupported locale gets the not-found page (in the default culture) on a page, and a bare 404 on any other endpoint with the parameter; a supported one in another case is replaced by its configured name.
- `MapLocaleRedirect` maps `GET /` to a redirect to the browser's best supported locale by `Accept-Language` (an `nl-BE` browser goes to `/nl`), else the default.

`NetjesLocalizationOptions` has `SupportedCultures` (default `["en"]`, the first is the default) and `RouteParameter` (default `lang`). See [redirects, rewrites and headers](../guides/redirects-rewrites-headers.md#internationalized-routing).

## Security

### GetCspNonce

```csharp
public static string GetCspNonce(this HttpContext http)
```

This request's Content-Security-Policy nonce, created on first use. Netjes puts it on every script and stylesheet it renders, and `useNonce()` returns it, so a policy you write yourself can allow exactly those. A document with a nonce is never stored in the output cache. With `NetjesOptions.ContentSecurityPolicy` containing `{nonce}`, Netjes calls it for you. See [security](../guides/security.md#content-security-policy).

## Options

`NetjesOptions`, `NetjesMode` (`Development`, `Production`), `NetjesImageOptions`, `NetjesSecurityHeaderOptions`, `NetjesLocalizationOptions` and `SkiaImageOptions` are described in the [options reference](options.md).

## Telemetry and headers

### NetjesTelemetry

```csharp
public static class NetjesTelemetry
{
    public const string Name = "Netjes";
}
```

The name of Netjes's `ActivitySource` and `Meter`: `.WithTracing(t => t.AddSource(NetjesTelemetry.Name))`, `.WithMetrics(m => m.AddMeter(NetjesTelemetry.Name))`. The spans and metrics are listed in [deployment](../guides/deployment.md#telemetry).

### NetjesHeaders

```csharp
public static class NetjesHeaders
{
    public const string Data = "X-Netjes-Data";
    public const string ServerTime = "X-Netjes-Now";
    public const string Segments = "X-Netjes-Segments";
    public const string Antiforgery = "X-Netjes-Antiforgery";
    public const string Cookies = "X-Netjes-Cookies";
}
```

The headers between the client router and the server, for tests and proxies.

| Constant | What it is |
|---|---|
| `Data` | Sent by the client router to get the page payload as JSON instead of a document. |
| `ServerTime` | On client payloads: the server's clock in Unix milliseconds, also on output-cached ones. |
| `Segments` | Sent by the client router with the keys of the layouts it holds. |
| `Antiforgery` | `invalid` on the 400 a client's action call gets when its antiforgery token was refused. |
| `Cookies` | `changed` on the response to a client's action call or form post that set or deleted a cookie other than the antiforgery cookie. |

## Images

### IImageProcessor and its types

```csharp
namespace Netjes.Images;

public interface IImageProcessor
{
    IReadOnlyList<string> OutputFormats { get; }
    ValueTask<ProcessedImage> ProcessAsync(ImageSource source, ImageTransform transform, CancellationToken cancellationToken);
}

public sealed record ImageSource(ReadOnlyMemory<byte> Content, string ContentType);
public sealed record ImageTransform(int Width, int Quality, string? Format);
public sealed record ProcessedImage(ReadOnlyMemory<byte> Content, string ContentType);
public sealed class ImageRejectedException(string message) : Exception(message);
public sealed class PassThroughImageProcessor : IImageProcessor;
```

The image endpoint hands each image to the registered `IImageProcessor` (a singleton). Register your own to resize and encode in another way.

- `OutputFormats`: the formats it can encode, preferred first (`image/webp`); the endpoint picks one the browser accepts.
- `ImageSource`: the original bytes, from `public/` or an allowed remote host, and the format they are (`image/png`, `image/jpeg`, `image/gif`, `image/webp` or `image/avif`), whatever the file's extension or the remote `Content-Type` said.
- `ImageTransform`: one of the configured widths (do not upscale past the source), the quality (1 to 100), and an output format from `OutputFormats`, or `null` to keep the source's.
- `ProcessedImage`: the bytes to send and their media type.
- `ImageRejectedException`: throw it to refuse an image (too many pixels, say); the endpoint answers with its message, 400 for a local image and 502 for a remote one.
- `PassThroughImageProcessor`: the default, which serves images unchanged (but cached).

### AddNetjesSkiaImages (Netjes.Images.SkiaSharp)

```csharp
public static IServiceCollection AddNetjesSkiaImages(this IServiceCollection services, Action<SkiaImageOptions>? configure = null)

namespace Netjes.Images.SkiaSharp;
public sealed class SkiaImageProcessor : IImageProcessor
{
    public SkiaImageProcessor(SkiaImageOptions options);
    public SkiaImageProcessor();
}
```

Replaces the image processor with `SkiaImageProcessor`, before or after `AddNetjes()`. It resizes to the requested width (never larger than the source), applies the EXIF orientation, decodes large JPEGs at a reduced size, and encodes WebP when the browser accepts it, else the source's format. It decodes PNG, JPEG and WebP only, and only bytes that start like the declared format; it refuses images whose header declares more than `MaxPixels` and images it cannot decode, and passes GIF (which may be animated) and AVIF through unchanged. See [images](../guides/images.md).

## Rendering seams

### ISsrRenderer

```csharp
namespace Netjes.Rendering;

public interface ISsrRenderer
{
    Task<string> RenderAsync(string payloadJson, CancellationToken cancellationToken);
    Task<Stream> RenderStreamAsync(string payloadJson, SsrStreamOptions options, CancellationToken cancellationToken); // default: buffers RenderAsync
    Task ResolveAsync(string renderId, string id, string? valueJson, string? error, CancellationToken cancellationToken); // default: does nothing
}

public sealed record SsrStreamOptions(bool WaitForAll, string? RenderId = null);
```

What renders a page payload (HTML-safe JSON) to an HTML document; by default the sidecar pool. Register your own to replace server rendering, as tests do to run the app without Node (see [testing](../guides/testing.md#the-app-over-http)): the sidecar then does not start. `RenderStreamAsync` streams the shell first and each Suspense boundary as it resolves, and throws before it returns if the shell fails; `WaitForAll` asks for the complete document (crawlers, export), and `RenderId` is set when the payload has deferred values, which `ResolveAsync` delivers.

### SsrRenderException

```csharp
public sealed class SsrRenderException(string message, string? jsStack, Exception? inner = null) : Exception
{
    public string? JsStack { get; }
}
```

Server rendering failed in the Node sidecar, or the sidecar could not be reached or took too long. `JsStack` holds the JavaScript stack when there is one; error pages show it in development.

## Route errors

```csharp
namespace Netjes.Routing;

public sealed class RouteConflictException(string message) : Exception;
public sealed class InvalidPageFileException(string message) : Exception;
```

Netjes throws these when the files in `app/` break the file conventions. The message names the files and what to change.

- `RouteConflictException`: two pages of one shape, such as `app/[lang]/about/page.tsx` and `app/[slug]/about/page.tsx`, or two files for one convention in one folder, such as `page.tsx` and `page.jsx`. Pages that only overlap, such as `[id]` next to `[...rest]`, get a [route warning](../guides/routing.md#two-pages-that-match-the-same-urls) instead.
- `InvalidPageFileException`: a file name the conventions do not allow, such as a catch-all that is not the last segment, a `global-error` file below the app root, or a `route.ts` file. `UseNetjes` also throws it for page files in a `pages/` folder, which Netjes does not route ([Routing](../guides/routing.md#what-is-not-there)).

`MapPages` reads the routes as the app starts, from `app/` in development and from the client build's manifest in production, so either exception stops the app there, and a test that starts the app can catch it by type. When a file changes while the development server runs, Netjes logs the error instead and keeps the previous routes.

The route table that Netjes builds from `app/` is not part of the API. A tool that needs the routes runs `netjes routes --json` ([the netjes command](cli.md#netjes-routes)).
