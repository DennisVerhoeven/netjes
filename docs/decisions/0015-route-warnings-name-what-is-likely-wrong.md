# 0015 – Route warnings name what is likely wrong, not everything that could be

Status: accepted

## Context

Two route mistakes fail without an error. A loader parameter such as `(string id)` on `/blog/[slug]` binds from the query string, so the slug never reaches it. And two pages can match the same URL, where ASP.NET Core's route precedence picks one without saying so. `RouteDiagnostics` checks for both once the app has started, and `netjes routes` reports the same findings.

A check that warns about everything it could name gets ignored, and a startup log full of warnings about correct code teaches people to hide the category. Many correct apps have a loader with `string? q` next to `/projects/[id]`, where `q` is two edits from `id`, and a static page beside a dynamic one (`/blog/new` next to `/blog/[slug]`) is a convention.

## Decision

- **A loader parameter is flagged only when it binds from the query string while some route parameter is bound by nothing.** A route parameter bound by the loader itself, or by a layout loader above it, counts as bound. Near names (a difference in case only, or an edit distance of two or less) never trigger a warning on their own; they only narrow it. If some query parameters have a near name of an unbound route parameter, the check names only those, one warning each. Otherwise the loader gets one warning listing every query parameter and every unbound route parameter. A correct loader such as `string? q` next to `[id]`, where `id` is bound, starts without a warning.
- **A layout loader parameter is flagged when it binds from the query string under the name of a route parameter that a page below the layout has and the layout's own URL does not.** A layout loader binds only the route values of its own URL ([ADR 0010](0010-app-router-with-csharp-layout-loaders.md)), so a query parameter named after a route value below it is a likely mistake.
- **Two pages that match the same URLs are flagged, unless the winner matches only URLs the other matches too, and the first segment where they differ is a literal in the winner.** That is a static segment carving an exception out of a dynamic one (`/blog/new` beside `/blog/[slug]`, `/about` beside `/[...slug]`). Everything else that overlaps is flagged: `/[id]` and `/[...rest]`, `/docs` and `/docs/[[...slug]]`, `/[lang]/about` and `/[slug]/[page]`. The winner the warning names is taken from the real endpoints: the lower order, then the lower `RoutePattern.InboundPrecedence`.
- **All of these are warnings, never failures.** They are silenced through the log level of `Netjes.Routing.RouteDiagnostics` (`Logging:LogLevel:Netjes.Routing.RouteDiagnostics`). There is no `NetjesOptions` switch.

## Alternatives considered

- **Warn on every near name and every overlap.** It finds more, but most findings would be correct code, and people would silence the whole category in the first week.
- **Fail startup.** A false positive would stop a working app from starting, and an overlap is sometimes what the app wants. Two pages of one shape, which the matcher itself cannot choose between, stay a startup error.
- **A per-kind option or an attribute.** More API to keep, while the log level already gives an all-or-nothing switch, and an attribute would spread the decision over the route files.

## Consequences

- An app whose routes trip one of the rules logs a warning on every start, and `netjes routes` lists the warnings after the table (`warnings` in `--json`). Routing and rendering are unaffected.
- Silencing is all or nothing: an app that keeps one overlap on purpose and raises the category's log level also hides query-binding warnings, warnings for routes added later, and the warning logged when the check cannot run.
- Some real mistakes go unreported: a misspelt query parameter whose route parameter is bound elsewhere, and a loader parameter named unlike its route parameter by more than two edits when another query parameter has the near name.
- The rules are small enough to state in the [routing guide](../guides/routing.md), so a warning that is wrong for an app can be explained and silenced without reading the code.
