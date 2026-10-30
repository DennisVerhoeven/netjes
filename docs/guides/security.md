# Security

This guide is for app developers putting a Netjes app in front of real users. It lists what Netjes does by default and what is left to you: the security headers, Content-Security-Policy, the antiforgery and origin checks on actions, request body limits, redirects, the image endpoint, the Node sidecar, the secret that signs layout keys, host names and HTTPS, and the development server. To report a vulnerability, follow [SECURITY.md](../../SECURITY.md).

## Defaults at a glance

| What | Default | Change it with |
|---|---|---|
| `X-Content-Type-Options`, `Referrer-Policy`, framing headers on documents | On | `Netjes:SecurityHeaders` |
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
| `X-Frame-Options` | `SAMEORIGIN`, unless the app decides about framing itself (below) |
| `Content-Security-Policy` | `frame-ancestors 'self'` added to `NetjesOptions.ContentSecurityPolicy` when you set one, likewise |

Client payloads (JSON) and static files get none of them.

Framing is the app's own once it sends `X-Frame-Options` itself, or has `frame-ancestors` in the Content-Security-Policy that goes out: its own header, or else `NetjesOptions.ContentSecurityPolicy`. Netjes then adds neither framing header, so nothing contradicts it. Browsers obey `frame-ancestors` over `X-Frame-Options`, which cannot name another site, so to let a partner frame your pages, send `frame-ancestors 'self' https://partner.example`. ASP.NET Core antiforgery adds `X-Frame-Options: SAMEORIGIN` with each new token cookie; browsers ignore it next to `frame-ancestors`, and `AntiforgeryOptions.SuppressXFrameOptionsHeader` turns it off.

`NetjesOptions.SecurityHeaders` (`Netjes:SecurityHeaders`) has two settings:

- `Enabled = false` turns them all off. To preview the app in an editor's frame, such as VS Code's Simple Browser, set it in `appsettings.Development.json`, and turn off the header that antiforgery adds in code, since ASP.NET Core does not bind `AntiforgeryOptions` from configuration:

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

Netjes makes a nonce for each document and puts it on everything it renders: the module scripts and preloads, the stylesheets, the payload, the scripts that stream deferred values and reveal Suspense boundaries, and Vite's `csp-nonce` meta (for the styles Vite injects in development). `useNonce()` returns it for inline scripts or styles of your own, and `<Script>` uses it.

A document with a nonce is never stored in the output cache: a stored copy would give every visitor the same, readable nonce. Netjes renders such a page each time and logs a warning once per output-cached page. For cached pages, use a hash-based policy.

If you write the policy yourself, for example with a headers library, call `HttpContext.GetCspNonce()` for the request's nonce, and Netjes puts the same one on what it renders. The output cache rule applies to that nonce as well.

### Hash-based

A policy without `{nonce}` makes no nonce, so pages under it can be output-cached. It must allow the one inline script Netjes puts in every document's head, the bootstrap that collects streamed values, by its hash:

```csharp
options.ContentSecurityPolicy =
    "script-src 'self' 'sha256-FSjVV+FzmXP8KKrLKLdvst/5N/bEWSkde01XzpGKRgs='; object-src 'none'; base-uri 'self'";
```

That is the hash of the bootstrap in this version; a Netjes version that changes its text changes the hash. Inline `<Script>` code of your own needs its hash listed too.

No hash covers what streams after the shell: each script that delivers a `Page.Defer` value carries that value, and React's scripts that reveal Suspense boundaries name the boundaries of that render. A hash-based policy blocks both, so a page served under one must not stream: no `Page.Defer`, and no Suspense boundary still waiting when the shell goes out (lazy code under `loading.tsx` included). Pages from `netjes export` are the exception, since their scripts are fixed text; see [static export](static-export.md#deferred-values-and-suspense).

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

## The image endpoint

