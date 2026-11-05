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

`.ExcludeFromExport()` leaves a page out, on its loader, its page action or `MapPages()`. On a layout loader, it leaves out every page below the layout:

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

### Loaders that read the query string

The export requests each URL without a query string, and a static host serves the file it wrote for every query string: `/blog?tag=react` gets `blog/index.html`, which the loader rendered with no tag. A page whose loader reads the query string is still exported, as the page without one, and the export logs a warning in the category `Netjes.Export.QueryString` for that loader. It does the same for a layout loader that reads the query string above a page it writes, naming the pages below the layout. The warning does not change the exit code (a loader that requires the value fails the export, as below):

```text
warn: Netjes.Export.QueryString[0]
      app/blog/page.tsx reads 'tag' from the query string, but netjes export writes /blog without one, and a static host serves that file for every query string. To export a page for each value, make it a route parameter with .StaticParams(...); to keep the page on a server, exclude it with .ExcludeFromExport().
```

There are two fixes:

- Make the value a route parameter and list its values with `.StaticParams(...)`: `/blog/tags/[tag]` instead of `/blog?tag=`, exported once for each tag.
- Keep the page on a server: `.ExcludeFromExport()` on its loader, or on the layout loader for every page below the layout.

If the page without a query string is what the static site should show, keep it, and set the category's log level to `Error` (`Logging:LogLevel:Netjes.Export.QueryString`, see [logs](deployment.md#logs)) to silence the warning. That silences it for every loader, including those added later; the export's own messages, in `Netjes.Export.StaticExportService`, still appear.

A loader that requires the value cannot be exported as it is. A parameter with no default value and a type that is not nullable, such as `int page`, or `string q` with nullable reference types on, has to have one: minimal APIs answer 400 to the export's request without it, so the export fails on that page, and a layout loader that requires one fails every page below it. The warning then says so, names only the keys the loader requires, and adds a third fix: give the parameter a default value or a nullable type (`int page = 1`, `string? q`) to export the page without one. An array never requires a value: without one, it binds an empty array.

