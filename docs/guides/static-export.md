# Static export

This guide is for app developers who want to publish a Netjes app, or part of one, as static files on a host without .NET: what `netjes export` writes, how dynamic pages list their URLs, what cannot be exported, and how exported pages behave in the browser, Content-Security-Policy included. It is the counterpart of Next.js's `output: 'export'`. The command's options are in the [CLI reference](../reference/cli.md#netjes-export).

## What the export does

```bash
npx netjes export          # builds, then writes out/
npx netjes export site     # into site/ instead
npx netjes export --no-build
```

`netjes export` builds the app (as `netjes build` does), runs it once in Production, and requests every page through it over HTTP: the same pipeline a visitor hits, so loaders, layout loaders, middleware and output caching apply as usual. It writes:

- `out/index.html` for `/`, and `out/<path>/index.html` for every other page,
- `out/404.html`, the not-found page,
- a copy of the client build (`dist/client`, without Vite's `.vite/` folder).

As with Next.js, the loaders run once, at export time. The pages then hydrate in the browser as usual.

## Dynamic pages: StaticParams

A page with route parameters is exported once for each set of values its loader lists with `.StaticParams(...)`, like `generateStaticParams`:

```csharp
app.MapPageLoader("/blog/[slug]", (string slug, PostStore store) => /* ... */)
    .StaticParams<PostStore>(store => store.All.Select(post => new { slug = post.Slug }));

app.MapPageLoader("/docs/[...path]", (string path, DocsStore docs) => /* ... */)
    .StaticParams<DocsStore>(docs => docs.All.Select(doc => new { path = doc.Path.Split('/') }));
```

- One object per page, with a property for each route parameter. A catch-all takes an array of segments (or a string with `/` between them); an optional catch-all may leave it out.
- `.StaticParams<TService>(service => ...)` resolves the service for you; `.StaticParams(services => ...)` takes the `IServiceProvider`, in a scope of its own.
- Each value becomes a directory name, so it must not be empty, `.` or `..`, or contain `/`, `\`, `:` or a NUL character. The export refuses it otherwise.
- `MapSitemap` uses the same lists for the dynamic pages in `sitemap.xml` (see [metadata](metadata.md)).

Every listed page must answer 200. A loader that returns `Page.NotFound()` for a listed value fails the export, naming the URL.

## What cannot be exported

`.ExcludeFromExport()` leaves a page out, on its loader, its page action or `MapPages()`:

```csharp
app.MapPageLoader("/guestbook", (GuestbookStore store) => /* ... */).ExcludeFromExport(); // needs a server
```

Without it, the export stops before it writes anything, naming each problem, when a page:

- is dynamic and has no `.StaticParams(...)`,
- has a page action (`MapPageAction`), since form posts need a server,
- requires authorization, through `RequireAuthorization()` on it or a layout loader above it, or through a fallback policy (unless the page is `.AllowAnonymous()`),
- would write the same file as another page on some file system: paths that differ only in case, Unicode normalisation or trailing dots and spaces, or a `.StaticParams` value that lands on another page's URL,
- has a path segment that Windows reserves for a device (`con`, `nul.txt`, `com1`).

Server actions, minimal APIs, `/_netjes/antiforgery`, web vitals collection and the image endpoint have no static counterpart: an exported page that calls them gets the static host's 404. `<Image>` points its `src` and `srcset` at the image endpoint, so give images on exported pages `unoptimized`, which uses the file as it is.

## Where the output goes

The output directory is replaced as a whole, so the export checks it first. It must be inside the app, and must not overlap `app/`, `public/`, `dist/` or the folder of the generated types. An existing directory is replaced only when it is empty or a previous export, which the export marks with a `.netjes-export` file.

## Redirects

A page whose loader returns `Page.Redirect(url)` is written as a small page that refreshes to the target, with a link to it, the target written exactly as the loader sent it. Other redirects fail the export, naming the URL:

- a redirect the page did not make itself: a login redirect, HTTPS redirection, a URL rewriter rule,
- one that points at the export server's own address (use a relative URL),
- a target that is neither a path on the site nor an http(s) URL,
- a target that starts with a quote, which a meta refresh would drop.

## In the browser

Exported pages carry no antiforgery token, since there is no server to check it. They hydrate as usual, and links and `router.push` work, but every navigation is a full page load: the client router asks for the next page's data, gets a static HTML file instead, and loads it as a document. Query strings reach no loader, since the loaders ran at export time. Assets are linked from the site root, so serve the export at the root of its host.

## Deferred values and Suspense

Each page is exported as a complete document, the one crawlers get (see [rendering](rendering-and-streaming.md#complete-documents)). The export waits for the page's `Page.Defer` values, up to `StreamTimeout`, and writes what they render in place of their Suspense fallback (`loading.tsx`, or a `<Suspense>` in the page), with no script to reveal it. So an exported page shows its deferred content without JavaScript, and under a static host's Content-Security-Policy that runs no inline script.

The values also follow in inline scripts, for the client to hydrate that content. Under a policy that blocks those scripts, the content shows on load but its boundary does not hydrate: the first update that renders the boundary again (a state change in the page around it) replaces it with the fallback, for good. Two ways out:

- keep `Page.Defer` out of exported pages that update around their deferred content, or
- list the scripts' hashes in the host's policy, next to the bootstrap's (see [security](security.md#content-security-policy)). In an exported file those scripts are fixed text, and change only when a value does, so their hashes change only when you export again with other data.

A value that fails, or misses `StreamTimeout`, leaves its fallback in the file, and the nearest `error.tsx` replaces it once JavaScript runs. The export logs a warning and still succeeds.
