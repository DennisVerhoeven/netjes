# Netjes documentation

Every page of the Netjes documentation, with a line on what it covers. Start with the tutorial if you are new; the guides cover one topic each, the reference pages list every API and option, and the architecture page and the design decisions are for people who change Netjes itself.

## Start here

- [Getting started](getting-started.md): a tutorial from `npx netjes new` to an app running in Docker, with a page, a C# loader, a server action and tests, and how to debug the app from an IDE.
- [Coming from Next.js](coming-from-nextjs.md): each App Router concept and its Netjes counterpart, and where the two differ.

## Guides

- [Routing](guides/routing.md): the `app/` file conventions, dynamic and catch-all segments, parallel and intercepting routes, the client router and its hooks, base paths, locales and route warnings.
- [Data loading](guides/data-loading.md): page and layout loaders, shared props, public configuration, the generated types and schemas, and TanStack Query seeded from C#.
- [Actions and forms](guides/actions-and-forms.md): server actions, validation, `<Form>` and page actions, forms without JavaScript, antiforgery, revalidation, signing in and out, uploads and downloads.
- [Rendering and streaming](guides/rendering-and-streaming.md): the Node processes that render pages, streamed documents, `loading.tsx` and `Page.Defer`, crawlers, the client-rendered fallback, `dynamic()` and error pages.
- [Caching](guides/caching.md): the default `Cache-Control`, output-cached pages, what keys an entry, revalidation, draft mode and CDNs.
- [Metadata](guides/metadata.md): titles and head tags from page files and from C#, the sitemap, `robots.txt` and `<Script>`.
- [Images](guides/images.md): `<Image>`, the image endpoint and its limits, and image processing with SkiaSharp.
- [Static export](guides/static-export.md): `netjes export`, static parameters, what cannot be exported, and Content-Security-Policy for exported pages.
- [Security](guides/security.md): the default headers, Content-Security-Policy, antiforgery and origin checks, body limits, redirects and the other defaults.
- [Deployment](guides/deployment.md): publishing, Docker, configuration, several instances behind a load balancer, health checks, telemetry and proxies.
- [Testing](guides/testing.md): component and page tests with Vitest and `netjes/testing`, C# tests of loaders and actions, Playwright, and the checks for CI.
- [Redirects, rewrites and headers](guides/redirects-rewrites-headers.md): what Next.js configures in `next.config.js`, done with ASP.NET Core.
- [Coding agents](guides/coding-agents.md): `llms.txt`, the `AGENTS.md` and skill that new apps get, and `netjes agents` for existing ones.

## Reference

- [React API](reference/react-api.md): every export of the `netjes` npm package: components, hooks, functions, `netjes/testing`, `netjes/vitals` and `netjes/zod`.
- [C# API](reference/csharp-api.md): every public type and extension method of the `Netjes` and `Netjes.Images.SkiaSharp` packages.
- [Options](reference/options.md): every `NetjesOptions` property and `Netjes:*` configuration key, with its type, default and effect.
- [The netjes command](reference/cli.md): every command, option, exit code and environment variable of the CLI.
- [Vite plugin](reference/vite-plugin.md): the options of `netjes()`, what it adds to the Vite config and the dev server, and how an app's TypeScript checks against the package's declarations.

## How Netjes works

- [Architecture](architecture.md): how a request, a client navigation and a server action flow through .NET, the Node processes and the browser, and where the code lives.
- [Benchmarks](benchmarks.md): how Netjes is benchmarked, and the results.

### Design decisions

Each decision record says what was decided, the alternatives, and what follows from it. [The decisions index](decisions/README.md) has a line on each.

- [0001 – React renders in a pool of Node sidecars](decisions/0001-react-renders-in-node-sidecars.md)
- [0002 – The server owns routing](decisions/0002-server-owns-routing.md)
- [0003 – React renders the whole document](decisions/0003-react-renders-the-whole-document.md)
- [0004 – Vite for the dev server and builds](decisions/0004-vite-for-dev-server-and-builds.md)
- [0005 – A small default stack](decisions/0005-a-small-default-stack.md)
- [0006 – Types and zod schemas generated from the C# contracts](decisions/0006-types-and-zod-from-csharp-contracts.md)
- [0007 – A Node CLI in the npm package, which asks the app itself](decisions/0007-a-node-cli-that-asks-the-app.md)
- [0008 – Image processing is pluggable, SkiaSharp optional](decisions/0008-pluggable-image-processing.md)
- [0009 – Public configuration is sent at runtime](decisions/0009-public-config-at-runtime.md)
- [0010 – The App Router's conventions, with C# layout loaders instead of server components](decisions/0010-app-router-with-csharp-layout-loaders.md)
- [0011 – Server actions are C# endpoints with typed references](decisions/0011-server-actions-as-csharp-endpoints.md)
- [0012 – Streamed documents, deferred values, and ready boundaries in place](decisions/0012-streamed-documents-and-deferred-values.md)
- [0013 – A client-rendered fallback when no sidecar is ready](decisions/0013-client-rendered-fallback.md)
- [0014 – Signing in or out clears what Back shows](decisions/0014-signing-in-or-out-clears-what-back-shows.md)
- [0015 – Route warnings name what is likely wrong, not everything that could be](decisions/0015-route-warnings-name-what-is-likely-wrong.md)
- [0016 – Page cache rules under any output caching](decisions/0016-page-cache-rules-under-any-output-caching.md)
- [0017 – A form input without any of its fields is empty, not null](decisions/0017-a-form-input-without-fields-is-empty-not-null.md)
- [0018 – A startup failure that says what to do ends the app in one line](decisions/0018-startup-failures-end-in-one-line.md)
- [0019 – A forced no-store removes the headers CDNs read first](decisions/0019-forced-no-store-removes-cdn-cache-headers.md)
- [0020 – Page actions check their inputs like server actions](decisions/0020-page-actions-check-their-inputs-like-server-actions.md)
- [0021 – Signing in or out reaches the app's other tabs](decisions/0021-signing-in-or-out-reaches-the-other-tabs.md)

## The project

- [Roadmap](roadmap.md): what is planned, and what is left out by design.
- [Changelog](../CHANGELOG.md): what each release contains.
- [Contributing](../CONTRIBUTING.md): how to propose and make a change.
- [Security policy](../SECURITY.md): how to report a vulnerability, and the security model.

## Examples and packages

- [Blog](../examples/Blog/README.md): a blog with a docs section, a guestbook, a JSON API, streaming, images and a static export.
- [TaskBoard](../examples/TaskBoard/README.md): a multi-tenant project tracker on Postgres, with authentication and a production Docker Compose stack.
- [netjes on npm](../packages/netjes/README.md), [Netjes on NuGet](../src/Netjes/README.md) and [Netjes.Images.SkiaSharp](../src/Netjes.Images.SkiaSharp/README.md): what each package holds and how to install it.
