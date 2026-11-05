# 0039 – A page URL has one spelling

Status: accepted

## Context

ASP.NET Core's matcher matches literal segments in any case and ignores one trailing slash. So `app/about/page.tsx` answered `/about`, `/About`, `/ABOUT` and `/about/`, and `MapRobotsTxt` and `MapSitemap` answered `/ROBOTS.TXT` and `/Sitemap.xml` in the same way. Each spelling was a page of its own to whatever keys on the URL. Crawlers could index one page several times. The output cache keys page entries on the path as sent ([ADR 0016](0016-page-cache-rules-under-any-output-caching.md)), because route values reach loaders as sent, so it stored the page once per spelling. `<Link>` compares paths in their case, so on `/About` no navigation link was marked current. An app could check the spelling in each page's loader and redirect, but nothing covered `robots.txt` and the sitemap. Next.js matches a route in its file's case only, so another case is a 404 there, and by default it redirects `/about/` to `/about`.

## Decision

- A middleware at the end of `UseNetjes`, after routing and ahead of authentication, the output cache and the endpoint, answers a `GET` or `HEAD` for a page in another case of the literal segments its file names with a 308 to the file's spelling, and a client navigation with the redirect payload the router follows. `NetjesOptions.RedirectLiteralCase`, true by default, turns it on and off.
- `NetjesOptions.TrailingSlash` decides about a trailing slash. `Ignore`, the default, answers both forms as before; `Never` redirects `/about/` to `/about`, and a catch-all's value loses its last slash. `/` stays as it is.
- `/robots.txt` and `/sitemap.xml` follow both settings, with a plain 308. Other endpoints, the not-found page and other methods are left alone.
- Only the literal segments change. Route values, the query string and the base path go along as sent, and so does a trailing slash under `Ignore`.
- It redirects only the URL the client sent. The request target (`IHttpRequestFeature.RawTarget`) must decode to the base path and the path routing matched. A request a rewrite changed, one run again (for a failed form post, a status code page or an exception handler), one whose path the server normalised, and one on a server that passes on no target, such as TestServer, get no redirect. The location is built from the target, so a value goes back encoded as it came.
- No location is sent that a client could read as another site or another page: one that starts with `//` or `/\`, as sent or decoded, or holds a backslash, a `#`, a control character or a character beyond ASCII. Such a request is answered as it was sent.
- The case and the slash are two settings, because their risks differ: the case redirect keeps every old link working, while dropping a slash loops behind a proxy that adds one. `TrailingSlash` is an enum so that a value that always adds the slash can follow.

## Alternatives considered

- **One setting for both.** It would turn on the riskier slash redirect with the safe case redirect, or leave both off.
- **`Always` as well, now.** A URL that must end in a slash needs the static export to write its files for that spelling and `<Link>` to write it, which are changes of their own.
- **The location from the decoded path (`Request.Path`).** It would redirect a rewritten or re-executed request to the path only the server saw, and re-encode values: `%2541` arrives as `%41`, goes out as `%41`, and the next request decodes it to `A`.
- **A 404 for another case**, as Next.js answers. Links and bookmarks in another case, which worked, would break.
- **Lower-casing the whole path.** Route values would change: a loader that looks up `Hello-World` would get `hello-world`.
- **Documentation only.** Every page would repeat the same loader check, and `robots.txt` and the sitemap would stay uncovered.
- **A convention per endpoint.** Every app would add it to every page, for what pages want by default.
- **URL Rewriting rules.** A rule sees the path, not which of its segments are literals and which are values, so it cannot change the case of one without the other.

## Consequences

- With the defaults, `/About` answers 308 to `/about`, `/About/` to `/about/` and `/ROBOTS.TXT` to `/robots.txt`. A client navigation shows the file's spelling, and the output cache never stores another case. Trailing slashes answer as before.
- `RedirectLiteralCase = false` answers every case as before.
- Browsers keep a 308 and follow it again without asking. A folder renamed to another case sends a visitor who followed the old redirect round in a loop until the browser forgets it. Behind a proxy or CDN that adds a trailing slash, `Never` loops at once.
- Tests on TestServer see no redirect, unlike the app on Kestrel. A test middleware can set the request target, and end-to-end tests see what visitors do.
- The base path keeps the case it was sent in, and route values keep theirs: under a `[lang]` folder, `/EN/about` is still a URL of its own next to `/en/about`.
- Every `GET` or `HEAD` for a page compares its path with the route's literals, without allocating; only a request in another spelling has its target read and a location built.
