# 0036 – zod runs without compiled parsers in the browser

Status: accepted

## Context

The app's generated module, `generated/netjes.ts`, makes zod object schemas as soon as a page module that imports it loads. Unless its `jitless` setting is on, zod 4 compiles an object schema's parser into a function it makes from a string, and tests once beforehand, by making an empty one, whether it may. Under a Content-Security-Policy whose `script-src` leaves out `'unsafe-eval'`, as both policies in the [security guide](../guides/security.md#content-security-policy) do, or that has no `script-src` and a `default-src` without it, the browser refuses that test and reports a `script-src` violation (blocked URI `eval`) on every document: it fires a `securitypolicyviolation` event, and sends a report to the policy's `report-uri` or `report-to` endpoint when it has one. zod catches the error and parses without compiled code from then on, so the page works. Chromium's console shows nothing when the policy has a `script-src`, and only a note that `default-src` was used in its place when it has none. But an app that collects reports gets one from every page load, and a test that watches for violations fails.

The setting has to be made before the first object schema is. In the browser, the only code sure to run before every page module is the client entry, which Netjes owns: an app has no module of its own that runs earlier. zod 4.4.0 and newer keep their settings on a global object, `globalThis.__zod_globalConfig`, which a copy of zod creates as it loads unless it is there already, and read `jitless` from it whenever they make an object schema. zod 4.0 to 4.3 keep their settings inside their own module.

## Decision

- The client entry's first import is a module that imports nothing and sets `jitless` on that global object, creating it when no zod has loaded yet. zod 4.4.0 and newer then neither run the test nor compile in the page, whether they load before that module runs or after.
- It applies in every browser, with or without a Content-Security-Policy, to every zod object schema made in the page (a Web Worker has a global object of its own): page and layout props, `api[...].response.parse(...)`, and the app's own search-param and form schemas.
- Netjes has no setting to keep the compiled parsers in the browser. That is deliberate: every app behaves the same under any policy, a policy a proxy or a CDN adds included, and the cost shows only where one schema parses many objects.
- The server entry does not import the module, so the Node sidecar, and the app's Vitest tests, keep the compiled parsers.

## Alternatives considered

- **A build hook that writes the setting at the start of each chunk that holds zod** (a Rolldown `intro`). It works in builds only, not in development, depends on finding those chunks by their content, and each app would have to turn it on.
- **`z.config({ jitless: true })` in a module the plugin generates.** It would make zod a dependency of the client entry, which an app on another schema library does not install, and the development server would have to be told to pre-bundle it.
- **Let C# decide from the policy it sends**, turning the parsers off only when the policy has no `'unsafe-eval'`. A policy that a proxy or a CDN adds never reaches the app.
- **The statement in the client entry's body.** All of a module's static imports run before its body, so one that made a schema would run first; as the entry's first import, it runs ahead of the others.
- **Docs only**, telling apps to set `jitless` themselves. They have no module of their own that is sure to run before the page modules.

## Consequences

- With zod 4.4.0 or newer, a policy without `'unsafe-eval'` reports nothing for zod, in development, in production and in exported pages. zod 4.0 to 4.3 do not read the global object and still run the test, once per document. The peer range stays `^4.0.0`, and the docs ask for 4.4.0 or newer.
- In the browser, parsing many objects with one schema is slower: a list of 1,000 small objects takes about 0.5 ms, against about 0.09 ms compiled (measured in Node.js 26). A new schema's first parse of one object, such as a page's props, is faster, since nothing is compiled.
- Server rendering keeps zod's compiled parsers, and so do the app's Vitest tests, which therefore parse on another of zod's code paths than the browser does.
- `globalThis.__zod_globalConfig` is not a documented part of zod's API. A zod version that renamed it would bring the reports back; the client entry's test, which reads the setting through `z.config()`, fails then.
- The import has to stay first; the test checks that it runs ahead of the entry's next import. In a production build, the modules the entry shares with page modules, such as React and the router, are in chunks of their own that run before the entry's code; none of them makes a zod schema, and the Blog's end-to-end suite checks its pages for violations in development and production.
