# 0029 – An endpoint that ties with a page is a route warning

Status: accepted; extends [ADR 0015](0015-route-warnings-name-what-is-likely-wrong.md)

## Context

Apps map their own endpoints next to their pages: an API, a file such as a Markdown copy of each docs page at `/docs/{**path:regex(\.md$)}` next to `app/docs/[...path]/page.tsx`, or Netjes's `MapLocaleRedirect()` for `/`. For a request, ASP.NET Core ranks the endpoints whose routes match the URL by order, then route precedence, then its matcher policies: an endpoint that requires a host, names HTTP methods or accepts only some content types ranks ahead of one that does not. Pages and page actions are endpoints of order 0. When an endpoint of the app ranks the same as a page and a request matches both, the matcher cannot choose and throws an `AmbiguousMatchException`, so the request answers 500. Nothing said so before that request: the route warnings compared pages with pages only ([ADR 0015](0015-route-warnings-name-what-is-likely-wrong.md)), and ASP.NET Core finds a tie only while it matches a request.

## Decision

- `RouteDiagnostics` compares the endpoints of each page, the one that answers GET and HEAD and the page action's POST, with every endpoint that is no page and names an HTTP method. Netjes's own endpoints count, since the locale redirect ties with `app/page.tsx`. The not-found fallback and the server actions carry Netjes's page marker without a page, and are left out. An endpoint that names no method ranks after one that does, so it never ties with a page.
- A pair is warned when the matcher ranks the two the same and a request can fit both: they have an HTTP method in common; the same order and the same route precedence, which means the same number of segments and the same kind of segment at each position; the same literal wherever both have one, in any case; a host in common, or no host on either; and an accepted content type in common, or none on either. A parameter with a required value, as MVC's conventional routes have, counts as that literal, because the matcher matches it as one. Route constraints are not compared: a page's only constraint is a catch-all's `minlength(1)`.
- The warning has the kind `ambiguous-endpoint`. It names the endpoint's methods and route, the page file or its action, and the page's URL, and says what to do: `.WithOrder(-1)` on the endpoint if it should answer those URLs, or another route.
- It is a warning like the others, logged at startup and listed by `netjes routes`, never a reason to stop, and hidden through the log level of `Netjes.Routing.RouteDiagnostics`.

## Alternatives considered

- **Fail startup.** Equal ranks only stand in for an ambiguity: an app's own matcher policies can still tell the two endpoints apart. Endpoints also come from libraries the app does not control. A false positive would stop a working app, which is why [ADR 0015](0015-route-warnings-name-what-is-likely-wrong.md) made route warnings warnings.
- **Warn about every endpoint that shares URLs with a page.** `.WithOrder(-1)`, or a literal route next to a dynamic page, is the intended way for an endpoint to take URLs from a page; most warnings would be about correct apps.
- **Ask the matcher about sample URLs.** Its candidate sets are internal to ASP.NET Core, and a sample URL would have to satisfy each endpoint's route constraints, such as regular expressions.
- **Reuse the check for overlapping pages.** It compares file patterns, which have no constraints, hosts, methods or content types: `/blog/{id:int}` next to `app/blog/[slug]/page.tsx` would be warned, though its constraint ranks the endpoint first.
- **Leave out endpoints with required values.** It would miss a real tie, such as a conventional route's `{controller}/{action}` for `Home` and `About` next to `app/home/about/page.tsx`.
- **Leave out page actions.** A `MapPost` next to a page action that binds no form input ties in the same way.
- **Warn only when the hosts or accepted content types are the same sets.** Two lists that share one value still tie for a request with that value, such as an endpoint that takes a file upload next to a page action that takes a form, for a multipart post.
- **Have the generated `Href` type refuse the URLs an endpoint answers.** A TypeScript template literal type cannot leave out what a regular expression matches.

## Consequences

- An app with such a tie logs a warning on every start, and `netjes routes` lists it (`warnings` in `--json`). The requests it names answered 500 before and still do; routing is unchanged.
- Silencing is all or nothing, as for the other route warnings: raising the category's log level hides them all.
- Some warnings name endpoints that do not fail: the app's own matcher or endpoint selector policies (API versioning, for one) can tell two endpoints of one rank apart, and an endpoint whose route constraint allows nothing a page's catch-all matches never shares a URL with it.
- Some ties go unreported: hosts and content types are compared as written, so a host wildcard (`*.example.com`), a port, or a content type such as `*/*` that meets the other endpoint's value is missed; ties between two endpoints of the app are left to ASP.NET Core; and endpoints added after startup are not seen. A page that ranks ahead of an endpoint for all of its URLs is no tie either, so an endpoint that never answers is not reported: `MapHealthChecks("/healthz")`, which names no method, never answers GET next to `app/healthz/page.tsx`.
- Endpoints from libraries are checked too. `MapHealthChecks` names no method, and `MapOpenApi`'s route has a segment of two parts (`{documentName}.json`), which no page has, so neither ties with a page.