`/_netjes/image` serves only the widths and qualities you configure, reads local images from `public/` only and only as their files are spelled, fetches remote images only from the hosts in `Netjes:Images:RemoteHosts` (https on the default port, public addresses only, no redirects followed, no proxy, within a size cap and a timeout), serves only PNG, JPEG, GIF, WebP and AVIF bytes (SVG only with `AllowSvg`, sandboxed), and bounds how many images it reads, fetches and processes at once. The [images guide](images.md) has every limit and setting.

With `Netjes.Images.SkiaSharp`, native codecs parse whatever a file in `public/` or an allow-listed host holds, even limited to PNG, JPEG and WebP. Keep SkiaSharp current: when a fix ships before `Netjes.Images.SkiaSharp` moves to it, reference the newer `SkiaSharp` and `SkiaSharp.NativeAssets.Linux.NoDependencies` packages from your project.

## The Node sidecar

The Node processes that render pages listen on `127.0.0.1` only, on a random port. Each gets a random token from .NET when it starts and refuses every request without it (403); a process does not start without one. The process removes the token from its environment before any page code runs. If you start `sidecar/server.mjs` yourself, set `NETJES_TOKEN` to a non-empty value and send it as the `X-Netjes-Token` header.

Page code that runs on the server runs in those processes, not in .NET. It sees the environment the app was started with, except the token.

## The layout key secret

On client navigations the router sends keys for the layouts it already shows, and the server skips the loaders of those layouts while the keys still match. A key is an HMAC of the layout's folder, the build, the user (authentication type, name, identifier and roles) and the layout's own route values, signed with `NetjesOptions.SegmentKeySecret`, so a client cannot make up a key for a layout it was not given, or reuse one across users.

- By default the secret is random per process. Set it, at least 32 bytes, when several instances should accept each other's keys (see [deployment](deployment.md#several-instances)); a shorter one stops the app at startup. Keep it out of source control like any secret.
- A layout loader binds only the route values of its own URL, and is skipped while they and the user stay the same. So an access check on a route value deeper than the layout's URL belongs in the page's loader, which runs on every navigation. [Data loading](data-loading.md) explains this.

## Hosts and HTTPS

- `AllowedHosts`: the template's `appsettings.json` allows any host (`*`), and its `appsettings.Development.json` allows only `localhost;127.0.0.1;[::1]`, so a page that rebinds its DNS name to your machine cannot reach the dev app. Pin `AllowedHosts` to your host names in production: the output cache keys entries on the host, and every made-up host would otherwise get entries of its own.
- HSTS and HTTPS redirection are ASP.NET Core's. The template calls `UseHsts()` and `UseHttpsRedirection()` outside Development.
- Behind a proxy that terminates TLS, call `UseForwardedHeaders()` first, trusting only that proxy's network, so the app sees the original scheme, host and client address. [Deployment](deployment.md#proxies-and-base-paths) has an example.

## Errors and configuration

- Error pages show exception messages and stacks only in Development (`IncludeErrorDetails`). In production an `error.tsx` gets the status's reason phrase as `error.message`.
- Everything in `Netjes:PublicConfig` is sent to the browser as it is. Never put a secret there.
- Output-cached pages are served to every visitor; keep per-visitor data out of them. See [caching](caching.md#shared-props-and-cookies).

## The development server

The Vite dev server binds to `localhost`. `NETJES_VITE_HOST=0.0.0.0` (in the environment of `npm run dev`) exposes it to the network, and with it the workspace. The Vite plugin denies the backend's files to the dev server, on top of Vite's own list (`.env` files, certificates, `.git`): settings files, C# sources and build output, dev certificates and keys, SQLite databases and Compose files, all listed in the [Vite plugin reference](../reference/vite-plugin.md#the-dev-server). Everything else under the workspace root (or your own `server.fs.allow`) is readable by anyone who can reach the port, so only expose it on a network you trust. The template's development Compose file binds its ports to `127.0.0.1`.
