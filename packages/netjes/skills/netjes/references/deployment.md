# Deployment

A Netjes app is an ASP.NET Core app that also needs Node at runtime: the .NET process starts and supervises the Node sidecar that renders pages (`NetjesOptions.NodeExecutable`, default `node` on the `PATH`). Only `netjes export` output runs without either.

## Build and publish

```bash
npx netjes generate --check   # in CI: the generated types match the C# types
npm test && npm run typecheck && dotnet build
dotnet test tests/MyApp.Tests -c Release   # the C# tests, where the app has them; a plain dotnet test runs none
dotnet publish -c Release -o bin/publish   # runs `npm run build`, ships dist/ (client, server bundle, sidecar)
cd bin/publish && ASPNETCORE_ENVIRONMENT=Production dotnet MyApp.dll
```

`npx netjes start` runs the app in Production from the source tree (or `--dll bin/publish/MyApp.dll`, which runs from the publish folder). In production the routes come from the built client manifest, so a page file that was not built does not exist.

For these startup failures the app logs one critical entry, saying what is wrong and what to do, and exits with code 1: no build in Production (`npm run build`), a `node` it cannot start (`Netjes:NodeExecutable`), a sidecar script that is not installed or built (`npm install` in development, `npm run build` in production), or in development the Vite dev server's port taken, an error in `vite.config.ts` (with Vite's code frame) or a package it imports that is not installed (`npm install`). Under `WebApplicationFactory` the start throws that message instead. Other exceptions at startup, such as a route conflict, keep their stack. An exception that nothing catches, at startup or on a thread of the app's own, ends it as a .NET crash does: an abort, exit code 134 on Linux and macOS. ASP.NET Core catches a request's exceptions (a page's renders its `error.tsx`), and the host catches one from a `BackgroundService` and stops the app with exit code 0.

## Docker

`netjes new` writes a `Dockerfile` and Compose files:

- `docker compose up`: development, with `dotnet watch` and Vite HMR, ports bound to localhost.
- `docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d`: the production image, a non-root user on a read-only filesystem, with a health check on `/healthz` and Data Protection keys in a named volume.
- Both publish the app on host port 8080; `APP_PORT` (in the shell or a `.env` file) moves it. Vite's 5173 stays.
- The production image runs `tini -s -- dotnet MyApp.dll`: tini is PID 1, passes `docker stop`'s SIGTERM on, and exits with the app's code, so a crash ends the container (134) and `restart: always` acts. When .NET itself is a container's PID 1, its abort does not end it. On arm64 it spins at 100% CPU and never exits; after a crash later than startup the rest of the app keeps serving and `/healthz` still passes. On x86-64 under emulation it has been seen to exit after a few seconds, with 139 instead of 134. An image of the app's own, or one from an older template, needs an init too: the template's `tini` stage, its `COPY` and the `ENTRYPOINT`, or the host's (`docker run --init`, Compose `init: true`, ECS `initProcessEnabled`).

The image restores Netjes from NuGet and npm, so the app must reference the packages, as an app made with `npx netjes new` does (from a Netjes checkout, pass `--packages`).

## Production settings

- `AllowedHosts`: pin it to the site's host names. `appsettings.Development.json` already limits Development to loopback names.
- HTTPS: outside Development the template calls `UseHsts()` and `UseHttpsRedirection()`. Behind a proxy that terminates TLS, call `UseForwardedHeaders()` first, trusting only that proxy's network (`KnownIPNetworks`), so the app sees https and the visitor's address.
- Data Protection keys sign antiforgery tokens and draft-mode cookies: keep them across restarts (the production compose file mounts a volume) and share them between instances.
- Several instances behind a load balancer also share the output-cache store and need the same `Netjes:SegmentKeySecret` (32 bytes or more).
- `Netjes:SidecarProcesses`: Node processes rendering in production (default 1; about one per core for CPU-heavy pages).
- If no sidecar is ready (they crashed or stopped answering, and are restarting), documents are sent for the browser to render instead of failing (`ClientRenderFallback`, on by default), and `/healthz` reports Unhealthy. Register the check with `builder.Services.AddHealthChecks().AddNetjes()` and `app.MapHealthChecks("/healthz")`.
- The app restarts its Node processes itself: one that exits, and in production one that answers no health ping within `RenderTimeout` (at least 5 s). A restart policy on the container's exit (`restart: always`) is all the host needs. A custom `Netjes:SidecarScript` must answer `GET /health`, with any status, without reading a request body.
- Output caching: set `SizeLimit`, and see [caching-and-streaming.md](caching-and-streaming.md).
- Security headers: every response through `UseNetjes()` gets `X-Content-Type-Options: nosniff`, so every response needs its real `Content-Type`: a script or stylesheet with a wrong one or none is refused, and a page that an endpoint writes with `Response.WriteAsync` shows as plain text unless the endpoint sets `Response.ContentType = "text/html; charset=utf-8"` first. Every document also gets `Referrer-Policy` and a same-origin framing policy, or the `X-Frame-Options` that the policy's `frame-ancestors` implies, unless the app sets them (`Netjes:SecurityHeaders`). A Content-Security-Policy is off until you set `NetjesOptions.ContentSecurityPolicy`: with `{nonce}` for a nonce-based policy, or without it, with `NetjesContentSecurityPolicy.ScriptHashes` in its `script-src`, for a hash-based one, which output-cached pages need ([caching-and-streaming.md](caching-and-streaming.md)).
- `Netjes:PublicConfig` is sent to the browser as is; secrets go in other configuration.
- Telemetry: add the `Netjes` ActivitySource and Meter (`NetjesTelemetry.Name`) to OpenTelemetry for loader, action and render spans and render durations.

## Startup work and CLI commands

`netjes generate` and `netjes routes` run the app once in command mode, without serving or starting Node. Skip startup work they do not need, such as migrations or seeding:

```csharp
if (app.GetNetjesCommand() is null)
{
    await SeedAsync(app.Services);
}
```

## Static hosting

`npx netjes export` writes the site as static HTML to `out/` for any static host; see [caching-and-streaming.md](caching-and-streaming.md#static-export) for what it needs and refuses.

## More

[Deployment](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/deployment.md) and [Security](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/security.md) in the guides, and every option in [Options](https://github.com/DennisVerhoeven/netjes/blob/main/docs/reference/options.md).
