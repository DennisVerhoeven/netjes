# Redirects, rewrites and headers

This guide is for app developers looking for what Next.js configures in `next.config.js`: `redirects`, `rewrites`, `headers`, `basePath` and `i18n`. In Netjes these are ASP.NET Core features, configured in `Program.cs` (or `appsettings.json`), next to authentication and the rest of the pipeline.

## Redirects

Next.js's `redirects()` is the URL Rewriting middleware, placed before `UseNetjes()`:

```csharp
using Microsoft.AspNetCore.Rewrite;

app.UseRewriter(new RewriteOptions()
    .AddRedirect("^old-blog/(.*)", "blog/$1", statusCode: StatusCodes.Status308PermanentRedirect)
    .AddRedirectToHttpsPermanent());
app.UseNetjes();
```

For redirects that depend on data (a renamed slug), return `Page.Redirect(...)` from the page's loader instead. It works for document requests and client navigations alike: the client router follows it without a full page load. See [security](security.md#redirects) for the URLs it accepts, and `Page.LocalRedirect` for URLs that come from the request.

## Rewrites

Internal rewrites, where the URL stays and another page answers (Next.js's `rewrites()`), are the rewriter too:

```csharp
app.UseRewriter(new RewriteOptions().AddRewrite("^docs/latest/(.*)", "docs/v2/$1", skipRemainingRules: true));
```

Rewrites to another host (an external `destination` in Next.js) are a reverse proxy, such as [YARP](https://microsoft.github.io/reverse-proxy/) (MIT), mapped next to the pages:

```csharp
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
// ...
app.MapReverseProxy(); // e.g. /api/legacy/{**rest} → https://legacy.example.com
```

## Headers

Netjes already sends `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin` and `X-Frame-Options: SAMEORIGIN` with every document (see [security headers](security.md#security-headers)), leaving out any the app has set itself. An app that sends its own `X-Frame-Options`, as below, or a policy with `frame-ancestors`, decides about framing alone: Netjes then adds neither framing header.

Set headers for all pages with a small middleware, or for some pages with an endpoint filter, where Next.js has `headers()`:

```csharp
app.Use((http, next) =>
{
    http.Response.Headers["X-Frame-Options"] = "DENY";
    http.Response.Headers["Permissions-Policy"] = "camera=(), microphone=()";
    return next(http);
});

app.MapPages().AddEndpointFilter(async (context, next) =>
{
    context.HttpContext.Response.Headers["X-Robots-Tag"] = "noindex";
    return await next(context);
});
```

For a Content-Security-Policy, use `NetjesOptions.ContentSecurityPolicy` rather than a header of your own, so the nonce Netjes puts on its scripts matches the header, or call `HttpContext.GetCspNonce()` when you write the policy yourself. A policy with `{nonce}` keeps pages out of the output cache; a hash-based one lets them be cached, but its pages must not stream. [Security](security.md#content-security-policy) explains both, and [static export](static-export.md#deferred-values-and-suspense) what a static host's policy does to exported pages. NetEscapades.AspNetCore.SecurityHeaders (MIT) covers the other headers declaratively.

## Base path

Next.js's `basePath` is `UsePathBase` in .NET and the `basePath` option of the Vite plugin, set to the same value:

```csharp
app.UsePathBase("/docs"); // before UseNetjes()
```

```ts
netjes({ basePath: '/docs' }) // vite.config.ts: built asset URLs get the prefix
```

Write links, form actions, redirects and `router.push` paths as if the app were at the root; Netjes adds the base. `usePathname()` leaves it out. For a raw URL such as `<link rel="icon">`, prefix it yourself with `useBasePath()`, or build it with `useHref(path)`, as with Next.js. [Base path](routing.md#base-path) in the routing guide has the rules.

## Internationalized routing

Next.js's `i18n` routing is a `[lang]` folder with Netjes's locale routing turned on ([Locales](routing.md#locales) in the routing guide):

```csharp
builder.Services.AddNetjesLocalization(o => o.SupportedCultures = ["en", "nl"]); // the first is the default
builder.Services.AddLocalization();
// ...
app.UseNetjes();
app.UseNetjesLocalization(); // after UseNetjes: it reads the [lang] route value
app.MapLocaleRedirect();     // "/" → "/nl" for a Dutch browser
```

Loaders run with `CultureInfo.CurrentCulture` set, so formatting and `IStringLocalizer<T>` follow the URL.

- Only the URL decides. Pages outside `[lang]` get the default culture, not one from a cookie or `Accept-Language`, because an output-cached copy would otherwise serve the first visitor's language to everyone. If such pages are not cached and should follow the browser, add `AcceptLanguageHeaderRequestCultureProvider` to `RequestLocalizationOptions.RequestCultureProviders` yourself (and vary any caching by `Accept-Language`).
- Netjes turns off parent-culture fallback, so a name matches only a configured culture exactly: list `nl-BE` as well as `nl` if both should match. The `/` redirect from `MapLocaleRedirect` is separate: it sends an `nl-BE` browser to `/nl`.
- An unsupported locale renders the 404 page in the default culture. Any other endpoint whose route has the locale parameter (a minimal API at `/{lang}/api/...`) answers a bare 404 for one too, so give a parameter that is not a locale another name.
- A supported locale in another case (`/EN/about`) reaches loaders and `params.lang` as configured (`en`).
- The route parameter is `lang` unless you set `NetjesLocalizationOptions.RouteParameter`.

The document's `<html lang>` follows the locale, and `useLocale()` returns it in React. Send the page's strings through shared props:

```csharp
builder.Services.AddNetjes(options => options.UseSharedProps(http =>
{
    var strings = http.RequestServices.GetRequiredService<IStringLocalizer<SharedResource>>();
    return ValueTask.FromResult(new SiteStrings(strings.GetAllStrings().ToDictionary(s => s.Name, s => s.Value)));
}));

public sealed record SiteStrings(Dictionary<string, string> Messages);
```
