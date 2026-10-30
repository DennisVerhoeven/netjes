# Options

This page lists every setting of the Netjes NuGet packages: each `NetjesOptions` property with its configuration key, type, default and effect, the nested image and security-header options, the options that are set in code only, the `Netjes:PublicConfig` section, the environment variables Netjes reads, and the MSBuild properties of `build/Netjes.targets`. It is a reference for app developers; the guides explain when to use each. The Vite plugin's options are in the [Vite plugin reference](vite-plugin.md), the CLI's in the [CLI reference](cli.md).

## Where options come from

`AddNetjes()` binds `NetjesOptions` from the `Netjes` configuration section, so every ASP.NET Core configuration source can set them, and then runs the delegate you pass, which wins:

```csharp
builder.Services.AddNetjes(options =>
{
    options.SidecarProcesses = 4;
    options.ContentSecurityPolicy = "script-src 'nonce-{nonce}' 'strict-dynamic'; object-src 'none'; base-uri 'self'";
});
```

```json
{
  "Netjes": {
    "SidecarProcesses": 4,
    "RenderTimeout": "00:00:20",
    "Images": { "RemoteHosts": ["images.example.com"] },
    "SecurityHeaders": { "DraftFrameAncestors": ["https://cms.example"] }
  }
}
```

The key of a property is `Netjes:<Name>`, and of a nested one `Netjes:Images:<Name>` or `Netjes:SecurityHeaders:<Name>`. In environment variables the separator is a double underscore (`Netjes__SidecarProcesses=4`, `Netjes__Images__RemoteHosts__0=images.example.com`). Durations are written `hh:mm:ss`.

Configuration adds list items to what a list holds already, while the delegate replaces the list. So configured `PrivateFormFields` are added to the default words. `Images:Widths` and `Images:Qualities` get their defaults only when nothing set them, so configured values replace those.

## NetjesOptions

### Files and mode

