# 0019 – A forced no-store removes the headers CDNs read first

Status: accepted

## Context

Some responses go out `Cache-Control: no-store` whatever the page or the app set: draft pages, which show unpublished content, and the redirects loaders return in draft mode, which can lead to it; the client-rendered fallback ([ADR 0013](0013-client-rendered-fallback.md)), which no cache may go on serving once the sidecars are back; the page rendered for a failed form post without JavaScript, which holds what was typed; server action answers; and the antiforgery token from `/_netjes/antiforgery`. Replacing `Cache-Control` is not enough for a CDN or proxy that reads a header of its own ahead of it. A page that sends `CDN-Cache-Control: max-age=600` for Cloudflare, `Surrogate-Control` for Fastly or Varnish, or `X-Accel-Expires` for nginx would have its draft kept for ten minutes and served to every visitor. A header set as the response starts, in a `Response.OnStarting` callback of a loader or a middleware, is set after Netjes's and wins, `Cache-Control` included.

## Decision

- Where Netjes forces `no-store`, it also removes the headers a CDN or proxy reads ahead of `Cache-Control`, in any casing: every header whose name ends in `-Cache-Control` (RFC 9213's `CDN-Cache-Control`, and a CDN's own, such as `Cloudflare-CDN-Cache-Control`), `Surrogate-Control`, `Edge-Control` and `X-Accel-Expires`.
- It sets `Cache-Control` and removes those headers when it renders the response, and once more as the response starts, from a callback that `UseNetjes` registers before routing. Callbacks run in the reverse order of their registration, so this one runs after those of loaders, actions and middleware registered after `UseNetjes`.
- Everywhere else Netjes leaves these headers alone. A page that sends them on its other responses keeps them, and the default `no-store, private` for pages that set no `Cache-Control` replaces nothing.

## Alternatives considered

- **Set them to `no-store` instead of removing them.** A CDN that reads them would hear the same, but Netjes would then send CDN headers, which it otherwise never does, and a CDN that does not know a header passes it on to the browser. Without them, every CDN falls back to `Cache-Control`.
- **Remove only the headers of the best-known CDNs.** Another CDN's own header would still keep a draft. CDNs name theirs after RFC 9213's `CDN-Cache-Control` (`Cloudflare-CDN-Cache-Control`, `Vercel-CDN-Cache-Control`, `Netlify-CDN-Cache-Control`), so the suffix covers them.
- **Leave them, and tell apps to set them only on pages that cannot be drafts.** Every app behind a CDN would need to know, and a page cannot choose when it becomes the fallback.
- **Register the callback ahead of the whole pipeline, with an `IStartupFilter`.** It would also run after callbacks of middleware ahead of `UseNetjes`, at the price of a callback on every request, static files included. Middleware there sets headers for the whole app, and changing what it sets is the app's call.

## Consequences

- Drafts, the fallback and the other responses above go out without those headers, whether a loader or a middleware set them, at once or in a response-start callback.
- The exception is a response-start callback of a middleware ahead of `UseNetjes`: it runs after Netjes's, and what it adds still goes out.
- Every request that gets past the static files registers one response-start callback, which does nothing unless the response was forced `no-store`.
