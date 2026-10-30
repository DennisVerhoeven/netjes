# Security policy

## Supported versions

Netjes is at 0.0.x. Only the latest 0.0.x release gets security fixes; upgrade to it before reporting something you found in an older one. This covers the `netjes` npm package and the `Netjes` and `Netjes.Images.SkiaSharp` NuGet packages, which are released at the same version.

## Reporting a vulnerability

Please do not open a public issue, pull request or discussion for a vulnerability.

1. **Preferred:** use GitHub's private vulnerability reporting: <https://github.com/DennisVerhoeven/netjes/security/advisories/new>.
2. **Fallback:** e-mail dennis.verhoeven@proton.me.

Please include:

- the Netjes version (npm and NuGet), the .NET SDK and Node.js versions, and the operating system;
- what you found and what an attacker gains from it;
- the smallest app or request sequence that shows it (a repository, a test, or `curl` commands);
- whether it needs a particular configuration, such as output caching, a CSP, or a reverse proxy;
- anything you already know about a fix.

## What to expect

- An acknowledgement within a week.
- A first assessment, and questions if the report is unclear, after that.
- Coordinated disclosure: we agree on a date with you, fix the issue in a new release, publish a GitHub security advisory, and credit you unless you prefer not to be named.

Netjes is maintained by one person in spare time, so the time to a fix depends on the severity and the size of the change; reports about the defaults and about code that guards a trust boundary come first.

## Security model

What Netjes does by default, so you can tell a bug from a documented limit. The [security guide](docs/guides/security.md), the [options reference](docs/reference/options.md) and [docs/architecture.md](docs/architecture.md) have the details and the settings.

- **Server actions and page actions** require a valid antiforgery token and a same-origin request (`Origin` or `Sec-Fetch-Site`); `Netjes:AllowedActionOrigins` adds origins. A page action can opt out with `.DisableAntiforgery()`, which turns off both checks (for a provider that posts back from its own site); on a server action it turns off the token check only. A failed form never hands back fields that look like secrets (`Netjes:PrivateFormFields`).
- **Server action bodies** are limited to 1 MB by default (`Netjes:ActionBodySizeLimit`). Page actions keep the server's own request size limit.
- **Layout segment keys**, which let the client router skip layouts it already has, are signed with HMAC-SHA256 using `Netjes:SegmentKeySecret` (at least 32 bytes, random per process unless you set one), so a client cannot make up a key for a segment it was not given.
- **The Node sidecar** listens on the loopback interface only, and every request to it must carry a random token generated when the app starts; it does not start without one.
- **The output cache** (`CachePage`, and the same rules under any output-cache policy) keys page entries on the scheme, the host, the path and query string as sent, whether the response is a document or a client payload, the layouts the client holds, and the client build. It does not store responses it cannot safely share: signed-in users, requests with an `Authorization` header, responses that set cookies, non-200 statuses, and documents carrying a CSP nonce. A cached page that reads request cookies is still stored and served to everyone; Netjes logs a warning. Cached pages carry no antiforgery token; forms fetch one first.
- **The image endpoint** accepts only configured widths and qualities, reads local images from `public/` only, fetches remote images only from hosts you list (https only, listed ports, public addresses only, no redirects followed, with a timeout), caps the source size and, with the SkiaSharp processor, the pixel count, bounds concurrency and queue length, and serves SVG only when you turn it on, sandboxed.
- **Security headers** on rendered documents: `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin` and a same-origin framing policy, unless your app sets those headers itself. Netjes sends every page response the output cache does not store, for visitors and signed-in users alike, with `Cache-Control: no-store, private` unless the page sets its own (the response that sets the antiforgery cookie carries ASP.NET Core's `no-cache, no-store` instead).
- **Content Security Policy** is opt-in: set `Netjes:ContentSecurityPolicy` with `{nonce}` and Netjes sends the header and puts a per-request nonce on the scripts and styles it renders. Netjes does not send a CSP until you configure one.
- **Redirects and static export:** `Page.Redirect` accepts relative and absolute http(s) URLs and rejects encoded schemes, so use `Page.LocalRedirect` for URLs taken from the request; static export does not follow redirects, and writes a redirect only when the page itself issued it and its target is site-relative or http(s).

Out of scope: vulnerabilities in your own loaders and actions, in a reverse proxy or CDN in front of the app, or in a dependency (report those upstream, and tell us if Netjes should pin or work around it).
