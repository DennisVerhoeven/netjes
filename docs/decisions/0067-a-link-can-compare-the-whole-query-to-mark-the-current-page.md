# 0067 – A link can compare the whole query to mark the current page

Status: accepted; extends [ADR 0024](0024-link-marks-the-current-page-by-path-and-query.md)

## Context

ADR 0024 marks a `<Link>` `aria-current="page"` when the page on screen has its path and the page's values for every query parameter the link names; parameters the link leaves out do not count. It turned down a `match` prop, since an `aria-current` prop already lets a link decide. A list's paging and sort links are where the rule is wrong. On `/orders?page=2`, the Previous link goes to page 1, `/orders`, which leaves out `page`, and it is marked as the page the user is on; so is a sort link that starts over from page 1. The `aria-current={undefined}` that apps pass on those links also takes the mark off the one that is the page on screen, such as the link to page 1 on page 1, and every list of an app has to remember it.

## Decision

- `<Link exactQuery>` compares the page's query parameters as well as the link's: the link is the current page when both have the same names with the same values, decoded, in any order and as often each, compared with their case, as ADR 0024 compares the names the link has.
- Everything else in ADR 0024 stays: the path with one trailing slash ignored, a `#` link or a link with a scheme or host of its own never marked, an `aria-current` prop that wins. Without the prop, the rule is ADR 0024's.

## Alternatives considered

- **The whole query by default.** ADR 0024's reasons stand: a navigation link would lose its mark on `/blog?page=2` or under a `utm_source`, and every app would have to change its links.
- **A `match` prop with named rules** (`'named'`, `'query'`, `'path'`). Only the whole query has a case behind it, and a boolean says that.
- **Leaving it to `aria-current={undefined}`.** It is right for a Clear or All link that is never the page, and wrong for the link that is.
- **Leaving out parameters such as `utm_*` under `exactQuery`.** A list Netjes would have to guess; links that a list builds from the page's own query carry what the page has.

## Consequences

- No app changes. A list's paging and sort links can drop their `aria-current={undefined}` and take `exactQuery`, and keep the mark where it is right.
- A link with `exactQuery` is not marked when the page's URL carries a parameter the link does not, `utm_source` included.
- The rule still reads only the page's path and query, which the server render and hydration share, so the attribute cannot cause a hydration mismatch. A page package's `Link` takes the prop too.