| Option | Type | Default | What it does |
|---|---|---|---|
| `Root` | `string?` | the content root | The folder that holds `package.json`, `vite.config.ts` and `app/`. Relative to the content root. |
| `AppDirectory` | `string` | `app` | The folder of page files, relative to `Root`. Must match the Vite plugin's `appDir`. |
| `DistDirectory` | `string` | `dist` | Where the build is, relative to `Root`: `client/` (with Vite's manifest in `client/.vite/`) and `server/`. |
| `PublicDirectory` | `string` | `public` | Static files served as they are in development, relative to `Root`. In production Vite has copied them into the client build. |
| `Mode` | `NetjesMode?` | `Development` in the Development environment, else `Production` | `Development` compiles pages on demand with the Vite dev server and reads the routes from the files in `app/`; `Production` serves the build in `dist/` and reads the routes from its manifest. |
| `TypesFile` | `string?` | `generated/netjes.ts` | The generated TypeScript, relative to `Root`. Written at startup in development when it changed, and by `netjes generate`. `null` (in code) turns generation off. |

### Rendering and the sidecar

| Option | Type | Default | What it does |
|---|---|---|---|
| `NodeExecutable` | `string` | `node` | The Node.js executable that runs the sidecar processes. When it cannot be started, the app logs that in one line, naming this setting, and stops with exit code 1. |
| `SidecarScript` | `string?` | the `netjes` package's `sidecar/server.mjs` in development (found in `node_modules` here or above), `dist/server/netjes-sidecar.mjs` in production | The sidecar script. Relative to `Root`. When it is not there, the app says to run `npm install` (development) or `npm run build` (production), in one line, and stops with exit code 1. |
| `SidecarProcesses` | `int` | `1` | How many Node processes render pages in production. Development always runs one. Less than 1 stops the app at startup. See [rendering](../guides/rendering-and-streaming.md#the-sidecar-pool). |
| `ViteDevPort` | `int` | `5173` | The Vite dev server's port in development. When something else has it, such as another app's Vite dev server, the app logs that in one line, naming the port and this setting, and stops with exit code 1. |
| `StartupTimeout` | `TimeSpan` | 60 s | How long the sidecar processes may take to report ready at startup; past it the app does not start. |
| `RenderTimeout` | `TimeSpan` | 15 s | How long a render may take to produce the shell of a streamed document (everything outside Suspense boundaries that are still waiting). Also how long a render waits for a restarting process; a complete document waits up to `StreamTimeout`. |
| `StreamTimeout` | `TimeSpan` | 60 s | How long a whole document may take, Suspense boundaries and `Page.Defer` values included; a deferred value not there by then fails. Complete documents (crawlers, `StreamDocuments = false`, export) wait up to this long. |
| `StreamDocuments` | `bool` | `true` | Stream documents: the shell first, with fallbacks for what is still waiting, the rest as it is ready. `false` sends every document complete. |
| `CrawlerUserAgents` | `string` | a regular expression matching search engine bots, social previews, validators and Lighthouse | A regular expression, matched without regard to case against the `User-Agent`; matching requests get complete documents. |
| `ClientRenderFallback` | `bool` | `true` | In production, a document request that finds no sidecar process ready is answered with a shell .NET writes, and the browser renders the page. `false` waits for a restart and shows the error page if none comes in time. See [rendering](../guides/rendering-and-streaming.md#when-no-sidecar-is-ready). |
| `Lang` | `string` | `en` | The `lang` attribute of `<html>`. With `AddNetjesLocalization` the request's locale is used instead. |
| `IncludeErrorDetails` | `bool?` | `true` in the Development environment, else `false` | Show exception messages and stacks in error pages and in failed actions' and deferred values' messages. |

### Actions and forms

| Option | Type | Default | What it does |
|---|---|---|---|
| `AllowedActionOrigins` | `IReadOnlyList<string>` | none | Origins besides the request's own host that may call server actions and post to page actions, such as `https://shop.example`. A request the browser marks `Sec-Fetch-Site: cross-site` is refused whatever its origin. Prefer forwarded headers, so the app sees its public host. |
| `ActionBodySizeLimit` | `long?` | `1048576` (1 MB) | The largest request body a server action (`MapAction`) accepts, in bytes; larger ones get 413. It only lowers the server's own limit, never raises it. `null` (or an empty value in configuration) leaves the server's limit. Page actions keep the server's limit. An action with its own `[RequestSizeLimit]` or `[DisableRequestSizeLimit]` uses that. |
| `PrivateFormFields` | `IReadOnlyList<string>` | `password`, `passwd`, `pwd`, `pass`, `pw`, `passcode`, `passphrase`, `secret`, `token`, `apikey`, `privatekey`, `credential`, `pin`, `otp`, `totp`, `mfa`, `recovery`, `cvc`, `cvv`, `csc`, `cc`, `card`, `iban`, `ssn`, `bsn` | Words that mark a form field as private: a failed action never hands its value back. Compared without regard to case; words of five letters or more match anywhere in a name, shorter ones as a word of it or at a word's start or end. Configuration adds words to this list; the delegate replaces it. See [what a failed post hands back](../guides/actions-and-forms.md#what-a-failed-post-hands-back). |

### Security

| Option | Type | Default | What it does |
|---|---|---|---|
| `ContentSecurityPolicy` | `string?` | `null`: no policy | A Content-Security-Policy for documents. With `{nonce}`, each document gets a nonce in its place and on everything Netjes renders, and is never output-cached. Without it (hash-based), no nonce is made and pages can be cached, but must not stream. `frame-ancestors 'self'` is added when the policy has none. Not sent when the app set the header itself. See [security](../guides/security.md#content-security-policy). |
| `SecurityHeaders` | `NetjesSecurityHeaderOptions` | below | The headers Netjes adds to documents. |
| `SegmentKeySecret` | `string?` | random per process | The secret that signs layout keys, at least 32 bytes of UTF-8 (shorter stops the app at startup). Set the same value on every instance so they accept each other's keys and share cached payloads that reuse layouts. See [deployment](../guides/deployment.md#the-segment-key-secret). |

### Caching, drafts and telemetry

| Option | Type | Default | What it does |
|---|---|---|---|
| `DraftModeDuration` | `TimeSpan` | 1 hour | How long `IDraftMode.Enable()` keeps draft mode on. |
| `CollectWebVitals` | `bool` | `false` | Accept Core Web Vitals from `<WebVitals />` at `/_netjes/vitals` (mapped by `MapPages()`) and record them as `netjes.web_vitals.*` histograms. |

### Images

| Option | Type | Default | What it does |
|---|---|---|---|
| `Images` | `NetjesImageOptions` | below | The `<Image>` endpoint. |

### Shared props (code only)

| Member | Type | What it does |
|---|---|---|
| `UseSharedProps<T>(Func<HttpContext, ValueTask<T>> factory)` | method, returns the options | Props for every page and layout, read with `useSharedProps(sharedProps)`, and typed as `sharedProps` in the generated file. |
| `SharedProps` | `Func<HttpContext, ValueTask<object?>>?` | The same, untyped: the generated file does not know their shape. Setting it clears `SharedPropsType`. |
| `SharedPropsType` | `Type?` (read-only) | The type `UseSharedProps<T>` was called with; `null` when untyped. |

Output-cached pages store the shared props with the page, so they must not depend on cookies or other per-visitor state; see [caching](../guides/caching.md#shared-props-and-cookies).

## NetjesImageOptions (`Netjes:Images`)

| Option | Type | Default | What it does |
|---|---|---|---|
| `Widths` | `int[]` | Next.js's device and image sizes: 16, 32, 48, 64, 96, 128, 256, 384, 640, 750, 828, 1080, 1200, 1920, 2048, 3840 | The only widths the endpoint produces; `<Image>` builds its `srcset` from them. Configured widths replace the defaults. |
| `Qualities` | `int[]` | `[75]` | The qualities the endpoint accepts besides `DefaultQuality`; `<Image quality>` rounds to the nearest. Configured ones replace the default. |
| `DefaultQuality` | `int` | `75` | The quality when a request names none. |
| `RemoteHosts` | `string[]` | none | Hosts remote images may come from: `images.example.com`, or `*.example.com` for its subdomains. Nothing else is fetched. |
| `RemoteRequireHttps` | `bool` | `true` | Fetch remote images over https only. |
| `RemotePorts` | `int[]` | none | Ports remote images may come from besides the scheme's default. |
| `RemoteTimeout` | `TimeSpan` | 10 s | How long fetching a remote image may take, headers and body together; past it, 504. |
| `MaxSourceBytes` | `long` | `20971520` (20 MB) | The largest source image read, local or remote. |
| `MaxConcurrency` | `int` | one per CPU | How many images are read and processed at once. |
| `MaxRemoteFetches` | `int` | `16` | How many remote images are fetched at once, in slots of their own, kept until the image is processed. |
| `MaxQueue` | `int` | `1000` | How many requests may wait for a slot, in each of the two pools; past it, 503 with `Retry-After`. |
| `CacheDuration` | `TimeSpan` | 7 days | How long browsers and the output cache keep an optimised image. |
| `AllowSvg` | `bool` | `false` | Serve SVG files, unprocessed and sandboxed. Off because SVG can carry script. |
| `Path` | `string` | `/_netjes/image` | The endpoint's path, used with one leading slash and none trailing. Other spellings of it are refused. |

The [images guide](../guides/images.md) explains the limits and what they cost in memory.

## NetjesSecurityHeaderOptions (`Netjes:SecurityHeaders`)

| Option | Type | Default | What it does |
|---|---|---|---|
| `Enabled` | `bool` | `true` | Add `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin` and same-origin framing (`X-Frame-Options: SAMEORIGIN`, `frame-ancestors 'self'` in Netjes's policy) to documents, each unless the app set it. `false` adds none. |
| `DraftFrameAncestors` | `string[]` | none | Sites that may frame pages in draft mode, such as a CMS's preview: each one CSP source (`https://cms.example`, `https://*.cms.example`); anything else stops the app at startup. Ignored when the app sends its own `X-Frame-Options` or `Content-Security-Policy`, or has `frame-ancestors` in `ContentSecurityPolicy`. |

See [security](../guides/security.md#security-headers).

## Options set in code only

These are not bound from configuration.

### NetjesLocalizationOptions

`services.AddNetjesLocalization(options => ...)`:

| Option | Type | Default | What it does |
|---|---|---|---|
| `SupportedCultures` | `string[]` | `["en"]` | The locales, by culture name. The first is the default. A name matches only that culture exactly. |
| `RouteParameter` | `string` | `lang` | The route parameter that holds the locale: `app/[lang]/...`. |

### SkiaImageOptions

`services.AddNetjesSkiaImages(options => ...)`, from `Netjes.Images.SkiaSharp`:

| Option | Type | Default | What it does |
|---|---|---|---|
| `MaxPixels` | `long` | `40000000` | Images whose header declares more pixels are refused before decoding (about 160 MB decoded at the default). |
| `MaxConcurrency` | `int` | one per CPU | How many images are decoded and encoded at once; the rest wait. `Netjes:Images:MaxConcurrency` limits the whole endpoint, reading included. |

## Netjes:PublicConfig

A configuration section sent to the browser with the first document and read with `usePublicConfig(schema)`, like Next.js's `NEXT_PUBLIC_` variables but at runtime, so one build runs in every environment. Keys follow the app's JSON naming policy (camelCase by default), values are strings (configuration has no other type; use coercing schemas), and numbered keys become arrays. Everything in it is public. See [data loading](../guides/data-loading.md).

```json
{ "Netjes": { "PublicConfig": { "ApiBase": "https://api.example.com", "Features": { "Search": "true" } } } }
```

## Environment variables

| Variable | Read by | What it does |
|---|---|---|
| `NETJES_COMMAND`, `NETJES_OUTPUT`, `NETJES_CHECK` | the app | Set by the `netjes` CLI to run the app once for `generate`, `routes` or `export`. Read only from the process environment, never from other configuration. |
| `NETJES_VITE_HOST` | the sidecar, in development | The Vite dev server's host. Unset, it listens on `localhost`; `0.0.0.0` exposes it to the network (see [security](../guides/security.md#the-development-server)). |
| `NETJES_VITE_ORIGIN` | the sidecar, in development | The origin browsers load modules from, when it is not `http://localhost:<ViteDevPort>` (a container, a remote machine). |
| `NETJES_TOKEN` | the sidecar | The token every request to it must carry. Netjes sets it; set it yourself only to run `sidecar/server.mjs` by hand. |
| `DOTNET_USE_POLLING_FILE_WATCHER` | ASP.NET Core | In development, polls `app/` for changes (every four seconds) instead of using file system events; the template's development image sets it. |

Netjes passes the sidecar its other settings (mode, root, ports, timeouts) itself.

## MSBuild properties

Set in the app's project file, read by `build/Netjes.targets`, which the Netjes package imports into the project:

| Property | Default | What it does |
|---|---|---|
| `NetjesRoot` | the project's folder | Where `package.json` and `dist/` are. |
| `NetjesBuildCommand` | `npm run build` | The client build that `dotnet publish` runs. |
| `NetjesSkipClientBuild` | unset | `true` publishes the `dist/` that is already there instead of building it. |

The targets also keep `node_modules/`, `dist/`, `test-results/`, `playwright-report/` and `publish/` out of the project's items, keep `package.json`, `package-lock.json` and `tsconfig.json` out of the output, and add `using Netjes;` when implicit usings are on. See [deployment](../guides/deployment.md#build-and-publish).
