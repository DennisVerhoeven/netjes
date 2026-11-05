# 0038 – `netjes/testing` renders a page's document from its props

Status: accepted

## Context

A test that wants a page's whole document, to check its head or the inline scripts a Content-Security-Policy has to allow, had one way to get it: `render` from `netjes/server-entry`, the entry point the Node sidecar calls. That takes the payload the server builds, a format internal to Netjes (the page, the segments that wrap it, the shared props, the document's assets and more), which the test then wrote by hand and a new version could change. Only the server knew which segments wrap a page: which folders the payload names, with which layout, template, loading and error files, and how many URL segments each layout covers.

## Decision

- `renderDocument(page, options)` in `netjes/testing` renders the document of a page file from what the test passes: the props, each layout's props by the layout's file, the shared props, the route values, the URL, the status, a nonce, the `lang` and the public config. It builds the payload itself, renders it with the sidecar's own renderer, waiting for every Suspense boundary as for a crawler, and resolves with the HTML. `inlineScriptHashes(html)` lists the hashes of the inline scripts in it that a browser runs.
- It builds the payload from the pages map the `netjes()` plugin provides, by the server's rules: for a page, the route from the file's folders without route groups; a segment for each folder on the way that has a layout, template, loading, error, not-found, forbidden or unauthorized file, route groups included; for a layout, the URL segments its folder covers, a catch-all counting the values it is given; for a boundary file its kind and status and the segments down to its own folder, as the server renders it in place of a page in or below that folder, and for an error file no error boundary of its own folder around it. The plugin's pages module also exports `appDir`, which segment ids and routes are relative to.
- The rules now live in C# (`RouteTable`, `PageRenderer`) and in TypeScript. A fixture, `packages/netjes/test/fixtures/segments/cases.json`, holds cases of what the server sends. `SegmentFixtureTests` checks the server against it and `render-document.test.tsx` checks the TypeScript copy, so a change to either side fails a suite until the fixture says what both send.
- A Promise in the props is a `Page.Defer` value, delivered as the sidecar delivers one: a resolved value renders in place, a rejected one leaves its Suspense fallback with the error's message for the browser, and a pending one keeps the render waiting until the test's `signal` stops it.
- What it cannot render as the server would, it refuses: a page in or below a folder with a parallel route slot, since the server chooses what each slot renders, a file that is neither a page nor a boundary file, and a DOM environment, where a form's server action, the query client and the router's first URL take their browser path. `netjes/server-entry` and the payload stay internal.

## Alternatives considered

- **Make `netjes/server-entry` and its payload public.** The rules would stay in C# alone, but every field of the payload would become API, and each test would still write the segments by hand, which is where such a test goes wrong.
- **A C# helper that builds the payload and renders it.** One copy of the rules, but an xUnit test would then need Node, and a Vitest test a running app.
- **Document how to write a payload.** It would go out of date unnoticed, and the segments, which decide what the layouts render, would still be written by hand.
- **Leave the check to `CSP_SCRIPT_HASHES`.** It covers Netjes's own inline scripts, not those the app's pages and layouts render.
- **Refuse Promises in the props.** A page with `Page.Defer` values could not be rendered at all.
- **The same cases in the C# and the TypeScript tests, without a shared file.** Nothing would keep the two lists the same.
- **The server entry's `render`, which also returns the whole document.** It writes no script that hands a `Page.Defer` value to the browser, while the document the sidecar sends a crawler, which `renderToStream` renders once everything is ready, has one for each value.

## Consequences

- A test can check what a page's document holds and which inline scripts it runs, the app's own pages and layouts included, and a new app starts with such a test for its home page, `app/document.test.ts`.
- The document is a crawler's, not a visitor's, and holds only what the test passes: no stylesheets or module scripts from the build, no development styles, no query entries seeded with `.WithQuery`, no metadata from C#, no antiforgery token, locale, draft mode, base path or image settings, and every Suspense boundary in place.
- A boundary file has no route in the payload, where the server sends the route of the page it renders in place of. A section's own forbidden or unauthorized file renders inside the section's layout, which the server leaves out for a user who fails the section's layout loader's authorization.
- A change to which segments the server sends, or a new kind of segment file, needs a case in the fixture and the same change to the TypeScript copy.
- A test whose page waits on a Promise that never settles, and that passes no signal, runs into Vitest's timeout.
- A page with parallel route slots cannot be rendered this way.
