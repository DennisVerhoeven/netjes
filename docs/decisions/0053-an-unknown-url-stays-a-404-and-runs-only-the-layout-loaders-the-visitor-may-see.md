# 0053 – An unknown URL stays a 404 and runs only the layout loaders the visitor may see

Status: accepted; extends [ADR 0049](0049-page-packages-are-mounted-into-the-route-table.md)

## Context

`RequireAuthorization()` on a layout loader, on a page package's mount or on `MapPages()` is an endpoint convention. ASP.NET Core's authorization middleware enforces it on each page endpoint that carries it, before any loader runs. Some pages Netjes renders come from an endpoint that carries none of it:

- the 404 for an unknown URL, which the fallback that `MapPages()` adds answers;
- the status page for an empty 401, 403 or other error status on that fallback or on a server action;
- the not-found and forbidden pages a minimal API asks for with `Page.NotFound()` and `Page.Forbidden()`;
- the error page for an exception on one of those, and the one after the 404 itself fails to render;
- the 404 that `UseNetjesLocalization` answers for an unsupported locale on a page's URL, before the authorization middleware has run when the app calls it first.

These render inside the root layout, or below a page package's mount inside the layouts down to the mount, and they ran each layout loader on that chain without checking its conventions. So a root layout loader with `RequireAuthorization()` sent its props to an anonymous visitor of `/nope`, in the document and in the client navigation's payload, and an app that protects every page with `MapPages().RequireAuthorization()` ran its root layout loader for every visitor of an unknown URL. ADR 0049 closed the first below a mount, by authorizing each loader on the mount's chain before any of them runs.

## Decision

- On those pages, each layout loader on the chain is authorized with its own conventions, evaluated as the authorization middleware would for an endpoint carrying them, before any loader on the chain runs. The page stops above the first one the visitor fails: its boundary renders inside the layouts above, and that loader and those below it never run.
- The status stays what it was. An unknown URL is a 404, with no challenge and no redirect to a login page, whatever guards the layouts around the not-found page, so its answer tells a visitor nothing new. A server action's 401 stays a 401.
- The fallback is one of `MapPages()`'s endpoints, so the authorization that `MapPages()`'s conventions add counts on its chain too, at the root and below a mount: a visitor who fails it gets no layout loader run, and the chain stops above the first folder with one. Folders without a loader still render. The fallback keeps those conventions in a marker of its own, unapplied.
- The 404 for an unsupported locale checks the page endpoint's own authorization, which holds the conventions of every layout loader on the page's chain and those of `MapPages()` or the mount: a visitor it refuses gets no layout loader run.
- A page's own endpoint is unchanged: it carries the conventions of every layout loader above it, and the middleware enforces them before the page renders.

## Alternatives considered

- **`MapPages()`'s conventions on the fallback endpoint.** Every unknown URL would answer a visitor without a user with a challenge (a 401, or a redirect to the login page), and the answer for a URL would no longer say whether a page is there. Its other conventions, such as rate limiting, CORS and output caching, would reach the fallback too, which changes what apps that authorize nothing see.
- **A challenge where a layout loader refuses the visitor.** An unknown URL would answer differently from one section to the next, and so show where the protected sections are.
- **The built-in 404, without any layout, for every visitor a loader refuses.** Simpler, but the not-found page would lose the site's header for every signed-out visitor of an app with one protected layout loader below the root, and below a mount it would differ from what ADR 0049 renders.

## Consequences

- An app that authorizes nothing sees no change: the same payloads, segments and generated files.
- An app whose root layout loader requires authorization shows a visitor it refuses the not-found page, and the status and error pages of endpoints that serve no page, without the root layout. `netjes export` writes its `404.html` as a visitor without a user sees it.
- The cost is on those pages only: one authorization check per layout loader on the chain, a few microseconds each, and less than one when the app maps no layout loader. The page path is unchanged.
- `MapPages()`'s other conventions still do not reach unknown URLs: an unknown URL is not rate-limited, given CORS headers or output-cached by them.
