# 0044 – `beforeInteractive` scripts run before the page's code

Status: accepted; amends [ADR 0022](0022-hydrate-once-the-shell-is-parsed.md)

## Context

`<Script strategy="beforeInteractive">` is meant, as in Next.js, for a script that runs before the page's own code. It called React's `preinit`, the only way a component puts a script into the head, and `preinit` writes `<script async>`: the browser ran it whenever it arrived, in no order with other such scripts or with the page's modules, and since the component skipped its effect for it, `onLoad` and `onError` never fired. Inline code had no server path: it was added to the body after hydration, as `afterInteractive` code is, so code meant for the first paint, such as a theme set on `<html>`, ran after the page had been painted and hydrated. The client entry, an async module in the head, imports the page's modules as soon as it has the payload and hydrates once the shell is parsed ([ADR 0022](0022-hydrate-once-the-shell-is-parsed.md)); nothing ordered a script before either.

Next.js's App Router writes both kinds into the document as a queue (a preload and an entry for an external script, the code for an inline one) that its bootstrap runs before it hydrates, with no promise that inline code runs before the first paint.

## Decision

- `Script` knows whether it renders into the server's document: `useSyncExternalStore` with a client snapshot of `false` and a server snapshot of `true`, kept in a ref, is `true` on the server and while the component hydrates, which uses the server snapshot, and `false` when it mounts in the browser.
- Rendered on the server, and while hydrating and after, a `beforeInteractive` script renders `<meta name="netjes-before-interactive">`, which React hoists into the head.
- Inline code is rendered where the component stands, `<script id nonce data-netjes-script>` with the code as raw HTML, escaped by React's own rule for its scripts: the `s` of `<script` and `</script`, in any case, is written `s` (`S`), which JavaScript reads as the same letter in strings, template literals, regular expressions and names. The code cannot close its element or open another, and the server and the client write the same text. The parser runs it as it reaches it, with the nonce or under the hash of that text.
- An external script is preloaded with its nonce, `integrity` and `crossOrigin`, and rendered where the component stands as an inert `<script type="application/json" data-netjes-script nonce>` that holds its `src` and its attributes as JSON that is safe in HTML.
- `adoptServerScripts(nonce)` takes over the entries in document order. It records an inline one as run. It reads an external one field by field (`src` a string, each attribute a string or `true`, without event handlers or a nonce of its own) and appends a script to the head with `async = false`, which browsers run in the order they were added, and the document's nonce. When the document has a nonce, it takes only entries that carry it. It resolves once each external script has loaded or failed.
- With the marker in the head, which is complete by the time the client entry reads the payload, the entry waits for the end of the shell, takes the entries over and waits for them, then imports the page's modules and hydrates. Without the marker it keeps ADR 0022's order, and takes the entries over just before it hydrates.
- A component that mounts in the browser renders nothing and, after it mounts, adds its script as `afterInteractive` does, unless the document has it already. So a component that remounts after a hydration error does not run its script a second time. A hydrated component's effect finds its script by id or src and calls `onLoad` or `onError`; an entry that streamed in after the others were taken over is taken over then.
- In development Netjes warns about `async`, `defer` and `type="module"` on a `beforeInteractive` script.

## Alternatives considered

- **Everything run by the parser where it stands**, external scripts as blocking `<script src>` elements. They would run in order, before the page's code, but a slow host would stop the parser, and nothing below the script would be painted until it answered.
- **Next.js's queue, run by the client entry**, inline code included. Inline code would then run once the client entry has loaded, not before the first paint, which is when a theme script has to run; and under a hash-based policy, an app would have to list the hash of Netjes's wrapper around its code.
- **Entries taken without a nonce check.** Under `'strict-dynamic'` a script that the trusted client entry adds runs whatever its origin, so markup that reached the page some other way, such as user HTML a sanitizer let through, could carry an entry that names any script. A nonce cannot be guessed by that markup. Without a nonce the policy's list of hosts, or the absence of a policy, decides what an entry can load, as it does for any script, so every entry is taken.
- **Keep `preinit` and change the guide.** The strategy would keep a name that promises what it does not do.
- **Wait for the end of the shell before importing the page's modules on every page.** Every page would lose the overlap ADR 0022 keeps, its modules running while the rest of the shell arrives, for scripts most pages do not have.

## Consequences

- An external `beforeInteractive` script is no longer async in the head. It runs once the shell is parsed, in order with the others, before the page's modules run, and hydration waits for it: a host that does not answer delays the page's interactivity until the browser gives up on it, as in Next.js. Its `onLoad` and `onError` are called.
- Inline code runs where it stands, before what follows it is painted, after the stylesheets before it have loaded and before any external `beforeInteractive` script, so it cannot use one. Under a hash-based policy the app lists the hash of the text as the document carries it, which `inlineScriptHashes` from `netjes/testing` reports.
- A page with such a script runs its own modules once the shell is parsed. `<link rel="modulepreload">` has fetched them already, so only their evaluation waits.
- The order of the external scripts rests on `async = false` for scripts added from code, which jsdom does not model; it was checked in Chromium.
- A `beforeInteractive` script inside a Suspense boundary that arrives after the shell is taken over when its boundary hydrates, not before the page's code; the docs ask for these scripts in the shell.
- Telling the server's document from a render in the browser rests on React using the server snapshot of `useSyncExternalStore` while it hydrates, which React documents.
- The document carries every inline entry the components render, so the same inline code rendered by two components runs twice. After a hydration error React renders the document in the browser and replaces the attributes of `<html>` and `<body>` with its own, and inline code does not run again.
- Netjes's own inline scripts do not change, so `NetjesContentSecurityPolicy.ScriptHashes` and `CSP_SCRIPT_HASHES` keep their value. The entries are data blocks and the marker is a `<meta>`, so neither needs a hash.
