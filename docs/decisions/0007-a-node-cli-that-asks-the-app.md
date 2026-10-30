# 0007 – A Node CLI in the npm package, which asks the app itself

Status: accepted

## Context

Netjes needs tooling: scaffolding a new app with the default stack, running it in development and production, writing the generated types in CI, showing the route table and exporting static pages. The route table and the types are defined in C# (`RouteTable`, endpoint metadata, System.Text.Json contracts), so any tool that reports them must run .NET code with the app's own configuration.

## Decision

The npm package has a `netjes` bin: plain Node ESM with no dependencies. A command that needs the C# side runs the app once in command mode (`NETJES_COMMAND=generate`, `routes` or `export`). The app boots as usual, so every `Map...` call and option applies, but it binds only a throwaway loopback port, writes its result and exits with a code. `generate` and `routes` start no Node sidecar; `export` renders every page through one. The command is read from the process environment only, never from configuration, so no appsettings file can turn a normal run into an export.

`netjes new` copies a template from the package. Run from a Netjes checkout, it points the new app at that checkout (a `ProjectReference` and a `file:` dependency); anywhere else, or with `--packages`, at the published packages.

## Alternatives considered

- **A `dotnet` tool.** Natural for .NET developers, but a second thing to install and version next to the npm package every app already has, and scaffolding the npm side from .NET is awkward.
- **A `dotnet new` template.** It covers scaffolding only, and needs a published NuGet package to reference. `netjes new` covers that and also works from a checkout.
- **Reimplementing the route conventions in Node** for `routes`. Faster, but a second implementation of `RouteTable` that could disagree with what ASP.NET Core maps, and it would not know loaders, props types, caching or authorization.

## Consequences

- `generate` and `routes` cost one `dotnet run` (a build and a startup): seconds rather than milliseconds.
- The app's startup code runs in command mode too, and hosted services it registers still start, so they should tolerate a short-lived process. `app.GetNetjesCommand()` returns the command, so startup work a command does not need, such as seeding a database, can be skipped.
- The template ships a `generated/netjes.ts`, kept in step with the generator by a test, so a new app typechecks before its first build.
