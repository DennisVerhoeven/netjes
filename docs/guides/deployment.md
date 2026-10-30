# Deployment

This guide is for app developers taking a Netjes app to production: what `dotnet publish` produces and what the server needs, the template's Docker and Compose setup, configuration, running several instances behind a load balancer, health checks, telemetry and logs, and running behind a proxy or under a base path. Security settings to review before you go live are in [security](security.md).

## Build and publish

A Netjes app is an ASP.NET Core app with a client build next to it. `dotnet publish` makes both:

```bash
dotnet publish -c Release -o bin/publish
```

The build leaves `bin/`, `obj/` and a `publish/` folder in the project out of the app's content, so `-o publish` works as well as `-o bin/publish`. A folder with another name is not left out: the Web SDK takes the JSON and config files under the project folder as content, so it would be copied into the next build's output. The template's `.gitignore` and `.dockerignore` list `publish/`.

The Netjes package's `build/Netjes.targets`, imported into every project that references it, runs `npm run build` during publish (`vite build && vite build --ssr` in the template) and ships `dist/` with the app:

- `dist/client/`: the browser's JavaScript and CSS, content-hashed under `assets/`, and the files from `public/`, with Vite's manifest in `.vite/`.
- `dist/server/`: the server bundle the Node processes import, with React and every other dependency bundled in, and the sidecar script (`netjes-sidecar.mjs`).

The published folder runs without `node_modules`. Three MSBuild properties change what publish does:

| Property | Default | |
|---|---|---|
| `NetjesSkipClientBuild` | unset | `true` publishes a `dist/` that is already built, for a CI step or Docker stage that builds the client on its own. |
| `NetjesBuildCommand` | `npm run build` | The command that builds the client. |
| `NetjesRoot` | the project's folder | Where `package.json` and `dist/` are. |

