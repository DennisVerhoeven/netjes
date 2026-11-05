# 0037 – Netjes keeps an app's Content-Security-Policy as written in Development, and warns

Status: accepted

## Context

Netjes sends an app's Content-Security-Policy on every document as it is written, `NetjesOptions.ContentSecurityPolicy` or the app's own header, and never compared it with what the document needs. A document needs the inline bootstrap that collects streamed values and its module scripts, and in Development the Vite dev server as well: the scripts come from the dev server's origin (`http://localhost:5173` by default), Vite's client opens its HMR WebSocket there (`ws://localhost:5173`), and the app's CSS comes inline, in `<style>` elements that Netjes writes and Vite's client adds. A policy that refuses any of these fails in the browser alone: the page does not hydrate, the styles do not apply, or edits never arrive, and the console names the refused resource, not the directive that refused it. `'self'` does not match another port, an `http://` source does not match a `ws://` address, and a nonce does not cover connections, so a policy written for production with `connect-src 'self'`, or `default-src 'self'`, refuses the socket in Development, nonce or not. One without a nonce also refuses the dev server's scripts, and with a `style-src` the inlined styles.

## Decision

- The policy goes out as written in every mode. Netjes does not add the dev server's origin, its socket or `'unsafe-inline'` to it.
- `CspChecks` compares each enforced policy on a document with what the document needs: every policy in the `Content-Security-Policy` header that goes out, the app's own or else the option's, several joined by commas included, and no report-only one. `PageRenderer` logs a warning for each problem it finds, naming the directive and what to change: the bootstrap without its hashes, a nonce or inline scripts allowed; `'strict-dynamic'` without a nonce; the module scripts without `'self'` or a source that may name the app; and in Development the dev server's scripts, its socket and the inlined styles. A script list with a nonce is not judged, since Netjes puts the nonce on every script it writes.
- Sources are matched as browsers match them, and a source that may allow what the document needs counts. For the app's own scripts, a host source counts whatever host it names. The socket is taken to be on the dev server's origin, since `server.hmr` in the Vite config is out of .NET's sight, and the warning says to check it.
- Each policy is checked once, by its text with its nonces taken out, for at most about 100 texts, and not under a command such as the static export, whose files go out under the static host's policy.

## Alternatives considered

- **Widen the policy in Development** with the dev server's origin, its socket and `'unsafe-inline'`. Every directive that could govern them would need rewriting (`default-src`, and `script-src-elem` or `style-src-elem` when there is one), the app would not try in Development the policy it sends in production, and `'unsafe-inline'` would do nothing next to the nonce Netjes puts on the inlined styles.
- **Send the policy as report-only in Development.** Nothing would be refused, but the console would fill with a report for everything the policy does not allow, the dev server's modules included, and what the policy refuses in production would no longer show.
- **Docs only.** The guide now says what a policy needs in Development, but the browser's message does not say which directive applied (`default-src` when there is no `connect-src`, `script-src-elem` next to `script-src`), and the cause was found by reading the policy against the browser's rules.
- **Match host sources against the request's host**, to catch a list that names only other hosts. Behind a proxy that forwards only the client's address and scheme, as the deployment guide sets it up, the request's host is not the one the browser sees, and such an app would be warned about a policy that works.

## Consequences

- An app whose policy refuses what Netjes or the dev server needs sees one warning per problem at the first document under that policy, with the hashes or the address to add. What goes out does not change.
- The checks lean towards silence: an unusual but valid policy can still be warned about, a list of hosts without the app's own is not, and a socket that `server.hmr` moves is judged at the dev server's address.
- An app that writes another policy for each request, with hashes that change each time say, has only about the first 100 of them checked.
