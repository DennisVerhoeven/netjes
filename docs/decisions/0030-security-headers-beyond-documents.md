# 0030 – Security headers beyond documents

Status: accepted

## Context

Netjes added its security headers in the renderer, so only the documents it rendered, and the plain error page, had them; the image endpoint set `nosniff` and a sandboxing policy of its own. The rest of what passes through `UseNetjes` went without: client payloads, static files from `public/` and the client build, `robots.txt`, `sitemap.xml`, `/_netjes/antiforgery` and the app's own endpoints. Under a policy such as `script-src 'self'`, any response from the app's origin can be loaded as a script or a stylesheet, and `X-Content-Type-Options: nosniff` is what makes a browser refuse one whose `Content-Type` says otherwise.

With `frame-ancestors` in the policy, Netjes added no `X-Frame-Options`, so as not to contradict it. ASP.NET Core antiforgery adds `X-Frame-Options: SAMEORIGIN` when it stores a new token cookie and finds none, which happens on a visitor's first document when the output cache will not store it. So under `frame-ancestors 'none'`, a browser that knows only `X-Frame-Options` got "same origin" on some documents and nothing on the rest: the header depended on the visitor's cookies and on the cache, never on the policy. `AntiforgeryOptions.SuppressXFrameOptionsHeader` removed antiforgery's header and put nothing in its place. Two policies joined by a comma in one header value were read as one.

## Decision

- A middleware at the start of `UseNetjes`, ahead of the static files, registers a response-start callback that adds `X-Content-Type-Options: nosniff` unless the response has the header by then. Callbacks run in the reverse order of their registration, so it runs after those of the endpoint and of later middleware: a value the app sets is kept, and an answer from the output cache, whose stored headers may carry it already, gets it once. Every response that passes through `UseNetjes` gets it. A response that middleware ahead of `UseNetjes` writes itself does not. `Referrer-Policy` and the framing headers stay on documents.
- When the app sent no `X-Frame-Options` and a policy that goes out (the app's own header, or else `NetjesOptions.ContentSecurityPolicy`) has `frame-ancestors`, Netjes sets the `X-Frame-Options` it implies and adds no `frame-ancestors` itself. A list that holds `'self'`, `*` or a scheme source such as `https:` could let the app's own pages frame it, and gets `SAMEORIGIN`, the closest `X-Frame-Options` comes, since it cannot name other sites. `'none'`, an empty list or other sites only get `DENY`. Every policy counts, in header values of its own or joined by commas, and the strictest decides. `Content-Security-Policy-Report-Only` does not count. Directive names and keywords are compared ignoring case.
- The renderer sets it before it asks antiforgery for the visitor's token, so antiforgery adds none, and every document gets the same value: a first visit, a returning visitor's, the not-found and error pages, and a page from the output cache. An `X-Frame-Options` already on the response is kept: the app's own, or antiforgery's when the app's code stored a token before the render.
- This extends the rule draft mode already follows: a draft with `SecurityHeaders.DraftFrameAncestors` keeps `X-Frame-Options: SAMEORIGIN` next to a `frame-ancestors` that names a CMS. An app's own `frame-ancestors 'self' https://partner.example` now gets `SAMEORIGIN` the same way.
- `SecurityHeaders.Enabled = false` turns all of it off, `nosniff` on every response included. The image endpoint keeps its own `nosniff` and sandboxing policy whatever the switch says: an image opened on its own, an SVG above all, must not run script.

## Alternatives considered

- **Document a middleware that apps add.** Every app would have to know about it, and Netjes already owns the headers on what it serves.
- **`nosniff` on Netjes's own endpoints, payloads and static files only.** Under `script-src 'self'`, the app's endpoints are script sources as much as Netjes's are, and an app that wants another value keeps its own.
- **Set `nosniff` when the request arrives.** A response that is cleared before it starts (`Response.Clear()`, which error handlers call before they write their own answer) would lose it, while a response-start callback still runs for it.
- **Register the callback ahead of the whole pipeline, with an `IStartupFilter`.** It would also reach responses from middleware ahead of `UseNetjes`, which sets headers for the whole app; what goes out there is the app's call, as in [ADR 0019](0019-forced-no-store-removes-cdn-cache-headers.md).
- **Send no `X-Frame-Options` next to `frame-ancestors`, and remove antiforgery's.** Every document would agree, but a browser that knows only `X-Frame-Options` would get no framing protection at all: it fails open.
- **Leave framing as it was.** The header would go on depending on cookies and the cache.
- **`ALLOW-FROM`.** Obsolete: current browsers ignore it.
- **Compare host sources with the request's host**, to send `SAMEORIGIN` for a list that names the app's own origin. Behind a proxy that does not forward the host, the request's host is not the one the browser sees.

## Consequences

- A script or stylesheet served with the wrong `Content-Type`, or with none, from `public/` or an endpoint, is now refused by the browser, and a page that an endpoint writes without a `Content-Type` shows as plain text, its scripts not run; the fix is the type.
- With `frame-ancestors` in the policy, documents carry `DENY` or `SAMEORIGIN`. A partner named in `frame-ancestors` can no longer frame the app in a browser that knows only `X-Frame-Options`, as draft mode already accepted for a CMS.
- A host source that names the app's own origin without `'self'` gets `DENY`. App code that stores antiforgery tokens before the render gets antiforgery's `SAMEORIGIN` on a first visit, which Netjes keeps, unless `SuppressXFrameOptionsHeader` is set. A policy that a middleware writes in a response-start callback, after the render, is not seen.
- Every request that reaches `UseNetjes` registers one more response-start callback.
