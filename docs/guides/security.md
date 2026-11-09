# Security

This guide is for app developers putting a Netjes app in front of real users. It lists what Netjes does by default and what is left to you: the security headers, Content-Security-Policy, the antiforgery and origin checks on actions, request body limits, redirects, the image endpoint, the Node sidecar, the layout loaders around 404 and error pages, the secret that signs layout keys, host names and HTTPS, and the development server. To report a vulnerability, follow [SECURITY.md](../../SECURITY.md).

## Defaults at a glance

| What | Default | Change it with |
|---|---|---|
| `X-Content-Type-Options` on every response through `UseNetjes()`; `Referrer-Policy` and framing headers on documents | On | `Netjes:SecurityHeaders` |
| Content-Security-Policy | Off | `NetjesOptions.ContentSecurityPolicy` |
| Antiforgery token and same-origin check on server actions and page actions | On | `.DisableAntiforgery()` on a page action; `AllowedActionOrigins` |
| Server action body limit | 1 MB | `ActionBodySizeLimit`, `[RequestSizeLimit]` |
| Fields a failed form hands back | Only declared ones, never private ones | `PrivateFormFields` |
| `Cache-Control` on pages | `no-store, private` | Your own header; see [caching](caching.md) |
| Error details in error pages | Development only | `IncludeErrorDetails` |
| Remote images | None allowed | `Netjes:Images:RemoteHosts` |
| Layout key secret | Random per process | `SegmentKeySecret` |
| HSTS, HTTPS redirection, `AllowedHosts` | ASP.NET Core's; the template turns on HSTS and HTTPS redirection outside Development | ASP.NET Core |

## Security headers

Every HTML document Netjes renders (pages, not-found and error pages, the plain fallback error page) carries these, unless the app has set that header itself:

| Header | Value |
|---|---|
| `X-Content-Type-Options` | `nosniff` |
| `Referrer-Policy` | `strict-origin-when-cross-origin` |
| `X-Frame-Options` | `SAMEORIGIN`, or what the policy's `frame-ancestors` implies (below) |
| `Content-Security-Policy` | `frame-ancestors 'self'` added to `NetjesOptions.ContentSecurityPolicy` when you set one without `frame-ancestors`, unless the app sent `X-Frame-Options` |

