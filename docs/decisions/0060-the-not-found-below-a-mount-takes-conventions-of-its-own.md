# 0060 – The not-found below a mount takes conventions of its own

Status: accepted; extends [ADR 0049](0049-page-packages-are-mounted-into-the-route-table.md), amends [ADR 0054](0054-layout-loaders-around-404-and-error-pages-are-judged-as-the-middleware-judges-their-pages.md)

## Context

The fallback that `MapPages()` adds answers every unknown URL, below a page package's mount too. It carries no authorization metadata, so `MapPages()`'s conventions never challenge an unknown URL (ADR 0053), and an app's fallback policy covers it as it covers every endpoint without metadata (ADR 0054).

A page package that protects itself with a scheme of its own opens its sign-in pages with `AllowAnonymous()`, so the app's fallback policy never keeps anyone from signing in. Its unknown URLs had no such way out. In an app whose fallback policy asks for a user of the site's scheme, an unknown URL below the mount got the site's challenge, a 401 or a redirect to the site's login. That was the answer for the package's staff, who are signed in through the package's scheme, and for everyone else; only a visitor the site's policy let in got the package's 404. ADR 0054 rejected marking the fallback `AllowAnonymous()`, because that would exempt every unknown URL of the app from its policy.

## Decision

- `ConfigureNotFound(configure)` on a mount gives the unknown URLs below it an endpoint of their own, which carries only the conventions `configure` puts on it. `.ConfigureNotFound(notFound => notFound.AllowAnonymous())` exempts them from the fallback policy, and `RequireAuthorization(...)` there would challenge them.
- The endpoint's route is the mount's URL with a catch-all, `/help/{**not-found:nonfile}`, in the fallback's last order. ASP.NET Core's route precedence ranks its literal and value segments ahead of the fallback's catch-all, and a mount inside another ahead of the outer one. The parameter's name cannot be a prefix value's, which is an identifier.
- It renders as the fallback does: on the mount's chain, each layout loader authorized before any runs (ADRs 0049, 0053 and 0054), for the user [ADR 0059](0059-a-not-founds-layout-loaders-run-for-the-user-their-policies-sign-in.md) describes. The mount's own conventions and `MapPages()`'s do not reach it, as they do not reach the fallback.
- A mount inside one whose not-found has conventions takes those when it has none of its own: its URLs are below the outer mount.
- It throws after `MapPages()`; on a mount at the root URL (`/`, `/(site)`), below which every unknown URL of the app is; on a mount whose URL has no literal segment (`/[tenant]`, `/(site)/[tenant]`), since a value matches any segment, so `/{tenant}/{**not-found:nonfile}` would take the unknown URLs below the app's other mounts and folders too, such as `/staff/nope` below a mount at `/staff` that gave its not-found no conventions, and give them the conventions the app meant for one package; and on a second mount at the same URL whose not-found has conventions, since two endpoints of one route would fail every such request as ambiguous. A literal segment keeps the endpoint to the URLs of the mount's shape, the ones its pages route.
- Without the call nothing changes: the app keeps one fallback endpoint.

## Alternatives considered

- **An anonymous fallback below every mount.** A package that should stay under the app's fallback policy would leave it without the app saying so.
- **A catch-all page of the package's own, behind `AllowAnonymous()`.** It overlaps the package's other pages unless it sits deep enough to miss them. It takes the unknown URLs away from what recognises the fallback, telemetry's "Page not found" among them, and every package writes it again.
- **`MapPages().ConfigureNotFound(...)` for every unknown URL.** That is the app's whole fallback, which ADR 0054 leaves to the fallback policy. On a mount, the package's prefix bounds what the app exempts. One for the app can follow if apps need it.

## Consequences

- `PagePackageBuilder` has one more public member, and the public API listing grows by it.
- Below a mount with one, the not-found is a second endpoint named "Page not found", with its own route template in telemetry and diagnostics.
- A mount at the root URL, or at a URL of values alone, cannot have one; its unknown URLs stay with the fallback.
