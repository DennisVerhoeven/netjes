# 0047 – Cached pages name the query keys they read

Status: accepted; extends [ADR 0016](0016-page-cache-rules-under-any-output-caching.md)

## Context

[ADR 0016](0016-page-cache-rules-under-any-output-caching.md) keys a page's entry on the query string as sent, because the payload repeats its URL, which the client router shows, and because a loader can bind any query value. So each query string is an entry, and a page had no way to say that most of them change nothing. A link tagged `?utm_source=feed`, a made-up `?a=1` and an empty `?&&` each rendered and stored another copy of the page, bounded only by the output cache's `SizeLimit`. `SetVaryByQuery` cannot narrow that, since the key already holds the whole query string, and must not, since it would serve one visitor's URL to another. The parsed query does not show the whole problem either: the parser drops empty segments, so `?`, `?&` and `?&&` have no keys, yet each is a query string of its own and an entry of its own.

## Decision

- **A public convention, `.CacheQueryKeys(params string[] keys)`**, on any endpoint or group. It is a page rule of its own, not a setting of `CachePage`: it takes effect wherever the page rules apply, on a page under any output caching and on an endpoint `CachePage` is put on. Its metadata stays internal. Of two calls on one endpoint, the last decides.
- **A request stays cached only when every segment of its raw query string has a name in the list.** A name is the text before the first `=`, decoded as the query parser decodes it (`+` as a space, then percent-escapes) and compared without regard to case. An empty segment (`?`, `?&`, `?page=2&`, `?a=1&&b=2`), a segment without a name (`?=1`) and another name all leave the request out. A request without a query string stays cached. `.CacheQueryKeys()` leaves out every query string.
- **ADR 0016 still holds.** Stored entries stay keyed on the query string as sent: `?page=2` and `?PAGE=2` are two entries, and an entry only ever answers the query string it was stored for.
- **A request left out is rendered for itself.** It gets a key value of its own under `netjes-unread-query`, so no lookup finds an entry and the output cache's lock holds no request back. The renderer refuses storage before it builds the payload, so a page rendered for it goes out `no-store, private` unless the page set its own `Cache-Control`, and keeps its antiforgery token under a policy other than `CachePage` (where a visitor's first page sets the antiforgery cookie and gets ASP.NET Core's `no-cache, no-store` instead). A loader's redirect is not stored either, and carries no `Cache-Control`, as on any page. The policy puts no watch on its cookies, so a read logs no cookie warning: a loader reads before the renderer refuses storage, so the refusal alone would not keep it quiet. The policy refuses storage again once the response is known, for a response no renderer wrote. The payload's URL stays as sent.
- **Outside the page rules it does nothing and does not throw**, because route groups mix pages and APIs. Such an endpoint narrows its key with `SetVaryByQuery`.

## Alternatives considered

- **`SetVaryByQuery`'s semantics: serve the stored entry for other keys.** The payload's URL drives the client router, so a visitor would get another query string's URL in the address bar, and one visitor's parameters could reach the next.
- **Next.js's model, where a shared payload does not decide the URL the client shows.** The client router takes the URL it shows from `payload.url`, so it would have to stop trusting it.
- **Reading only the parsed keys (`Request.Query.Keys`).** Empty segments have none, so `?&`, `?&&` and any longer run of them would still each store a copy.
- **Normalising the query string in the key.** The stored entry would show another URL than the one requested.
- **Redirecting to the URL without the other keys.** That drops the tracking parameters the links carry for the page's own analytics to read.
- **A parameter of `CachePage`.** It would not cover the other ways to output-cache a page, which the page rules apply to alike.
- **Inferring the keys from loader bindings.** Components read the query string too, in the browser, where the server cannot see it.
- **Throwing or warning on an endpoint outside the page rules.** Groups mix them, so a throw would rule out `.CacheQueryKeys()` on a group. A route warning can still be added.

## Consequences

- Without the convention nothing changes, and no app has to do anything.
- Requests with other keys render each time, and the output cache keeps none of them. The pages rendered for them go out `no-store`, so no CDN keeps them either; a loader's redirect carries no `Cache-Control`, as on any page. A page with many visits through tagged links pays for a render per visit, which it should weigh before adding the convention; data that is slow to load is cached in the service that loads it.
- A declared key can still fill the cache by value (`?page=1`, `?page=2`) and, where the loader takes it, by repetition (`?page=1&page=1`), bounded by `SizeLimit` as before.
- Reading a name differently from the parser can only cost a hit or store a page for its own URL. Entries stay keyed as sent, so it cannot serve one query string's page for another.
- A loader that binds a key the list leaves out keeps working, but those requests are not cached, and nothing warns about it yet. A route warning could share the binding helper the export's warning uses.