A parameter counts when minimal API binding takes its value from the query string: one marked `[FromQuery]`, which the warning names by the attribute's `Name` when it has one, or one without a binding attribute that is a string or a type with `TryParse` whose name is no route parameter's, or an array of those or `StringValues`, whatever its name, since a route value binds only to a single value (see [route warnings](routing.md#a-loader-parameter-that-reads-the-query-string-instead-of-a-route-value)). Members of an `[AsParameters]` type count as parameters. The check cannot see a loader that reads `HttpContext.Request.Query` itself, or a parameter whose type binds itself with `BindAsync`. A layout loader's warning names the pages below the layout and never `404.html`, which the export renders under the root layout too, so a root layout loader that reads the query string renders that file without one as well. Nor can it see code in the browser, where `useSearchParams()` sees no query string on an exported page either (see [In the browser](#in-the-browser)).

## Where the output goes

The output directory is replaced as a whole, so it is checked before anything is written to it. It must be inside the app, and must not overlap `app/`, `public/`, `dist/` or the folder of the generated types. An existing directory is replaced only when it is empty or a previous export, which the export marks with a `.netjes-export` file. A folder name that starts with two dots, such as `..site`, is an ordinary name; only `..` itself means the folder above.

`netjes export` checks the first rule before it builds, against the project folder: the folder it runs in, with the app's `.csproj`, or the one `--cwd` names. On macOS and Linux it takes that folder by its real path, with any symlink resolved, as the app sees its own folder, and resolves a relative output folder against it. A folder outside the project folder, or the project folder itself, ends the command with exit code 1 before anything is built. On macOS and Windows that check ignores letter case, as .NET does. After the build, the app checks every rule before it renders a page, against `Netjes:Root`, which is the project folder unless it is set.

`netjes export --no-build` skips what `netjes build` does (the types and both Vite bundles) and the command's own check of the folder, and leaves every rule to the app. It still resolves a relative output folder against the project folder, by its real path on macOS and Linux, as above. So an app whose `Netjes:Root` is a folder above the project folder can export to a folder inside that one, such as `../site`; and a folder the app refuses costs a compile and a start before the export stops.

## Redirects

A page whose loader returns `Page.Redirect(url)` is written as a small page that refreshes to the target, with a link to it, the target written exactly as the loader sent it. Other redirects fail the export, naming the URL:

- a redirect the page did not make itself: a login redirect, HTTPS redirection, a URL rewriter rule,
- one that points at the export server's own address (use a relative URL),
- a target that is neither a path on the site nor an http(s) URL,
- a target that starts with a quote, which a meta refresh would drop.

## In the browser

Exported pages carry no antiforgery token, since there is no server to check it. They hydrate as usual, and links and `router.push` work, but every navigation is a full page load: the client router asks for the next page's data, gets a static HTML file instead, and loads it as a document. Assets are linked from the site root, so serve the export at the root of its host.

Query strings reach no loader, since the loaders ran at export time (see [above](#loaders-that-read-the-query-string)), and no hook either. On an exported page, `useSearchParams()` reads the URL the page was exported for, which has no query string, whatever the address bar shows: it returns an empty `URLSearchParams`, and with a schema, what the schema makes of no values. For the same reason, `<Link>` never marks a link with a query parameter as the current page there. Until the router reads the address itself, read `window.location.search` in an effect, and parse it with the schema you would give `useSearchParams`:

```tsx
import { useEffect, useState } from 'react';
import { z } from 'zod';

const filters = z.object({ tag: z.string().optional() });

// On an exported page: the query string in the address bar, once the page has hydrated.
function useExportedSearch() {
  const [search, setSearch] = useState<z.infer<typeof filters>>({});
  useEffect(() => {
    const parsed = filters.safeParse(Object.fromEntries(new URLSearchParams(window.location.search)));
    if (parsed.success) setSearch(parsed.data);
  }, []);
  return search;
}
```

The first render matches the exported HTML, as hydration needs, and the effect then renders the page again with the values. Every navigation on an exported site loads a document, so the effect runs for each URL. `Object.fromEntries` keeps the last value of a key that repeats; `getAll(key)` on the `URLSearchParams` reads every value.

The redirect of another spelling of a page's URL ([One URL per page](routing.md#one-url-per-page)) is the server's, and the export writes none. It requests each page at its file's spelling and writes it where it always has, `out/about/index.html` for `/about`, whatever `RedirectLiteralCase` and `TrailingSlash` say. What `/About` or `/about/` answers on a static host is up to that host.

## Deferred values and Suspense

Each page is exported as a complete document, the one crawlers get (see [rendering](rendering-and-streaming.md#complete-documents)). The export waits for the page's `Page.Defer` values, up to `StreamTimeout`, and writes what they render in place of their Suspense fallback (`loading.tsx`, or a `<Suspense>` in the page), with no script to reveal it. So an exported page shows its deferred content without JavaScript, and under a static host's Content-Security-Policy that runs no inline script.

The values also follow in inline scripts, for the client to hydrate that content. Under a policy that blocks those scripts, the content shows on load but its boundary does not hydrate: the first update that renders the boundary again (a state change in the page around it) replaces it with the fallback, for good. Two ways out:

- keep `Page.Defer` out of exported pages that update around their deferred content, or
- list the scripts' hashes in the host's policy, next to `CSP_SCRIPT_HASHES` from `netjes`, the hashes of the inline scripts that every document carries with the same text, such as the bootstrap (`NetjesContentSecurityPolicy.ScriptHashes` in C#; see [security](security.md#hash-based)). In an exported file those scripts are fixed text, and change only when a value does, so their hashes change only when you export again with other data.

A value that fails, or misses `StreamTimeout`, leaves its fallback in the file, and the nearest `error.tsx` replaces it once JavaScript runs. The export logs a warning and still succeeds.
