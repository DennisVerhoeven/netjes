# 0046 – The export writes the GET endpoints marked IncludeInExport

Status: accepted; extends [ADR 0041](0041-the-export-warns-about-loaders-that-read-the-query-string.md)

## Context

`netjes export` wrote the pages, `404.html` and a copy of the client build, nothing else. An app that mapped `MapSitemap` and `MapRobotsTxt` got neither file in its static site, and any other file an endpoint makes, such as `/llms.txt` or a feed, had to be written by a script of the app's own. Copying the sitemap the app serves would not have been right either: it lists every public page, the pages marked `.ExcludeFromExport()` included, which have no file on the static host and answer its 404 there. A `robots.txt` in `public/` was copied as it was, and on the server it hid `MapRobotsTxt`, since `UseNetjes` serves the client build ahead of routing. And both endpoints take the site URL the app passes them, which apps and the docs' example fall back to `http://localhost:5080` for when none is configured.

Next.js writes a route handler into its export when the handler answers `GET` and is marked `dynamic = 'force-static'`, and `sitemap.ts` and `robots.ts` are such handlers.

## Decision

- **A public convention, `.IncludeInExport()`**, on any endpoint. The export requests each marked endpoint that is no page with a `GET` and no query string, after the pages and `404.html`, through the app's own address like a page, and writes the body of a 200 byte for byte at its URL. The headers are not written; a static host sends its own. Any other answer fails the export. `.ExcludeFromExport()` wins over it. It has no overload for route values yet: one call, one file.
- **Rules a file needs, checked before anything is written.** The endpoint answers `GET` (one that names no method answers every one), its route has no parameters, its segments are safe file names, its last segment has a file extension, and it needs no authorization, its own or the fallback policy's. Its path is in no file of the client build, compared as Windows and macOS compare paths. The collision and device-name checks cover every file the export writes, the marker and `404.html` included, and refuse a file where another needs a directory.
- **`MapSitemap` and `MapRobotsTxt` are its first callers.** In an export the sitemap lists the pages the export writes, which the plan hands it (`ExportPlan`) before the first request, minus those marked `.ExcludeFromSitemap()`. Served by the app, it lists what it listed before.
- **A loopback site URL is a warning**, in `Netjes.Export.StaticExportService`, naming the file, the URL and the environment the export runs in, and the files are still written.

## Alternatives considered

- **Copy the sitemap the app serves.** It lists the pages the export leaves out, so the static site's sitemap would send crawlers to its 404 page.
- **Build the sitemap inside the export, without a request.** It would be the only file written that way: `robots.txt` and the app's own files would need a second mechanism, and the sitemap would no longer be what the endpoint answers.
- **An internal marker, used by the sitemap and `robots.txt` only**, made public later with values for routes with parameters. The rules for a file are the same either way, and apps had no way to export `/llms.txt` or a feed meanwhile.
- **Let the client build's file win**, as the copy did. The server answers with that file too, so the endpoint never runs anywhere, and nothing said so.
- **Fail the export on a loopback site URL.** An export made to be checked on the machine that made it, such as an end-to-end test, has nowhere else to point.
- **Docs only.** Every app that maps the two endpoints would have had to exclude pages from the sitemap and write both files by hand.

## Consequences

- An app that maps `MapSitemap` or `MapRobotsTxt` gets `out/sitemap.xml` and `out/robots.txt`. Its export now logs a warning when it falls back to a localhost site URL, until the static host's URL is configured for the environment the export runs in. `.ExcludeFromExport()` on the Map call leaves a file out; `robots.txt` always names the sitemap, so leaving out the sitemap means leaving out `robots.txt` too.
- An app with `public/robots.txt` or `public/sitemap.xml` next to the matching Map call can no longer export until it removes one or excludes the endpoint.
- The export's last line counts files as well as pages: `Exported 7 pages and 2 files to` and the output directory.
- An endpoint that reads the query string is warned about like a page loader ([ADR 0041](0041-the-export-warns-about-loaders-that-read-the-query-string.md)), and one that requires a value there fails the export.
- A route with parameters cannot be exported yet. An overload that lists its values, as `.StaticParams(...)` does for pages, can be added without changing what the parameterless one does.
