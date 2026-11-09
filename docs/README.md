# Netjes documentation

Every page of the Netjes documentation, with a line on what it covers. Start with the tutorial if you are new; the guides cover one topic each, the reference pages list every API and option, and the architecture page and the design decisions are for people who change Netjes itself.

## Start here

- [Getting started](getting-started.md): a tutorial from `npx netjes new` to an app running in Docker, with a page, a C# loader, a server action and tests, and how to debug the app from an IDE.
- [Coming from Next.js](coming-from-nextjs.md): each App Router concept and its Netjes counterpart, and where the two differ.

## Guides

One topic each, in four groups that follow how an app is made: build its pages, render them, publish the site and run the app.

### Build

- [Routing](guides/routing.md): the `app/` file conventions, dynamic and catch-all segments, one URL per page, parallel and intercepting routes, the client router and its hooks, focus and announcements after a navigation, base paths, locales, endpoints next to pages and route warnings.
- [Data loading](guides/data-loading.md): page and layout loaders, shared props, public configuration, the generated types and schemas, typed URLs for the app's endpoints, and TanStack Query seeded from C#.
- [Actions and forms](guides/actions-and-forms.md): server actions, validation, `<Form>` and page actions, forms without JavaScript, antiforgery, revalidation, signing in and out, uploads and downloads.

### Render

- [Rendering and streaming](guides/rendering-and-streaming.md): the Node processes that render pages, streamed documents, `loading.tsx` and `Page.Defer`, crawlers, the client-rendered fallback, `dynamic()` and error pages.
- [Caching](guides/caching.md): the default `Cache-Control`, output-cached pages and why none is cached in the Development environment, what keys an entry and the query keys a page reads, endpoints that are not pages, revalidation, draft mode and CDNs.
- [Images](guides/images.md): `<Image>`, the image endpoint and its limits, and image processing with SkiaSharp.

### Publish

- [Metadata](guides/metadata.md): titles and head tags from page files and from C#, the sitemap, `robots.txt` and `<Script>`.
- [Static export](guides/static-export.md): `netjes export`, static parameters, what cannot be exported, the sitemap, `robots.txt` and other endpoints as files (`.IncludeInExport()`), and Content-Security-Policy for exported pages.
- [Redirects, rewrites and headers](guides/redirects-rewrites-headers.md): what Next.js configures in `next.config.js`, done with ASP.NET Core.

### Run

- [Security](guides/security.md): the default headers, Content-Security-Policy in production and in Development and the warnings Netjes logs for a policy, zod without compiled parsers in the browser, antiforgery and origin checks, body limits, redirects and the other defaults.
- [Testing](guides/testing.md): component and page tests with Vitest and `netjes/testing`, failing queries and fake timers included, a page's whole document with `renderDocument`, C# tests of loaders and actions and the test project a new app has, Playwright, a new app's suite and waiting for hydration, and the checks for CI.
- [Deployment](guides/deployment.md): publishing, Docker, configuration, several instances behind a load balancer, health checks, telemetry and proxies.
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
- [0022 – The client entry hydrates once the shell is parsed](decisions/0022-hydrate-once-the-shell-is-parsed.md)
- [0023 – Pages are not output-cached in Development](decisions/0023-pages-are-not-output-cached-in-development.md)
- [0024 – Link marks the current page by its path and the query parameters it names](decisions/0024-link-marks-the-current-page-by-path-and-query.md)
- [0025 – A mark on `<html>` says the client router runs](decisions/0025-a-mark-on-html-says-the-client-router-runs.md)
- [0026 – Netjes's own `<html>` and `<body>` skip React's attribute check](decisions/0026-netjes-html-and-body-skip-reacts-attribute-check.md)
- [0027 – Testing Library advances Vitest's fake timers through a jest global that exists only while they are on](decisions/0027-testing-library-advances-vitest-fake-timers.md)
- [0028 – The test harness starts from the app's query defaults and keeps its retries, without the waits](decisions/0028-test-harness-starts-from-the-apps-query-defaults.md)
- [0029 – An endpoint that ties with a page is a route warning](decisions/0029-an-endpoint-that-ties-with-a-page-is-a-route-warning.md)
- [0030 – Security headers beyond documents](decisions/0030-security-headers-beyond-documents.md)
- [0031 – A sidecar that stops answering is restarted like one that crashed](decisions/0031-a-sidecar-that-stops-answering-is-restarted.md)
- [0032 – The release links the package READMEs to its tag](decisions/0032-release-links-package-readmes-to-its-tag.md)
- [0033 – Client navigations announce the page and start the focus over](decisions/0033-client-navigations-announce-the-page-and-start-the-focus-over.md)
- [0034 – Navigation promises resolve when React has the page](decisions/0034-navigation-promises-resolve-when-react-has-the-page.md)
- [0035 – Development styles are inlined from the client environment's build](decisions/0035-development-styles-are-inlined-from-the-client-build.md)
- [0036 – zod runs without compiled parsers in the browser](decisions/0036-zod-runs-without-compiled-parsers-in-the-browser.md)
- [0037 – Netjes keeps an app's Content-Security-Policy as written in Development, and warns](decisions/0037-csp-kept-as-written-in-development-with-warnings.md)
- [0038 – `netjes/testing` renders a page's document from its props](decisions/0038-netjes-testing-renders-a-pages-document.md)
- [0039 – A page URL has one spelling](decisions/0039-a-page-url-has-one-spelling.md)
- [0040 – Links from C# are a Href type](decisions/0040-links-from-csharp-are-a-href-type.md)
- [0041 – The export warns about loaders that read the query string](decisions/0041-the-export-warns-about-loaders-that-read-the-query-string.md)
- [0042 – The app's image runs an init as PID 1, so a crash ends the container](decisions/0042-the-apps-image-runs-an-init-as-pid-1.md)
- [0043 – Netjes marks the errors whose message it withheld](decisions/0043-netjes-marks-the-errors-whose-message-it-withheld.md)
- [0044 – `beforeInteractive` scripts run before the page's code](decisions/0044-before-interactive-scripts-run-before-the-pages-code.md)
- [0045 – Typed URLs for the app's endpoints](decisions/0045-typed-urls-for-the-apps-endpoints.md)
- [0046 – The export writes the GET endpoints marked IncludeInExport](decisions/0046-the-export-writes-the-get-endpoints-marked-includeinexport.md)
- [0047 – Cached pages name the query keys they read](decisions/0047-cached-pages-name-the-query-keys-they-read.md)
- [0048 – A new app has a C# test project and a Playwright suite, both outside the app's build](decisions/0048-a-new-app-has-a-csharp-test-project-and-a-playwright-suite.md)

## The project

- [Roadmap](roadmap.md): what is planned, and what is left out by design.
- [Changelog](../CHANGELOG.md): what each release contains.
- [Contributing](../CONTRIBUTING.md): how to propose and make a change.
- [Security policy](../SECURITY.md): how to report a vulnerability, and the security model.

## Examples and packages

- [Blog](../examples/Blog/README.md): a blog with a docs section, a guestbook, a JSON API, streaming, images and a static export.
- [TaskBoard](../examples/TaskBoard/README.md): a multi-tenant project tracker on Postgres, with authentication and a production Docker Compose stack.
- [HelpCenter](../examples/HelpCenter/README.md): a page package, a help center that the Blog mounts at `/help` and TaskBoard in its marketing group, with one of its pages replaced.
- [netjes on npm](../packages/netjes/README.md), [Netjes on NuGet](../src/Netjes/README.md) and [Netjes.Images.SkiaSharp](../src/Netjes.Images.SkiaSharp/README.md): what each package holds and how to install it.