`netjes build` does the client build on its own (it also writes the generated types first), and `netjes start` runs the app in Production from the project or a published `.dll` (run from the `.dll`'s folder). See the [CLI reference](../reference/cli.md).

## What the server needs

- The ASP.NET Core runtime, 10.0.14 or newer.
- Node.js `^22.22.2 || ^24.15.0 || >=26.0.0` (Node.js 22.22.2 or a later 22, 24.15.0 or a later 24, or 26 or newer), as `node` on the path (or `NetjesOptions.NodeExecutable`). Netjes starts the Node processes itself; you do not run them.
- The published folder, with `dist/`. In Production the app reads `dist/client/.vite/manifest.json` at startup and stops when it is missing.

The app runs in Production mode whenever the environment is not Development (`ASPNETCORE_ENVIRONMENT`); `Netjes:Mode` sets the mode outright.

When the app finds no build, or cannot start Node.js, it says so in one line and exits with code 1. It logs the reason as a critical entry, the last thing it logs, with what to do:

```text
crit: Netjes.Rendering.ViteManifestProvider[0]
      No production build found at '/app/dist/client/.vite/manifest.json'. To develop, run `npm run dev`, or start the app with ASPNETCORE_ENVIRONMENT=Development. To run in production, build first: `npm run build` in '/app'.
```

For Node.js the line is `Could not start 'node'. Is Node.js installed and on the PATH? Set Netjes:NodeExecutable to use another executable.`, and for a build without the sidecar script `` Cannot find the netjes sidecar at '/app/dist/server/netjes-sidecar.mjs'. Run `npm run build` in '/app'. ``. A container runtime or process manager sees exit code 1, as for any app that fails to start. In a test host such as `WebApplicationFactory`, the start fails with that message instead ([testing](testing.md#the-app-over-http)).

## Docker

Every app made with `netjes new` has a `Dockerfile` and three Compose files. An app scaffolded from a Netjes checkout references that checkout instead of the packages, and the Docker build cannot see the checkout, so pass `--packages` there.

```bash
docker compose up                                                     # development: dotnet watch + Vite HMR on http://localhost:8080
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d  # the production image
```

The `Dockerfile` has these stages:

| Stage | What it does |
|---|---|
| `deps` | `npm ci`, cached until `package.json` or the lockfile changes. |
| `dev` | The .NET SDK with Node, running `dotnet watch run`; the source is bind-mounted. |
| `client` | `npm run build`: `dist/client` and `dist/server`. |
| `build` | `dotnet publish` with `-p:NetjesSkipClientBuild=true`, taking `dist/` from `client`. |
| `production` | The ASP.NET Core runtime image plus the `node` binary, the published app, running as the non-root `app` user, with a `HEALTHCHECK` on `/healthz`. |

The Compose files:

- `docker-compose.yml` defines the `app` service from the `production` stage, with `pull_policy: build`, so `up -d` always builds and never reuses a stale or development image.
- `docker-compose.override.yml`, which Compose loads by default, switches to the `dev` stage, bind-mounts the source, and publishes ports 8080 and 5173 on `127.0.0.1` only: development error pages show stack traces and Vite serves the source.
- `docker-compose.prod.yml` publishes port 8080 and runs the container read-only, with a `tmpfs` at `/tmp`, `no-new-privileges`, every capability dropped, limits of one CPU and 512 MB, `restart: always`, and a named volume for ASP.NET Core's Data Protection keys, which sign antiforgery tokens and the draft mode cookie, so they survive restarts and new images.

ASP.NET Core logs a warning at startup that those keys are not encrypted at rest. Encrypt them with one of the `ProtectKeysWith...` methods (a certificate, Azure Key Vault) if that matters where you run.

## Configuration

`NetjesOptions` binds from the `Netjes` configuration section, so any ASP.NET Core configuration source sets it: `appsettings.json`, `appsettings.Production.json`, environment variables (`Netjes__SidecarProcesses=4`, `Netjes__Images__RemoteHosts__0=images.example.com`), the command line. Code in `AddNetjes(options => ...)` runs after the binding and wins.

```json
{
  "AllowedHosts": "shop.example",
  "Netjes": {
    "SidecarProcesses": 4,
    "Images": { "RemoteHosts": ["images.example.com"] },
    "PublicConfig": { "ApiBase": "https://api.shop.example" }
  }
}
```

`Netjes:PublicConfig` is sent to the browser as it is and read with `usePublicConfig()`, so the same build runs in every environment (see [data loading](data-loading.md)). Every option and key is in the [options reference](../reference/options.md).

Startup work the CLI's `generate` and `routes` commands do not need, such as migrating a database, can be skipped with `app.GetNetjesCommand()`:

```csharp
if (app.Environment.IsDevelopment() && app.GetNetjesCommand() is null)
{
    await Database.MigrateAndSeedAsync(app.Services);
}
```

## Sizing

- `SidecarProcesses` (default 1) sets how many Node processes render pages. A process renders one document at a time on one core, so an app whose pages take real CPU time to render wants about one per core. Client navigations do not use them. See [rendering](rendering-and-streaming.md#the-sidecar-pool).
- The image endpoint's memory is bounded by its settings, roughly `MaxConcurrency` × (2 × `MaxSourceBytes` + 4 bytes × the processor's `MaxPixels`) + `MaxRemoteFetches` × `MaxSourceBytes`. Lower them in small containers; see [images](images.md).
- The output cache's `SizeLimit` (100 MB by default) bounds the memory of the in-memory store.

## Several instances

Behind a load balancer, any instance may answer any request, including each client navigation of one visitor. The instances need to share three things, and a rolling deploy runs two builds side by side for a while.

### The Data Protection key ring

Authentication cookies, antiforgery tokens and the draft mode cookie are protected with ASP.NET Core Data Protection. Give every instance the same application name and a key ring they all read, as for any ASP.NET Core app behind a load balancer:

```csharp
builder.Services.AddDataProtection()
    .SetApplicationName("MyApp")
    .PersistKeysToFileSystem(new DirectoryInfo("/keys")); // or a database, Redis, Azure Blob Storage
```

### The output-cache store

Cached pages use ASP.NET Core output caching, whose default store is in memory, per instance. Each instance then renders and stores its own copy of a page, and `Revalidate`, `RevalidatePath` and `IPageCache` evict only on the instance that ran them. Register a store the instances share, such as Redis (`Microsoft.AspNetCore.OutputCaching.StackExchangeRedis`):

```csharp
builder.Services.AddStackExchangeRedisOutputCache(options => options.Configuration = "redis:6379");
builder.Services.AddOutputCache();
```

Then an entry stored by one instance is served by the others on the same build (for payloads that reuse a layout, only with the same secret, below), and revalidating on one evicts for all. Signed-in visitors are never served or stored either way.

### The segment key secret

On client navigations the router sends the keys of the layouts it holds (`X-Netjes-Segments`), and the server skips the layout loaders whose key is unchanged. Keys are signed with `NetjesOptions.SegmentKeySecret`, which is random per process by default, so a key one instance issued means nothing to another: that instance loads the layout in full.

The output cache keys a client payload by which layouts the visitor holds a valid key for. A payload that reuses a layout is also keyed by a fingerprint of the secret (never the secret itself), because it names the visitor's key, and only instances with the same secret accept it. Documents and full payloads reuse nothing, so every instance on the store shares them, whatever its secret.

With the default secret, then, a layout is reused only when the instance that signed the visitor's key answers, and each instance stores its own payloads that reuse a layout. To reuse layouts on every instance and share those entries, set the same secret everywhere, at least 32 bytes, kept out of source control like any secret:

```bash
openssl rand -base64 32                  # once
Netjes__SegmentKeySecret=<that value>    # in every instance's environment
```

Changing the secret makes the keys that clients hold unknown, so their next navigation loads its layouts in full, and leaves the entries stored under the old one to expire.

### Rolling deploys

While a deploy replaces instances one by one, instances on the old and the new build answer side by side and share the store. Every cached page is keyed by the client build, the version of the Vite build the instance serves, so neither serves the other's entries: a document names its own build's assets, which instances on the other build may not have, and a payload from another build makes the client load the page again. Each build renders and stores its own copies, so the new build starts with an empty cache, and the old build's entries expire on their own.

A visitor whose browser runs the old build and whose navigation reaches a new instance gets the new build's payload, so the browser loads that page as a new document; there is nothing to configure for that. Until the deploy completes, a load balancer that spreads one visitor's requests over both builds can send that document, or a later navigation, to the other build again, and each switch loads the page anew. Sticky sessions avoid that.

### Size and hosts

A cached page's entry is keyed on the host, the path and query string as sent, and the client build, and the cache's size limit counts bodies, not keys. Pin `AllowedHosts` and bound the store's size (`SizeLimit` for the in-memory store, the server's memory limit for Redis).

## Health checks

`AddNetjes()` on the health checks builder adds a check of the Node processes:

```csharp
builder.Services.AddHealthChecks().AddNetjes();
// ...
app.MapHealthChecks("/healthz");
```

| Status | When | `/healthz` answers |
|---|---|---|
| Healthy | Every process is ready. | 200 |
| Degraded | Some processes are restarting; the others take the renders. | 200 |
| Unhealthy | None is ready: starting, or all restarting. Documents are then [rendered in the browser](rendering-and-streaming.md#when-no-sidecar-is-ready). | 503 |

The status codes are ASP.NET Core's defaults for `MapHealthChecks`. The template's `Dockerfile` and `docker-compose.yml` use `/healthz` for Docker's health check.

## Telemetry

Netjes reports traces and metrics through an `ActivitySource` and a `Meter`, both named `Netjes` (`NetjesTelemetry.Name`). With OpenTelemetry:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(NetjesTelemetry.Name).AddAspNetCoreInstrumentation())
    .WithMetrics(metrics => metrics.AddMeter(NetjesTelemetry.Name).AddAspNetCoreInstrumentation());
```

| Span | Covers | Tags |
|---|---|---|
| `netjes.loader` | A page loader | `netjes.route` |
| `netjes.action` | A page action | `netjes.route` |
| `netjes.render` | Building the payload and, for documents, rendering | `netjes.route`, `netjes.page`, `netjes.response` (`document` or `data`), `http.response.status_code` |
| `netjes.sidecar.render` | The call to a Node process (a client span) | |

The call to the Node process carries `traceparent`, and the process logs it with render errors, so its logs can be matched to the trace.

| Metric | Unit | Tags |
|---|---|---|
| `netjes.render.duration` | s | `netjes.route`, `netjes.response`, `http.response.status_code` |
| `netjes.sidecar.render.duration` | s | |
| `netjes.sidecar.fallbacks` | documents | `netjes.route` |
| `netjes.web_vitals.lcp`, `.inp`, `.fcp`, `.ttfb` | ms | `netjes.route`, `netjes.web_vitals.rating` |
| `netjes.web_vitals.cls` | | `netjes.route`, `netjes.web_vitals.rating` |

Tags carry route patterns (`/blog/[slug]`), never URLs.

### Web vitals

To collect Core Web Vitals from real visitors, turn on `NetjesOptions.CollectWebVitals`, install `web-vitals`, and render `<WebVitals />` from `netjes/vitals` in the root layout. Each page load then posts its vitals to `/_netjes/vitals`, which records them as the `netjes.web_vitals.*` histograms. The endpoint accepts anyone's reports, so it checks each one and tags it only with route patterns the app has (`unknown` otherwise). `useReportWebVitals(callback)` from the same module hands you the measurements to send elsewhere.

## Logs

Netjes logs through `ILogger`, under these categories:

| Category | What it logs |
|---|---|
| `Netjes.Sidecar.NodeSidecar` | The Node processes: their output (stdout as information, stderr as warnings, without terminal colour codes), exits with their last lines, restarts, processes taken out and back. |
| `Netjes.Sidecar.NodeSidecarHostedService` | Why the app could not start its Node processes, or in development the Vite dev server: one critical entry, after which the app exits with code 1. |
| `Netjes.Rendering.ViteManifestProvider` | That the app started in Production without a build: one critical entry, after which it exits with code 1. |
| `Netjes.Rendering.PageRenderer` | Documents sent for the browser to render, deferred values that could not be delivered, output-cached pages that cannot be stored (a nonce, an endpoint that is not keyed, the middleware order). |
| `Netjes.Rendering.PageErrorMiddleware` | Exceptions while serving a page (errors) and bad requests (warnings). |
| `Netjes.Rendering.CachePaths` | Output-cached renders that read a request cookie. |
| `Netjes.Deferred` | `Page.Defer` values that failed or could not be serialized. |
| `Netjes.ServerActions` | Server action calls that failed (errors) or were refused (warnings). |
| `Netjes.Routing.RouteDiagnostics` | [Route warnings](routing.md) at startup. |
| `Netjes.Routing.FileSystemRouteTableProvider` | Development: route table rescans and the conflicts they find. |
| `Netjes.TypeGen.TypeGenHostedService` | Writing `generated/netjes.ts` in Development, and the `generate` and `routes` commands. |
| `Netjes.Export.StaticExportService` | `netjes export`: what it wrote, and why a page could not be exported. |

Set their levels as for any category:

```json
{
  "Logging": {
    "LogLevel": {
      "Netjes.Routing.RouteDiagnostics": "Error"
    }
  }
}
```

## Proxies and base paths

Behind a reverse proxy or a TLS-terminating load balancer, tell the app what the visitor sent, trusting only the proxy's network. The examples do it like this:

```csharp
if (app.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() is { Length: > 0 } proxyNetworks)
{
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    foreach (var network in proxyNetworks)
    {
        forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }

    app.UseForwardedHeaders(forwarded);
}

app.UseNetjes();
```

A proxy in front of a Netjes app must pass these headers through unchanged:

| Header | Direction | Why |
|---|---|---|
| `X-Netjes-Data`, `X-Netjes-Segments` | request | Client navigations ask for the page payload, and name the layouts they hold. |
| `X-Netjes-Cookies` | response | Tells the client router that an action signed someone in or out, so it forgets the pages it keeps for Back and Forward and clears the query cache. |
| `X-Netjes-Antiforgery` | response | Marks a 400 for a refused antiforgery token, the only one the client sends again. |
| `X-Netjes-Now` | response | The server's clock, which the client uses to judge how fresh seeded data is. |

A proxy that also caches must keep responses apart by `X-Netjes-Data` and `X-Netjes-Segments` (they carry `Vary` for both), or not cache pages; see [caching](caching.md#cdns-and-proxies).

A proxy that rewrites `Host` makes every action fail the same-origin check until the app sees its public host. Forward the host as well (`ForwardedHeaders.XForwardedHost`, with `ForwardedHeadersOptions.AllowedHosts` set to your host names), or list the public origin in `AllowedActionOrigins`; see [security](security.md#server-actions-and-page-actions).

To serve the app under a path, like Next.js's `basePath`, set it on both sides:

```csharp
app.UsePathBase("/docs"); // before UseNetjes()
app.UseNetjes();
```

```ts
// vite.config.ts
export default defineConfig({ plugins: [netjes({ basePath: '/docs' })] });
```

Links, forms, redirects and `router.push` take paths as if the app were at the root, and Netjes adds the base; see [routing](routing.md).