Every other response that passes through `UseNetjes()` gets `X-Content-Type-Options: nosniff` too: client payloads (JSON), static files from `public/` and the client build, `robots.txt`, `sitemap.xml`, `/_netjes/antiforgery`, the app's own endpoints, and answers from the output cache. Under a policy such as `script-src 'self'`, any response from the app's origin can be loaded as a script or a stylesheet, and with `nosniff` the browser refuses to run a script whose `Content-Type` is missing or not a JavaScript type, or to apply a stylesheet whose `Content-Type` is missing or not `text/css`. Nor does it take a response with no `Content-Type` for HTML: a page that an endpoint writes itself with `Response.WriteAsync` shows as plain text, and none of its scripts run, unless the endpoint sets `Response.ContentType = "text/html; charset=utf-8"` first. Netjes adds it as the response starts, only when the response does not have it by then, so a value the app sets is kept and an answer from the output cache carries it once. A response that middleware registered before `UseNetjes()` writes itself, without passing the request on, does not get it. These responses get none of the other headers in the table. The image endpoint sends `nosniff` and a sandboxing Content-Security-Policy of its own, whatever `Enabled` says (see [the image endpoint](images.md#the-image-endpoint)).

An app that sends `X-Frame-Options` itself decides about framing alone: Netjes adds neither framing header, so nothing contradicts it.

An app with `frame-ancestors` in a Content-Security-Policy that goes out (its own header, or else `NetjesOptions.ContentSecurityPolicy`) gets the `X-Frame-Options` that the directive implies, for browsers that know only that header, and no `frame-ancestors` from Netjes:

| `frame-ancestors` | `X-Frame-Options` |
|---|---|
| `'none'`, an empty list, or other sites only, such as `https://partner.example`, or scheme sources no page of the app has, such as `data:` or `blob:` | `DENY` |
| `'self'`, `*`, `http:` or `https:`, with or without other sites | `SAMEORIGIN` |

Every policy counts, whether it comes in a header of its own or joined to others by commas, and the strictest decides: when one of them implies `DENY`, the document gets `DENY`. A `Content-Security-Policy-Report-Only` header does not count. Directive names and keywords such as `'self'` are compared ignoring case. Browsers obey `frame-ancestors` over `X-Frame-Options`, which cannot name another site, so to let a partner frame your pages, send `frame-ancestors 'self' https://partner.example`: browsers that support `frame-ancestors` let the partner frame them, and a browser that knows only `X-Frame-Options` keeps them to your own site.

Netjes sets `X-Frame-Options` on a document before the visitor's antiforgery cookie is stored. ASP.NET Core antiforgery adds `X-Frame-Options: SAMEORIGIN` with each new token cookie, but only to a response that has no `X-Frame-Options` yet, so on documents it adds none: a visitor's first page, a returning visitor's, and a page from the output cache all get the same header. Four limits remain:

- A host source that names your own site counts as another site: `frame-ancestors https://www.example.com` on `https://www.example.com` gets `DENY`. Netjes does not compare sources with the request's host, which behind a proxy is not always the one the browser sees. Write `'self'` for your own site.
- Code that stores antiforgery tokens before the page renders, such as a loader or a middleware that calls `IAntiforgery.GetAndStoreTokens`, has antiforgery add its `SAMEORIGIN` to a visitor's first page before Netjes decides, and Netjes keeps it as the app's own. Set `AntiforgeryOptions.SuppressXFrameOptionsHeader = true` to have Netjes follow the policy there too.
- Netjes decides when the page renders, so it does not see a policy that a middleware writes later, in a `Response.OnStarting` callback. Set such a policy before the page renders, or send `X-Frame-Options` yourself.
- A page whose loader or render throws is answered with its error page on a cleared response, so the headers set before the exception are gone, your own `Content-Security-Policy` and `X-Frame-Options` included. That error page gets `SAMEORIGIN`, or the `X-Frame-Options` that the `frame-ancestors` of `NetjesOptions.ContentSecurityPolicy` implies, with the option's policy. To have your policy on those pages too, set it in `NetjesOptions.ContentSecurityPolicy`.

On responses that are not documents, and on every response with `Enabled = false`, antiforgery still adds its `X-Frame-Options: SAMEORIGIN` with each new token cookie; `AntiforgeryOptions.SuppressXFrameOptionsHeader` turns it off.

`NetjesOptions.SecurityHeaders` (`Netjes:SecurityHeaders`) has two settings:

- `Enabled = false` turns all of them off, `nosniff` on the other responses included; the image endpoint keeps its own. To preview the app in an editor's frame, such as VS Code's Simple Browser, set it in `appsettings.Development.json`, and turn off the header that antiforgery adds in code, since ASP.NET Core does not bind `AntiforgeryOptions` from configuration:

  ```json
  { "Netjes": { "SecurityHeaders": { "Enabled": false } } }
  ```

  ```csharp
  if (builder.Environment.IsDevelopment())
  {
      builder.Services.AddAntiforgery(o => o.SuppressXFrameOptionsHeader = true);
  }
  ```

- `DraftFrameAncestors = ["https://cms.example"]` lets those sites frame pages in [draft mode](caching.md#draft-mode), for a CMS that shows the preview in a frame. Draft documents then get `frame-ancestors 'self' https://cms.example` (in Netjes's own policy, or a header with only that directive) and keep `X-Frame-Options: SAMEORIGIN`, which still holds if a middleware rewrites the policy without `frame-ancestors`. A browser without `frame-ancestors` support cannot show the preview. Published pages stay same-origin. It is ignored when the app sets its own `X-Frame-Options` or `Content-Security-Policy`, or has `frame-ancestors` in `ContentSecurityPolicy`; put the sites there instead. Each entry must be one CSP source, such as `https://cms.example` or `https://*.cms.example`; anything else stops the app at startup.

To send other headers, or other values on some pages, see [redirects, rewrites and headers](redirects-rewrites-headers.md#headers).

## Content-Security-Policy

Netjes sends no Content-Security-Policy until you set `NetjesOptions.ContentSecurityPolicy`. It then sets the header on every document, unless the app sent one itself, with `frame-ancestors 'self'` added when the policy has no `frame-ancestors`. There are two kinds of policy, and the choice depends on whether your pages are output-cached.

### Nonce-based

Put `{nonce}` where the per-request nonce goes:

```csharp
builder.Services.AddNetjes(options =>
    options.ContentSecurityPolicy = "script-src 'nonce-{nonce}' 'strict-dynamic'; object-src 'none'; base-uri 'self'");
```

Netjes makes a nonce for each document and puts it on everything it renders: the module scripts and preloads, the stylesheets, the payload, the scripts that stream deferred values and reveal Suspense boundaries, and Vite's `csp-nonce` meta (for the styles Vite injects in development). `useNonce()` returns it for inline scripts or styles of your own, and `<Script>` uses it. A `beforeInteractive` `<Script>` with a `src` is written into the document as an entry that the client entry turns into a script, with the nonce, before the page hydrates ([code that runs before the first paint](metadata.md#code-that-runs-before-the-first-paint)). The client entry takes only entries that carry the document's nonce, so markup that reaches the page some other way, such as HTML from a user that a sanitizer let through, cannot make it run a script.

A document with a nonce is never stored in the output cache: a stored copy would give every visitor the same, readable nonce. Netjes renders such a page each time and logs a warning once per output-cached page. For cached pages, use a hash-based policy.

If you write the policy yourself, for example with a headers library, call `HttpContext.GetCspNonce()` for the request's nonce, before the page renders, and Netjes puts the same one on what it renders. A nonce the library makes itself is on none of it, so browsers refuse what the rest of the policy does not allow, and Netjes warns about it ([what Netjes checks](#what-netjes-checks)). The output cache rule applies to that nonce as well.

### Hash-based

A policy without `{nonce}` makes no nonce, so pages under it can be output-cached. It must allow, by their hashes, the inline scripts that Netjes writes with the same text into every document. `NetjesContentSecurityPolicy.ScriptHashes` lists those hashes:

```csharp
options.ContentSecurityPolicy =
    $"script-src 'self' {NetjesContentSecurityPolicy.ScriptHashes}; object-src 'none'; base-uri 'self'";
```

`ScriptHashes` holds a `'sha256-…'` source for each such script, in its quotes and separated by spaces. In this version there is one, the bootstrap in the head that collects streamed values, so the value is `'sha256-FSjVV+FzmXP8KKrLKLdvst/5N/bEWSkde01XzpGKRgs='`. A Netjes version that changes the bootstrap, or adds a script like it, changes the value. So build the policy from `ScriptHashes` rather than pasting the hash: a pasted hash would block the script after such an upgrade. A policy written in JavaScript, such as a static host's headers for [exported pages](static-export.md), gets the same value from the npm package: `import { CSP_SCRIPT_HASHES } from 'netjes'`.

Keep `'self'`: a hash allows only the inline script it was computed from, and the built app's module scripts are files the app serves, which `'self'` allows. Leave out `'strict-dynamic'`, which needs a nonce here: next to it, browsers ignore `'self'`, and the module scripts carry no nonce under this policy and no `integrity` attribute for a hash to match, so browsers refuse them and the page does not hydrate. Inline `<Script>` code of your own needs its hash listed too: for `afterInteractive` and `lazyOnload`, the hash of the code as you passed it; for `beforeInteractive`, the hash of the code as the document carries it, which differs where the code holds `<script` or `</script` ([code that runs before the first paint](metadata.md#code-that-runs-before-the-first-paint)). An external `<Script>` from another origin needs that origin in `script-src`, as any other script from there does. Without a nonce, the client entry turns every entry of an external `beforeInteractive` script in the document into a script, one in HTML from a user that a sanitizer let through included: the hosts the policy allows bound what such markup can load, so list only those the app needs, and let no `<script>` element through a sanitizer.

In the Development environment the module scripts come from the Vite dev server, on another origin, so a policy without a nonce lists that origin as well; [in development](#in-development) shows how.

A form action that is a function makes React add an inline script of its own, which replays a submission made before the page hydrates. A server action from `useAction` in `<Form action>` adds none, since Netjes renders that form with the action's URL. Any other function does, in `<Form action>`, in `<SubmitButton formAction>` (server actions included) or on a plain `<form>` or `<button>`. The script's text follows the React version, so `ScriptHashes` leaves it out. List its hash for the React version you run (Chromium names the hash in the console when it blocks the script), or keep such actions off pages under the policy. While the script is blocked, a submission made before the page hydrates is lost; after hydration, React runs the action without it.

No hash covers what streams after the shell: each script that delivers a `Page.Defer` value carries that value, and React's scripts that reveal Suspense boundaries name the boundaries of that render. A hash-based policy blocks both, so a page served under one must not stream: no `Page.Defer`, and no Suspense boundary still waiting when the shell goes out (lazy code under `loading.tsx` included). Pages from `netjes export` are the exception, since their scripts are fixed text; see [static export](static-export.md#deferred-values-and-suspense).

The pages of a mounted [page package](page-packages.md) render under the app's policy, and add no inline script of their own. A package whose pages stream says so with `PagePackage.RequiresNonce`, and Netjes logs the route warning `package-nonce` when such a package is mounted under a `NetjesOptions.ContentSecurityPolicy` that refuses inline scripts without a `{nonce}` ([Route warnings](routing.md#a-page-package-whose-pages-stream-under-a-policy-without-a-nonce)).

### zod in the browser

Neither kind of policy needs `'unsafe-eval'` for zod. In the browser, the client entry turns zod's compiled parsers off (zod's `jitless` setting) before any of the app's modules run, in development, in production and in exported pages ([ADR 0036](../decisions/0036-zod-runs-without-compiled-parsers-in-the-browser.md)). Without that, zod would compile each object schema's parser into a function made from a string, after testing once whether it may. A policy refuses the test when its `script-src` leaves out `'unsafe-eval'`, as both policies above do, or when it has no `script-src` and its `default-src` leaves it out; a policy with neither directive lets zod compile. Although zod catches the error and the page works, the browser reports the refusal as a violation of `script-src` (blocked URI `eval`) on every document: it fires a `securitypolicyviolation` event, and sends a report to the policy's `report-uri` or `report-to` endpoint when it has one. Chromium logs nothing to the console for it when the policy has a `script-src`. When the policy leaves scripts to `default-src`, Chromium logs one error, which reads in full "Note that 'script-src' was not explicitly set, so 'default-src' is used as a fallback."

This holds in every browser, with or without a policy, for every zod object schema made in the page: the generated props schemas that `definePage` and `defineLayout` parse with, `api[...].response.parse(...)`, and the app's own search-param and form schemas. A Web Worker has a global object of its own, which the setting does not reach: a worker of the app's own that parses with zod calls `z.config({ jitless: true })` before it makes a schema. Netjes has no setting to keep the compiled parsers. An app's own call of zod's `z.compile(schema)` is left alone: it makes a function from a string whatever the setting says, so a policy that refuses zod's test reports it.

The cost shows where one schema parses many objects: a list of 1,000 small objects takes about 0.5 ms to parse, against about 0.09 ms with compiled parsers. A new schema's first parse of one object, such as a page's props, is faster, since nothing is compiled.

It takes zod 4.4.0 or newer, which reads the setting however many copies of zod the page loads. zod 4.0 to 4.3 do not read it, and still test once per document: upgrade zod to 4.4.0 or newer (an app made with `netjes new` asks for `^4.6.7`). Server rendering in the Node sidecar, and the app's Vitest tests, do not run the client entry, so they keep the compiled parsers.

### What Netjes checks

Netjes sends the policy as written, in every environment, and compares it with what each document needs. When it refuses something, `Netjes.Rendering.PageRenderer` logs a warning that names the directive and what to change, once per policy (a nonce that changes from document to document does not make it another policy). Every policy in the `Content-Security-Policy` header that goes out counts, your own or `NetjesOptions.ContentSecurityPolicy`, several joined by commas included. A report-only policy does not count, and nor does a policy that a middleware writes in a `Response.OnStarting` callback, after the page renders. The warnings are about:

- the scripts, in the list that script elements follow: `script-src-elem`, else `script-src`, else `default-src`. A list that holds the document's nonce allows them all, since Netjes puts that nonce on every script it writes. Netjes warns when any other list refuses the bootstrap (it holds neither every hash in `ScriptHashes` nor `'unsafe-inline'` without a hash, a nonce or `'strict-dynamic'` beside it), has `'strict-dynamic'`, which leaves the module scripts nothing to be allowed by, or refuses the module scripts (it holds none of `'self'`, `*`, `http:`, `https:` or a host source). Netjes does not judge which host a host source names: behind a proxy, the request's host is not always the one the browser sees. So a list of other hosts only gets no warning, and nor does `self` without its quotes, which browsers read as a host named `self`. When the list is `default-src`, a warning says to add the missing source there; a `script-src` added instead takes the place of `default-src` for every script, so it needs `'self'` as well;
- a nonce that is not the document's, such as one your own code makes without `GetCspNonce()`. It is on none of what Netjes renders, and next to it `'unsafe-inline'` does not count, so when the rest of its list refuses one of the scripts, or in Development the inlined styles, Netjes warns once, about the nonce;
- in Development, the Vite dev server's scripts, its socket and the inlined styles ([in development](#in-development)).

A policy that allows all of it, but blocks something else, such as a script that streams a deferred value, gets no warning. Netjes compares no more than about 100 different policies, so an app that writes another one for each request does not make it keep them all. The static export (`netjes export`) is not checked: its files go out under the static host's policy.

### In development

In the Development environment the browser loads the page's module scripts from the Vite dev server, on another origin than the app's, and Vite's client in the page opens a WebSocket to it, which brings your edits to the page (hot module replacement). Netjes keeps the policy as written there too, so a policy also has to allow these:

- **The dev server's scripts.** The document's nonce in `script-src` covers them, from `{nonce}` or `GetCspNonce()`: Netjes puts it on `@vite/client` and on the client entry, and the modules they load inherit it. A list without it lists the dev server's origin, or browsers refuse the scripts and the page does not hydrate. The origin is `http://localhost:5173`, on another port when `ViteDevPort` (`Netjes:ViteDevPort`) moves it, or the one in `NETJES_VITE_ORIGIN` when browsers reach the dev server at another address.
- **The socket.** A policy with `connect-src`, or with `default-src` and no `connect-src`, lists the socket's address in it: `ws://` and the dev server's host and port, such as `ws://localhost:5173` (`wss://` for an `https` origin), or `ws:`. A nonce does not cover connections, `'self'` stands for the app's own origin, whose port is another, and an `http://` source never matches a `ws://` address. Without it, browsers refuse the socket, and an edit shows only once you reload the page. When `server.hmr` in the Vite config moves the socket (its `host`, `port`, `clientPort` or `protocol`), list the address it gives instead.
- **The styles.** In Development the document carries the app's CSS inline, in `<style>` elements, and Vite's client adds more of them as modules load and change. Both carry the nonce when the document has one (Vite reads it from the `csp-nonce` meta). So the list that style elements follow, `style-src-elem`, else `style-src`, else `default-src`, needs that nonce or `'unsafe-inline'`, which counts only with no hash or nonce beside it; otherwise pages show without their styles. A client build links its CSS as files instead.
- **Images and fonts.** Vite serves the files the app imports, such as the URL that `import logo from './logo.png'` gives, and the files its CSS names with `url()`, from the dev server's origin too. So a policy with `img-src` or `font-src`, or with `default-src` and not them, lists that origin there, or those files do not load. `media-src` does the same for imported audio and video.

Netjes warns about the first three once per policy; it cannot see which images or fonts a page uses, so it does not check those. Under a policy without `'unsafe-eval'`, the browser can also report a violation of `script-src` with the blocked URI `eval` in Development, from the development code of React or Vite rather than from zod. The page still hydrates, the built app does not report it, and Netjes does not check for `'unsafe-eval'`. The simplest policy for Development is none: set the policy outside Development only.

```csharp
builder.Services.AddNetjes(options =>
{
    if (!builder.Environment.IsDevelopment())
    {
        options.ContentSecurityPolicy =
            $"script-src 'self' {NetjesContentSecurityPolicy.ScriptHashes}; connect-src 'self'; object-src 'none'; base-uri 'self'";
    }
});
```

To try the policy in Development as well, widen it there with the dev server's origin and its socket:

```csharp
builder.Services.AddNetjes(options =>
{
    var scripts = $"'self' {NetjesContentSecurityPolicy.ScriptHashes}";
    var connections = "'self'";
    if (builder.Environment.IsDevelopment())
    {
        // The Vite dev server, which serves the scripts there, and its socket, on the same host and port unless
        // server.hmr in the Vite config moves it.
        var devServer = new Uri(builder.Configuration["NETJES_VITE_ORIGIN"] ?? $"http://localhost:{options.ViteDevPort}");
        scripts += $" {devServer.GetLeftPart(UriPartial.Authority)}";
        connections += $" {(devServer.Scheme == "https" ? "wss" : "ws")}://{devServer.Authority}";
    }

    options.ContentSecurityPolicy = $"script-src {scripts}; connect-src {connections}; object-src 'none'; base-uri 'self'";
});
```

A policy with `style-src` adds `'unsafe-inline'` to it in Development in the same way, and one with `img-src`, `font-src` or `default-src` the dev server's origin.

## Server actions and page actions

Every server action (`MapAction`) and page action (`MapPageAction`) needs a valid ASP.NET Core antiforgery token and a same-origin request. Put `app.UseAntiforgery()` in the pipeline, after authentication. `<Form>`, `useAction` and `serverAction()` send the token; pages that carry none (output-cached ones) fetch it from `/_netjes/antiforgery` first.

- A token the server refuses: a client call gets 400 with `X-Netjes-Antiforgery: invalid`, which no other response carries, and the client sends that call once more with a fresh token (no other 400 proves that the action did not run). A post without JavaScript gets the 400 error page.
- The origin check, like Next.js's, compares the request's `Origin` with its `Host`, and refuses a request the browser marks `Sec-Fetch-Site: cross-site` whatever its origin: 403. `Origin: null` passes only when the browser also says `Sec-Fetch-Site: same-origin`. `NetjesOptions.AllowedActionOrigins` lists more origins, for a same-site origin such as a sibling subdomain; a cross-site post is refused even when its origin is listed.
- Behind a proxy that rewrites `Host`, every action fails the origin check until the app sees its public host. Use `UseForwardedHeaders`, or list the public origin in `AllowedActionOrigins`.

A page action that receives a form posted from another site (a payment or identity provider posting back) can turn both checks off with `.DisableAntiforgery()`. Check the post some other way, such as the provider's signature. On a server action `.DisableAntiforgery()` turns off the token check only; its origin check stays.

The antiforgery cookie keeps ASP.NET Core's defaults (`SameSite=Strict`, `HttpOnly`), so the same app works over plain HTTP in development. An app served only over HTTPS should make it a `__Host-` cookie, which a sibling subdomain or a plain-HTTP response cannot set or replace:

```csharp
builder.Services.AddAntiforgery(o =>
{
    o.Cookie.Name = "__Host-Antiforgery";
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.Path = "/"; // __Host- cookies need it, also under UsePathBase
});
```

With `SecurePolicy.Always`, ASP.NET Core refuses every antiforgery operation on a request that is not HTTPS (a 500), so behind a TLS-terminating proxy call `UseForwardedHeaders` first, and keep the default in development.

### Body limits

A server action's request body may be at most 1 MB (`NetjesOptions.ActionBodySizeLimit`, like Next.js's `serverActions.bodySizeLimit`); a larger one gets 413. The option only lowers the server's own limit (Kestrel's is 30 MB). Page actions keep the server's limit. An action that takes uploads sets its own with `[RequestSizeLimit(bytes)]`. [Body limits](actions-and-forms.md#body-limits) has the details.

### What a failed post hands back

A form whose action fails validation comes back with what was typed, so the user does not type it again. Only the fields the action declares come back, never files, and never a field whose name contains one of the words in `NetjesOptions.PrivateFormFields` (`password`, `token`, `card`, `pin`, `iban` and more). What comes back goes out `Cache-Control: no-store`: the page rendered again, or a server action's answer to a call from the client. [What a failed post hands back](actions-and-forms.md#what-a-failed-post-hands-back) has the default words, how they match and the size limits.

The return page of a post without JavaScript is always a page of the app: a return URL that routes anywhere else renders the home page (or the error page with 422, where `/` is no page either). Middleware registered before `UseNetjes()` does not run again for that render, so protect pages with endpoint authorization, not with middleware ahead of `UseNetjes()` ([Forms without JavaScript](actions-and-forms.md#forms-without-javascript)).

## Redirects

`Page.Redirect(url)` and `ServerAction.Redirect(url)` accept relative URLs and absolute `http` and `https` URLs, and throw for anything else, including a `%` before the first `:` or `/`, which could hide an encoded scheme (`javascript%3A`). Start a relative URL with `/` or `./`.

For a URL that comes from the request, such as a `returnUrl` query value, use `Page.LocalRedirect(url)`: it throws unless the URL is a path on this site (`/path`, not `//host` or `/\host`). `Page.IsLocalUrl(url)` checks one first, so you can fall back instead:

```csharp
return Page.IsLocalUrl(returnUrl) ? Page.LocalRedirect(returnUrl) : Page.Redirect("/");
```

The client router never loads a page from another origin: a redirect or link to another site is a full page load, handed to the browser.

Netjes's own redirect of another spelling of a page's URL ([One URL per page](routing.md#one-url-per-page)) is built from the URL the client sent, and never sends a location that could lead off the site. Under a root catch-all with `TrailingSlash.Never`, `//other.example/` would go to `//other.example`, `/\other.example/` to `/\other.example`, and `/%5Cother.example/` to a location that a client which decodes it reads as `/\other.example`: each of them is answered as it was sent, without a redirect. So is a URL whose location would hold a backslash, which browsers read as a slash, a `#`, which ends the path, a control character such as a tab, which browsers drop, or a character beyond ASCII, and one whose base path comes from a header, such as an `X-Forwarded-Prefix` taken from anyone, and would make the location start with `//` or `/\`.

## The image endpoint

`/_netjes/image` serves only the widths and qualities you configure, reads local images from `public/` only and only as their files are spelled, fetches remote images only from the hosts in `Netjes:Images:RemoteHosts` (https on the default port, public addresses only, no redirects followed, no proxy, within a size cap and a timeout), serves only PNG, JPEG, GIF, WebP and AVIF bytes (SVG only with `AllowSvg`, sandboxed), and bounds how many images it reads, fetches and processes at once. The [images guide](images.md) has every limit and setting.

With `Netjes.Images.SkiaSharp`, native codecs parse whatever a file in `public/` or an allow-listed host holds, even limited to PNG, JPEG and WebP. Keep SkiaSharp current: when a fix ships before `Netjes.Images.SkiaSharp` moves to it, reference the newer `SkiaSharp` and `SkiaSharp.NativeAssets.Linux.NoDependencies` packages from your project.

## The Node sidecar

The Node processes that render pages listen on `127.0.0.1` only, on a random port. Each gets a random token from .NET when it starts and refuses every request without it (403); a process does not start without one. The process removes the token from its environment before any page code runs. If you start `sidecar/server.mjs` yourself, set `NETJES_TOKEN` to a non-empty value and send it as the `X-Netjes-Token` header.

Page code that runs on the server runs in those processes, not in .NET. It sees the environment the app was started with, except the token.

## Layout loaders around 404 and error pages

`RequireAuthorization()` on a layout loader protects every page below its layout: each page's endpoint carries it, and the authorization middleware checks it before any loader runs ([Layout loaders](data-loading.md#layout-loaders)). Some pages render inside the layouts without the middleware having let their visitor in:

- the 404 for an unknown URL, which the fallback that `MapPages()` adds answers, in a document, a client navigation, a `HEAD` and `netjes export`'s `404.html`;
- the pages of other endpoints that serve no page: the page for an empty 401, 403 or other error status on the fallback or on a server action, the not-found and forbidden pages a minimal API asks for with `Page.NotFound()` or `Page.Forbidden()`, and the error page for an exception on one of those or for a 404 that failed to render;
- a page's own `unauthorized.tsx` or `forbidden.tsx`, for the visitor its authorization refused;
- a page's status and error pages from before the authorization middleware ran, or in place of its answer: an empty 429 or other status from a middleware ahead of `UseAuthorization()`, such as a rate limiter, an exception there, such as an authentication handler that cannot reach its identity provider, the 413 Netjes answers for a body over the size limit before the rest of the pipeline runs, the 404 that `UseNetjesLocalization` answers for an unsupported locale, and a refusal the app answers with an empty 404 to hide the page.

On these, before any layout loader on the way runs, Netjes judges each one as the authorization middleware would judge the pages in its folder, and the visitor has to pass both kinds of page. Every page there carries the conventions of the layout loaders down to it and those of the page package's mount where the folder holds the package's files, a page with a loader (`MapPageLoader`) too, which gets none of `MapPages()`'s: those must let the visitor in, unless they ask for no authorization. A file page there carries `MapPages()`'s as well, and is judged on all of them at once: an `.AllowAnonymous()` on any of them lets every visitor in, as on that page, and when none of them asks for authorization, the fallback policy applies. So `MapPages().AllowAnonymous()` opens no layout loader whose `RequireAuthorization()` the pages with a loader keep.

Authorization counts in every form the middleware enforces: `RequireAuthorization(...)`, an `[Authorize]` attribute, a policy, and requirements an endpoint's metadata states (`IAuthorizationRequirementData`), joined into one policy as there. The policy goes through the app's policy evaluator (`IPolicyEvaluator`), as in the middleware: a policy that names authentication schemes is checked against the user those schemes authenticate, which the request carries while the handlers run, and a policy that names none against the request's user. On a page from before `UseAuthentication()` ran, such as the 413, an unsupported locale's 404 or a rate limiter's 429 in the order these guides give (`UseNetjes()` first), that is the user the default authenticate scheme signs in, as that middleware would have found them, or nobody if the scheme throws. Once judged, the layout loaders run for the request's own user. A page whose own endpoint lets the visitor in, judged the same way, renders on its own chain as before.

The page stops above the first layout loader that refuses the visitor and renders inside the layouts above it, with the status it had ([ADR 0053](../decisions/0053-an-unknown-url-stays-a-404-and-runs-only-the-layout-loaders-the-visitor-may-see.md), [ADR 0054](../decisions/0054-layout-loaders-around-404-and-error-pages-are-judged-as-the-middleware-judges-their-pages.md)). `MapPages()`'s conventions never challenge an unknown URL: it stays a 404, with no login redirect, so its answer tells a visitor nothing about what the pages protect. A fallback policy does challenge it: the not-found fallback carries no authorization metadata, so the fallback policy covers it as it covers every such endpoint, and a visitor it refuses gets a 401, or a login redirect with cookie authentication, for any unknown URL. Folders without a layout loader still render, unless a page package's mount refuses the visitor there. A client navigation that claims to hold a refused layout's key gets no more than a document does.

Judging an unknown URL's chain authenticates the schemes that the policies on it name, as a page there would. A sliding cookie of such a scheme can therefore be renewed on the 404, which then carries its `Set-Cookie`, and the default scheme's can be renewed on a page from before `UseAuthentication()` ran.

So under `MapPages().RequireAuthorization()` or a fallback policy, a visitor the pages refuse runs no layout loader, the root layout's included: their 401 page renders without the layouts that have one, as does their 404 under `MapPages().RequireAuthorization()` (a fallback policy answers their unknown URL with its challenge), and the root `unauthorized.tsx` should stand on its own. `.AllowAnonymous()` on the root layout's loader is no way to keep the layout there, since it reaches every page below it and opens them all. A layout loader with `RequireAuthorization()` of its own, below the root, protects its section and leaves the layouts above it to every visitor.

`Page.Render(pattern)` renders a page from another endpoint, such as a minimal API, and runs that page's layout loaders under the authorization of the endpoint that calls it, not the page's: give that endpoint the authorization the page has.

The fallback carries none of `MapPages()`'s conventions itself, so its others, such as `RequireRateLimiting(...)`, `RequireCors(...)` or `CacheOutput(...)`, do not apply to unknown URLs. Put a limit that must cover every URL in middleware, or in a policy that applies to every endpoint (a global rate limiter, a CORS default policy).

## The layout key secret

On client navigations the router sends keys for the layouts it already shows, and the server skips the loaders of those layouts while the keys still match. A key is an HMAC of the layout's folder, the build, the user (authentication type, name, identifier and roles) and the layout's own route values, signed with `NetjesOptions.SegmentKeySecret`, so a client cannot make up a key for a layout it was not given, or reuse one across users.

- By default the secret is random per process. Set it, at least 32 bytes, when several instances should accept each other's keys (see [deployment](deployment.md#several-instances)); a shorter one stops the app at startup. Keep it out of source control like any secret.
- A layout loader binds only the route values of its own URL, and is skipped while they and the user stay the same. So an access check on a route value deeper than the layout's URL belongs in the page's loader, which runs on every navigation. [Data loading](data-loading.md) explains this.

## Page packages

A [page package's](page-packages.md) page actions and server actions get the same checks as the app's. Its server actions share one namespace with the app's, compared ignoring case, so an app cannot map a second action under a package's name, or the other way round: the second stops the app at startup, naming both.

Conventions on a package's mount, such as `RequireAuthorization()`, protect the package's own pages, loaders, actions and endpoints, and not the app's pages under the prefix, which the app protects as before: with their loaders' conventions, `MapPages()`'s, or a layout loader above them. A layout loader above the mount protects the package's pages too, and a package's layout loader with `RequireAuthorization()` protects the app's pages in its folder. The mount's conventions do not reach the package's layout loaders, which keep their own: an app page in a package's folder renders the package's layouts, and runs their loaders, for every visitor it lets in, so a package puts `RequireAuthorization()` on each layout loader whose props not every visitor may see. An unknown URL below the mount authorizes the layout loaders and the package's folders on the way before anything loads, under `MapPages()`'s authorization too, so a visitor they refuse gets a 404 from the nearest not-found above, without the package's layouts ([Protecting a mounted package](page-packages.md#protecting-a-mounted-package)).

Every page payload names each mounted package and its URL for the request in `mounts`, public pages and 404s included, and the client bundle's page module map holds the file ids of each package in `netjes({ packages })`, as it holds the app's. So `RequireAuthorization()` on a mount keeps the package's pages and their data from a visitor it refuses, not the fact that the package is there or where: anyone can read its npm name and its prefix (`/back-office`). Keep a secret out of a prefix, as out of any URL.

## Hosts and HTTPS

- `AllowedHosts`: the template's `appsettings.json` allows any host (`*`), and its `appsettings.Development.json` allows only `localhost;127.0.0.1;[::1]`, so a page that rebinds its DNS name to your machine cannot reach the dev app. Pin `AllowedHosts` to your host names in production: the output cache keys entries on the host, and every made-up host would otherwise get entries of its own. Made-up query strings make entries too: name the query keys each cached page reads with `.CacheQueryKeys(...)` ([caching](caching.md#query-strings-a-page-does-not-read)).
- HSTS and HTTPS redirection are ASP.NET Core's. The template calls `UseHsts()` and `UseHttpsRedirection()` outside Development.
- Behind a proxy that terminates TLS, call `UseForwardedHeaders()` first, trusting only that proxy's network, so the app sees the original scheme, host and client address. [Deployment](deployment.md#proxies-and-base-paths) has an example.

## Errors and configuration

- Error pages show exception messages and stacks only in Development (`IncludeErrorDetails`). In production an `error.tsx` gets the status's reason phrase as `error.message`, marked with `error.withheld`, and no stack. A failed `Page.Defer` value goes to the browser with an empty message, and a server action's 5xx with none; the browser marks both as withheld.
- Everything in `Netjes:PublicConfig` is sent to the browser as it is. Never put a secret there.
- The generated file names the app's endpoints: `api` its JSON endpoints, and `endpointUrl()` the method and route of each endpoint that has a key, an endpoint the app maps only in Development included, since the file is generated in Development. The routes are one map in that file, so once any component calls `endpointUrl`, all of them reach the browser in the chunk that holds the file. Pages share that chunk, usually through the root layout, so expect every page to load it, also one that never calls `endpointUrl`. While nothing calls it, the build leaves the map out. `api` ships its keys the same way. A route in the file is no easier to reach than one outside it: authorization stays on the endpoint (`RequireAuthorization()`) ([Endpoint URLs](data-loading.md#endpoint-urls)).
- Output-cached pages are served to every visitor; keep per-visitor data out of them. See [caching](caching.md#shared-props-and-cookies).

## The development server

The Vite dev server binds to `localhost`. `NETJES_VITE_HOST=0.0.0.0` (in the environment of `npm run dev`) exposes it to the network, and with it the workspace. The Vite plugin denies the backend's files to the dev server, on top of Vite's own list (`.env` files, certificates, `.git`): settings files, C# sources and build output, dev certificates and keys, SQLite databases and Compose files, all listed in the [Vite plugin reference](../reference/vite-plugin.md#the-dev-server). With page packages listed in `netjes({ packages })`, the dev server also reads each package's folder by its real path, wherever a workspace or `npm link` puts it, and denies its `bin/`, `obj/` and `dist/server/` folders, where a package linked from outside the workspace keeps its C# half's build output. Everything else under the workspace root (or your own `server.fs.allow`) is readable by anyone who can reach the port, so only expose it on a network you trust. The template's development Compose file binds its ports to `127.0.0.1`.
