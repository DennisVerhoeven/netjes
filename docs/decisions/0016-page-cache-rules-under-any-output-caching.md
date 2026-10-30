# 0016 – Page cache rules under any output caching

Status: accepted

## Context

An output-cached page needs rules that ASP.NET Core's output cache does not know. A document and a client payload at one URL must not share an entry. A payload must be keyed by the layouts the client holds, since it leaves out those it reuses. Entries need tags for `RevalidatePath`, and the page rendered for a failed form post must stay out of the cache.

Apps can output-cache a page in many ways: `CachePage`, `.CacheOutput()` plain, named or built, an `[OutputCache]` attribute, conventions on `MapPages()`, or a base policy. Keyed by ASP.NET Core's rules alone, such a page goes wrong. A stored payload answers a document request at its URL, and a payload that reuses a layout reaches a client that lacks that layout. A failed form post gets the stored page instead of its errors. A stored response carries the antiforgery token of the visitor it was rendered for, so every visitor it is served to gets that token. And during a rolling deploy, instances on two builds that share a store would serve each other's documents and payloads.

## Decision

- `AddNetjes` registers `PageCachePolicy` as an output-cache base policy. It applies the rules to every page endpoint, and to the endpoints `CachePage` marks, whatever policy caches them. A base policy runs before the endpoint's own, and ASP.NET Core's default policy then turns lookup and storage back on, so the rules only add to the key, and refuse storage once the response is known. Requests that must never share an entry, such as a page for a failed form post or a draft-mode request, get a key value of their own: nothing is ever stored under it, and the output cache's lock does not make such requests wait for each other.
- Page entries are keyed on the path and query string as sent, which overrides a narrower `SetVaryByQuery`: the payload repeats its URL, which the client shows, and loaders can bind any query value. A payload is also keyed by which of the page's layout segments the client holds, and one that reuses a segment by a fingerprint of `SegmentKeySecret` (never the secret itself).
- Every page entry is keyed by the client build (`IAssetResolver.Version`).
- `CachePage` pages never carry an antiforgery token. Under other output caching, a response the cache may still store carries none either; the renderer decides this after its own storage refusals. A response the cache will not store (for a signed-in user, a request with an `Authorization` header, or a failed form post) keeps its token.
- An endpoint that is no page but renders one, such as a minimal API returning `Page.Render`, is not stored under output caching other than `CachePage`. Netjes logs a warning once per endpoint, for a 200.

## Alternatives considered

- **Refuse output caching of pages other than `CachePage` at startup.** A startup check cannot see base policies, which apply per request, and it would rule out `.CacheOutput()` on a page loader, a natural thing for an ASP.NET Core developer to write.
- **Respect the app's query vary.** One visitor's query string, in the payload's URL, would reach the next visitor.
- **Refuse storage whenever a token is present.** Every page carries one unless it is left out, so pages would in effect never be stored.
- **Apply the rules to every output-cached endpoint.** That would change the keys of the app's own APIs (case-sensitive paths, the build).

## Consequences

- Forms on pages the output cache stores need JavaScript, as on `CachePage` pages: the client fetches a token before it posts.
- A narrower `SetVaryByQuery` does not cap how many entries one page has; the output cache's `SizeLimit` bounds the store.
- A new build starts with an empty cache. Instances on the same build share entries.
- A minimal API that renders a page needs `CachePage` to be cached.
- With `UseOutputCache()` before `UseNetjes()`, the output cache runs before routing, so no endpoint policy applies: pages are not stored, and Netjes logs a warning.
- A page whose documents carry a CSP nonce is rendered each time rather than stored, with a warning, because a stored copy would give every visitor the same nonce. Cached pages use a hash-based policy instead.
- A stored render that read a request cookie logs a warning once per page, since that one visitor's value reaches every visitor the entry is served to.
- Not covered: a request culture that varies per visitor (an `Accept-Language` or cookie provider) is not part of the key. A policy built without ASP.NET Core's default policy also loses that policy's storage vetoes for responses that set a cookie and for signed-in users, and Netjes does not add them back.
