# Roadmap

The work in progress, what comes next, what is planned without a date, and what is left out by design. Nothing here is a promise: plans change as people use Netjes and report what they need. To ask for something, or to say that an item matters to you, open a discussion or an issue on GitHub.

## Now

- **The 0.0.1 release.** The packages `netjes` on npm and `Netjes` and `Netjes.Images.SkiaSharp` on NuGet, released together under one version, with the documentation, `llms.txt` and the app template.

## Next

- **Apps without Node in production.** An option to render every page in the browser, from the client-rendered document Netjes already serves while no Node process is ready, so an app runs on .NET alone. Loaders, actions, typed props and client navigation keep working. What goes is what server rendering gives: HTML for crawlers and link previews, the metadata in that HTML, forms that work before the JavaScript loads, and caching of documents (a client-rendered document goes out `no-store`; data payloads can still be cached). It revisits [ADR 0001](decisions/0001-react-renders-in-node-sidecars.md), which chose server rendering, so it starts with a new decision record.
- **Instant loading.** On a client navigation, show the target's `loading.tsx` at once inside the layouts it shares with the current page, then stream in the layout loaders' props and the page as they are ready, instead of waiting for the whole payload.
- **Caching depth.** Stale-while-revalidate for cached pages, cache profiles, tags added by a loader at render time, revalidation by route pattern or layout, and a cached shell that is filled for an anonymous visitor, for pages with a part that differs per visitor.
- **Metadata and images.** The metadata file conventions in `app/` (`icon`, `apple-icon`, `opengraph-image`, `twitter-image`, `manifest`), generated Open Graph images, richer sitemaps and `robots.txt`, `metadataBase` and `viewport`, and more of `<Image>`: `fill`, blur placeholders and static imports.
- **Client navigation in static exports.** Write each page's payload next to its HTML, so exported sites navigate without full page loads, with a trailing-slash option, the sitemap and `robots.txt` in the export, and images resized at export time.
- **Bundle size.** Smaller first-load JavaScript: generated schemas split per route and free of side effects, so bundlers can drop what a page does not use, and `dynamic()` chunks preloaded from the build manifest.
- **Error handling.** An id for each server error, logged with the exception and shown to the user, a way to retry the failed segment from `error.tsx`, and a global error boundary in the browser.
- **Internationalisation.** Redirects from paths without a locale, `hreflang` alternates from the supported cultures, and optional domains per locale.
- **Operations.** Streamed responses that proxies do not buffer, an asset prefix for serving the client build from a CDN, a deployment id, and letting renders in progress finish when a Node process is stopped.

## Later

- Partial prerendering: a cached HTML shell, resumed by React with the parts that differ per request.
- valibot as an alternative to zod in the generated file.
- Navigations that keep what an unmatched parallel-route slot showed, as Next.js does after a soft navigation.
- A `__Host-` antiforgery cookie by default. It is an opt-in today for apps served only over HTTPS: as a default it would break plain-HTTP development and `UsePathBase`.
- `<Link>` treating `/x` and `/x/` as the same page for `aria-current`.
- AVIF output for `<Image>`, from a second image processor.
- Typegen edge cases: item nullability for collection subclasses (`class Tags : List<string?>`), `UnknownDerivedTypeHandling.FallBackToBaseType` in discriminated unions, and read semantics (defaulted properties optional, computed ones left out) for JSON endpoint request schemas, as server action inputs have.

## Not planned

Netjes leaves these out by design. Taking one up would need a new decision record that supersedes the one named.

- React Server Components. C# loaders do their job: data comes from C# as props, and every component ships to the browser ([ADR 0010](decisions/0010-app-router-with-csharp-layout-loaders.md)).
- The pages router, and its hooks for code moving from `pages/`: `useRouter().query`, `asPath`, router events and shallow routing ([ADR 0010](decisions/0010-app-router-with-csharp-layout-loaders.md)).
- Serverless and edge hosts. Pages render in long-lived Node processes next to the .NET app ([ADR 0001](decisions/0001-react-renders-in-node-sidecars.md)).

