# Design decisions

These records explain why Netjes is built the way it is, for contributors and for anyone weighing it against another framework. Each one states a decision, the context that led to it, the alternatives considered and what follows from it. [The architecture page](../architecture.md) describes how the pieces fit together; these records say why they are those pieces.

- [0001 – React renders in a pool of Node sidecars](0001-react-renders-in-node-sidecars.md): supervised Node processes render React, and .NET balances renders between them.
- [0002 – The server owns routing](0002-server-owns-routing.md): routes are matched in C# only, and the client router asks the server for every page.
- [0003 – React renders the whole document](0003-react-renders-the-whole-document.md): React renders `<html>` itself, head and metadata included.
- [0004 – Vite for the dev server and builds](0004-vite-for-dev-server-and-builds.md): Vite serves modules in development and builds the client and server bundles.
- [0005 – A small default stack](0005-a-small-default-stack.md): React, TanStack Query, Tailwind CSS and Vitest, with no other runtime dependencies.
- [0006 – Types and zod schemas generated from the C# contracts](0006-types-and-zod-from-csharp-contracts.md): the running app writes `generated/netjes.ts` from its System.Text.Json contracts.
- [0007 – A Node CLI in the npm package, which asks the app itself](0007-a-node-cli-that-asks-the-app.md): `netjes` runs the app in command mode to report its routes and types.
- [0008 – Image processing is pluggable, SkiaSharp optional](0008-pluggable-image-processing.md): the image endpoint owns the rules, and a separate package resizes.
- [0009 – Public configuration is sent at runtime](0009-public-config-at-runtime.md): `Netjes:PublicConfig` reaches the browser with the first document, not through the build.
- [0010 – The App Router's conventions, with C# layout loaders instead of server components](0010-app-router-with-csharp-layout-loaders.md): `app/` only, and each segment's data comes from C#.
- [0011 – Server actions are C# endpoints with typed references](0011-server-actions-as-csharp-endpoints.md): stable names, antiforgery and origin checks, and forms that work without JavaScript.
- [0012 – Streamed documents, deferred values, and ready boundaries in place](0012-streamed-documents-and-deferred-values.md): documents stream, `Page.Defer` values follow the shell, and ready boundaries need no script.
- [0013 – A client-rendered fallback when no sidecar is ready](0013-client-rendered-fallback.md): .NET sends the payload for the browser to render while Node is down.
- [0014 – Signing in or out clears what Back shows](0014-signing-in-or-out-clears-what-back-shows.md): a cookie change in an action makes the router and the browser forget pages.
- [0015 – Route warnings name what is likely wrong, not everything that could be](0015-route-warnings-name-what-is-likely-wrong.md): startup warnings for query-bound loader parameters and overlapping pages.
- [0016 – Page cache rules under any output caching](0016-page-cache-rules-under-any-output-caching.md): page keys, tags and token rules apply whatever policy caches a page.
- [0017 – A form input without any of its fields is empty, not null](0017-a-form-input-without-fields-is-empty-not-null.md): an action gets an empty `[FromForm]` input, checked like any other, where form binding gives none.
- [0018 – A startup failure that says what to do ends the app in one line](0018-startup-failures-end-in-one-line.md): a missing build, Node.js or Vite dev server is one log entry and exit code 1, and a failed start in a test host.
- [0019 – A forced no-store removes the headers CDNs read first](0019-forced-no-store-removes-cdn-cache-headers.md): drafts, the fallback and the other `no-store` responses go out without `CDN-Cache-Control`, `Surrogate-Control` and the like.
- [0020 – Page actions check their inputs like server actions](0020-page-actions-check-their-inputs-like-server-actions.md): a failed check renders the page again from its loader, and `usePageActionState()` reads it.
- [0021 – Signing in or out reaches the app's other tabs](0021-signing-in-or-out-reaches-the-other-tabs.md): a tab that sees a cookie change tells the others on a `BroadcastChannel`, and they forget and reload the same way.

A decision that a newcomer would not guess, or that was chosen over a real alternative, gets a new record with the next number. An accepted record is not rewritten when the decision changes; a new record supersedes it.
