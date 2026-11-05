# 0024 – Link marks the current page by its path and the query parameters it names

Status: accepted

## Context

`<Link>` sets `aria-current="page"` on a link to the page on screen. Screen readers announce such a link as the current page, and apps style the current item of a navigation bar with it. Until now it compared the link's path with the current path, byte for byte, and nothing else. On `/`, links to `/#features` and `/#start` were all marked as the current page, and so was `/search?q=other` on `/search?q=cache`. A link to `/docs` was not marked on `/docs/`. In an app under the base path `/base`, a link written with it (`/base/contact`, which `<Link>` leaves as it is) was never marked, because the path it was compared with had no base path.

The server renders the attribute and the browser hydrates it, so whatever the rule is, both must reach the same answer from what both have: the page's path and query. The fragment never reaches the server.

## Decision

A link is the current page when the URL its `href` goes to is the page on screen:

- An `href` with a `#` is never current, `#` and `/#` included: it points to a place in a page, not to the page.
- The `href` is resolved under the base path, against the page's URL, on a fixed origin. An `href` with a scheme or host of its own (`https://…`, `//host/…`, `mailto:`) is never current, even one on the app's own host, which a server render cannot be sure of.
- The paths are compared with one trailing slash removed from each, so `/docs` and `/docs/` are one page, and `/` stays `/`.
- For each query parameter the link names, the page has the same values, decoded, in any order and as often each: `?t=x&t=y` matches `?t=y&t=x` but not `?t=x`, and `q=a+b` matches `q=a%20b`. Parameters the link does not name do not count, so `/blog?tag=react` stays current on `/blog?tag=react&utm_source=nl`.
- An `aria-current` prop wins: `undefined` leaves the attribute off, and `"true"` marks a link to a section on the pages below it.

## Alternatives considered

- **The path only**, the earlier rule. React Router's `NavLink` also ignores the query and the fragment. Every fragment link and every filter link on a page is then the current page.
- **The same query string, exactly.** A link loses its mark as soon as the page's URL carries a parameter the link does not set, such as `utm_source`, and a navigation link without a query, such as Blog, would need a special case to stay current on `/blog?page=2`.
- **The same URL, fragment included.** The server never sees the fragment, so the server render and hydration would disagree, and a link to `/#features` would only become current once the visitor followed it.
- **A `match` prop**, for the app to choose a rule per link. It is more API for what an `aria-current` prop already does.
- **No `aria-current` at all**, as with Next.js's `next/link`. Apps that style the current link with it would lose that.

## Consequences

- Fragment links lose the attribute. A link with a query keeps it only while the page has the values the link names. `/x` and `/x/` count as one page, and a link whose `href` starts with the base path is marked on its page.
- A link that names fewer parameters than the page is still current: an "All" filter (`/blog`) on `/blog?tag=react`, or a `?page=2` link that drops the page's filter. Such links set `aria-current` themselves, as the Blog example's "All" filter does.
- A navigation link that stands for a whole section, such as Docs on every page under `/docs`, is marked on `/docs` only. The [routing guide](../guides/routing.md#the-current-section) shows how to mark the section with `aria-current="true"`.
- The rule reads only the page's path and query, which the server render and hydration share, so the attribute cannot cause a hydration mismatch.
