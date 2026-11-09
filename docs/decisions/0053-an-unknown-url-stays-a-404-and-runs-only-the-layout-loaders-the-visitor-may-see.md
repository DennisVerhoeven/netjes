# 0053 – An unknown URL stays a 404 and runs only the layout loaders the visitor may see

Status: accepted; extends [ADR 0049](0049-page-packages-are-mounted-into-the-route-table.md)

## Context

`RequireAuthorization()` on a layout loader, on a page package's mount or on `MapPages()` is an endpoint convention, and so is the fallback policy's reach. ASP.NET Core's authorization middleware enforces them on each page endpoint that carries them, before any loader runs. Some pages Netjes renders inside the layouts come from an endpoint that carries none of them, or for a visitor the middleware has not let in:

- the 404 for an unknown URL, which the fallback that `MapPages()` adds answers;
- the status page for an empty 401, 403 or other error status on that fallback or on a server action;
- the not-found and forbidden pages a minimal API asks for with `Page.NotFound()` and `Page.Forbidden()`;
- the error page for an exception on one of those, and the one after the 404 itself fails to render;
- a page's own 401 or 403 page, for the visitor its authorization refused;
- a page's status and error pages from before the authorization middleware ran, or in place of its answer: `PageErrorMiddleware` runs ahead of the app's `UseAuthorization()`, so an empty status or an exception from a middleware in between (a rate limiter's 429, an authentication handler that throws), the 413 it answers itself for a body over the size limit, the 404 `UseNetjesLocalization` answers for an unsupported locale, and a refusal an app answers with an empty 404 to hide the page all render there.

These ran each layout loader on their chain without checking it as the page would, or checked only the loader's own conventions. So a root layout loader with `RequireAuthorization()` sent its props to an anonymous visitor of `/nope`, of a protected page with a body over the limit, or of a page behind a rate limiter that answered 429, in the document and in the client navigation's payload; and an app that protects every page with `MapPages().RequireAuthorization()` or a fallback policy ran its root layout loader for every visitor of an unknown URL and on every 401 page. ADR 0049 closed the first below a mount, by authorizing each loader on the mount's chain before any of them runs.

## Decision

- On those pages, before any layout loader on the chain runs, each one is judged as the authorization middleware would judge a file page in its folder, on everything that page carries at once: the conventions of the layout loaders down to it, its page package's mount's where the folder holds the package's files, and `MapPages()`'s. An `AllowAnonymous()` on any of them lets every visitor in, as on that page; with none that asks for authorization, the fallback policy applies; a policy that names authentication schemes is checked against the user those schemes authenticate, as the middleware's policy evaluator does, without replacing the request's user. A loader then runs only for a visitor the pages below it let in.
- The page stops above the first loader the visitor fails: its boundary renders inside the layouts above, and that loader and those below it never run. Folders without a loader still render, unless the mount refuses the visitor there.
- On a page's own not-found, status and error pages, the page's endpoint is evaluated the same way first. A visitor it lets in gets the page's chain as before, so the check costs nothing on the page path; one it refuses gets the chain judged loader by loader.
- The status stays what it was. An unknown URL is a 404, with no challenge and no redirect to a login page, whatever guards the layouts around the not-found page, so its answer tells a visitor nothing new. A server action's 401 stays a 401, and a rate limiter's 429 a 429.
- The fallback carries none of `MapPages()`'s conventions: the renderer reads them from the data source that `MapPages()` returns.

## Alternatives considered

- **`MapPages()`'s conventions on the fallback endpoint.** Every unknown URL would answer a visitor without a user with a challenge (a 401, or a redirect to the login page), and the answer for a URL would no longer say whether a page is there. Its other conventions, such as rate limiting, CORS and output caching, would reach the fallback too, which changes what apps that authorize nothing see.
- **A challenge where a layout loader refuses the visitor.** An unknown URL would answer differently from one section to the next, and so show where the protected sections are.
- **Each loader's own conventions only, and `MapPages()`'s on the fallback alone.** That kept the root layout around the 401 page of an app whose pages `MapPages().RequireAuthorization()` protects, but ran its loader for every visitor those pages refuse, so a request for any page's URL got what the 404 withholds. Judged apart, `MapPages()`'s authorization also ignored an `AllowAnonymous()` on a mount or a loader, which lets every visitor into the pages.
- **The built-in 404, without any layout, for every visitor a loader refuses.** Simpler, but the not-found page would lose the site's header for every signed-out visitor of an app with one protected layout loader below the root, and below a mount it would differ from what ADR 0049 renders.

## Consequences

- An app that authorizes nothing sees no change: the same payloads, segments and generated files.
- An app whose root layout loader requires authorization shows a visitor it refuses the not-found page, and the status and error pages of endpoints that serve no page, without the root layout. `netjes export` writes its `404.html` as a visitor without a user sees it.
- Under `MapPages().RequireAuthorization()` or a fallback policy, a visitor the pages refuse runs no layout loader, the root layout's included: their 401 page, like their 404, renders without the layouts that have one, so the root `unauthorized.tsx` stands on its own. `AllowAnonymous()` on the root layout's loader would open every page below it, so it is no way to keep the layout; a layout loader with `RequireAuthorization()` below the root protects its section and leaves the layouts above it to every visitor.
- The cost is on those pages only: one authorization check per layout loader on the chain, a few microseconds each, and one for the page's endpoint on its own not-found, status and error pages. The page path is unchanged.
- `MapPages()`'s other conventions still do not reach unknown URLs: an unknown URL is not rate-limited, given CORS headers or output-cached by them.
- `Page.Render(pattern)` from another endpoint is no boundary: it runs the page's layout loaders under the authorization of the endpoint that calls it, which the app gives the authorization the page has.
