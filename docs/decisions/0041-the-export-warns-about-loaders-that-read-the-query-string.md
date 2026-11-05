# 0041 – The export warns about loaders that read the query string

Status: accepted

## Context

`netjes export` requests each page's URLs without a query string and writes what the app answers. A static host then serves that file for every query string: `/blog?tag=react` gets `blog/index.html`, which the loader rendered with no tag. A loader that binds a parameter from the query string runs once, with no value, and every visitor gets that answer whatever the address holds. Nothing said so: the export succeeded, and the page looked right until someone followed a filter link on the static site. The static export guide said that query strings reach no loader, but only the export knows which loaders read one.

The page's endpoint carries what minimal API binding inferred for each of the loader's parameters (`IParameterBindingMetadata`), which the route warnings read too ([ADR 0015](0015-route-warnings-name-what-is-likely-wrong.md)), and a layout loader's binding can be inferred the same way. In the browser, `useSearchParams()` does not see the query either: the router starts from the URL the page was exported for. Next.js refuses server code that reads the request, such as a page's `searchParams`, in a static export, and renders the components that call `useSearchParams()` in the browser, from the address bar.

## Decision

- **A warning, and the page is still written.** When nothing stops the export, it logs one warning for each page it writes whose loader reads the query string, and one for each layout loader that reads it above a page it writes. Each names the file, the query string keys, and the page's route, or the pages below the layout (the first three, then how many more), and says what to do: make the value a route parameter with `.StaticParams(...)`, or keep the page on a server with `.ExcludeFromExport()`. The exit code is unchanged.
- **In a category of its own, `Netjes.Export.QueryString`.** Raising its log level silences these warnings and nothing else.
- **What binding reads, not what the code does.** A parameter reads the query string when minimal API binding takes its value from there: one with `[FromQuery]`, whose `Name` is the key when it has one, or one whose binding from the query string is inferred (a string, a type with `TryParse` or an array of those, whose name is no route parameter's). Members of an `[AsParameters]` type count. `LoaderBinding.QueryKeyOf` is the one rule, and reads `[FromQuery]` before the inferred binding, because minimal APIs report no `TryParse` for a parameter that has the attribute.
- **The browser side is documented, not fixed.** The static export guide says that `useSearchParams()` sees no query string on an exported page, and shows `window.location.search` read in an effect, after hydration, as the remedy until the router reads the address itself.

## Alternatives considered

- **Fail the export**, as for a page action or a page that requires authorization. Those cannot work from a file at all, while a page that reads the query string often still works for the URL without one: `/blog` lists every post. A failure would make every such site exclude the page or move the value into the route before it can export anything, and a page exported this way on purpose would need a way to say so.
- **Docs only.** The guide already said it, and pages were still exported without anyone noticing.
- **A per-page acknowledgment**, such as a convention that marks a page as exported without its query string. More API for one warning; the route warnings chose the log level over switches per kind for the same reason ([ADR 0015](0015-route-warnings-name-what-is-likely-wrong.md)).
- **The export's own category**, `Netjes.Export.StaticExportService`. Its only silencer, the log level, would also hide "Exported N pages" and the reasons an export failed.
- **Pointing the message at `useSearchParams()`** as a fix. It sees no query string on an exported page either.

## Consequences

- An app with an exported page or layout that reads the query string logs a warning on every export. A page exported that way on purpose warns every time, unless the app raises the category's log level, which hides the warning for loaders added later too.
- The check follows binding, so some reads go unreported: a loader that reads `HttpContext.Request.Query` itself, or takes a type that binds itself with `BindAsync`.
- The Blog example's export logs one warning, for `/blog`, which it keeps as its example of a filter in the query string; its export suite asserts that warning.
- When the router reads the browser's query string on exported pages, the docs that describe the gap and its remedy change with it: the static export guide, `useSearchParams` in the React API reference, "Reading the URL" in the routing guide, and the agent skill.
