# Routing

ASP.NET Core does the routing: every page file becomes an endpoint with an ordinary route template, so precedence, constraints and conventions such as `RequireAuthorization()` work as for any minimal API. The browser never matches routes itself.

## Files in `app/`

| File | Role |
|---|---|
| `page.tsx` | The page for its folder's URL. |
| `layout.tsx` | Wraps every page below it and stays mounted across navigations (its state survives). The root `app/layout.tsx` imports the global stylesheet. Netjes renders `<html>`, `<head>` and `<body>` around the root layout, with `suppressHydrationWarning` on `<html>` and `<body>`, so no layout renders them: the root one returns what goes inside `<body>`. |
| `template.tsx` | Like a layout, but remounted on every navigation. |
| `loading.tsx` | Suspense fallback, streamed first while the page below waits on deferred values or lazy code. |
| `error.tsx` | Error boundary below its layout; props `{ error, reset }` (`ErrorPageProps`). `error.status` is the HTTP status of a server answer behind the error and is undefined for an error thrown in the browser, so show `error.status ?? 500`. Render `error.message` only when `!error.withheld`: `withheld` marks a stand-in for a message Netjes does not show (an exception's or a failed `Page.Defer` value's with error details off, a server action answer with no message, a thrown value that is neither an Error nor a string). `reset()` renders the segment again, or on an error page the server rendered loads the page again; on the page the router's own boundary shows it does nothing, so offer a link there too. In development `error.message` can take several lines (a syntax error's code frame): render it with `whitespace-pre-wrap`. |
| `not-found.tsx` | Rendered for `Page.NotFound()` from a loader below it, and the root one for unknown URLs, except below a page package's mount, where the package's renders. |
| `forbidden.tsx`, `unauthorized.tsx` | Rendered for `Page.Forbidden()` / `Page.Unauthorized()` and for empty 403/401 responses from authorization; without one, the error page renders. |
| `global-error.tsx` | In `app/` only: rendered without any layout when even the error page fails. |

Built-in pages stand in for missing boundary files.

## Folders

| Folder | URL |
|---|---|
| `blog/[slug]` | `/blog/{slug}`: one segment |
| `docs/[...path]` | `/docs/a/b`: one or more segments; the loader gets them joined by `/` |
| `docs/[[...path]]` | `/docs` and anything below it |
| `(group)` | No URL segment: groups pages under a shared layout (`app/(marketing)/layout.tsx`) |
| `_private` | Not routed: components that live next to the pages |
| `@slot` + `default.tsx` | A parallel route: the layout gets the slot as a prop |
| `@modal/(.)photo/[id]` | An intercepting route, inside a slot only: a client navigation shows it in the slot, a reload shows the real page |

A literal segment beats a dynamic one (`blog/new` over `blog/[slug]`), as in Next.js. Two page files of the same shape (`[lang]/about` and `[slug]/about`) stop the app at startup. There is no pages router: Next.js pages router files in `pages/` stop the app at startup, and the message says where each goes (`pages/about.js` to `app/about/page.jsx`, `pages/404.tsx` to `app/not-found.tsx`, `pages/_app.tsx` and `pages/_document.tsx` to the root `app/layout.tsx`, `pages/api/` routes to minimal API endpoints). There are no route handlers either: a `route.ts` in `app/` stops the app in development and fails `vite build`, and an API is a minimal API endpoint in C# (`app.MapGet`, `app.MapPost`).

## One URL per page

A page answers its file's spelling: a `GET` or `HEAD` for `/About` for `app/about/page.tsx` gets a 308 to `/about`, and a client navigation a redirect payload, so links and crawlers see one URL, and the output cache stores no other case that a browser sends (`RedirectLiteralCase`, on by default; `options.RedirectLiteralCase = false`, or `Netjes:RedirectLiteralCase=false`, answers every case). Other methods are answered as sent: a `POST` to `/About` is not redirected. Only literal segments change; route values (`/Blog/Hello` goes to `/blog/Hello`), the query and the base path stay as sent, so a loader that redirected to its own spelling of a literal can drop that check, while one that checks a slug's case keeps it. A trailing slash is answered as it is (`TrailingSlash.Ignore`); `options.TrailingSlash = TrailingSlash.Never` (`Netjes:TrailingSlash=Never`) sends `/about/` to `/about` and drops a catch-all's last slash. The `/robots.txt` and `/sitemap.xml` that `MapRobotsTxt` and `MapSitemap` serve follow both. `WebApplicationFactory` tests run on TestServer, which passes on no request target, so they see no redirect: check it end to end with Playwright, or set `IHttpRequestFeature.RawTarget` in a startup filter, as the testing guide shows.

## Loader parameters and route warnings

Loaders bind like minimal APIs: a string or `TryParse` parameter takes the route value of the same name (any case), and otherwise the query string. `(string id)` on `/blog/[slug]` therefore reads `?id=`, never the slug, and `string[] path` on `[...path]` reads `?path=`. Netjes logs a warning for that at startup, and `npm run routes` prints it. Name parameters after the route segment, or mark query parameters `[FromQuery]`. Another warning is two pages that match the same URLs (`[id]` next to `[...rest]`); rename one, or accept it if the overlap is wanted. Others are an endpoint of the app that ties with a page (below), and a `[FromForm]` positional record with a `bool`, `bool?` or list field, which form binding refuses (400) when the post leaves that field out, as for an unchecked box: give the input `init` properties instead.

## Endpoints next to pages

ASP.NET Core ranks the endpoints a URL matches by order (pages and `MapGet` endpoints have 0), then route precedence (a literal, a constrained parameter, a parameter, a constrained catch-all, then a catch-all), then HTTP methods, hosts and accepted content types, in that order. When two rank first together and both fit a request, it answers 500 (`AmbiguousMatchException`); Netjes warns about such an endpoint next to a page or a page action at startup and in `npm run routes`. A file next to a catch-all page, such as a Markdown copy of each docs page next to `app/docs/[...path]/page.tsx`, ranks the same as the page, so map it ahead of it and for HEAD as well: `app.MapMethods("/docs/{**path:regex(\\.md$)}", [HttpMethods.Get, HttpMethods.Head], ...).WithOrder(-1)`. Link it with `<a href={useHref(endpointUrl('GET /docs/{**path:regex(\\.md$)}', { path: 'routing.md' }))}>`, where `path` is the file's path as one string (on the docs page, `` `${path.join('/')}.md` `` from the catch-all's array); a bare `<a href>` misses the base path.

## Links and navigation

- `<Link href={route('/blog/[slug]', { slug })}>`: client navigation with prefetch on hover and focus; takes a `ref`, as `<Form>`, `<SubmitButton>` and `<Image>` do. `route()` and the `Href` type come from `generated/netjes.ts`, so a wrong path or a missing parameter is a type error.
- `route(pattern, params, search, { hash })` builds the query from `search` and a `#fragment` from `hash`, the id of an element on the page: `route('/docs/[...path]', { path }, undefined, { hash: 'keys' })`. Pass `undefined` as `search` for a link with a fragment and no query.
- `aria-current="page"` goes on a link to the page on screen: the same path (`/x` and `/x/` are one) and the page's values for every query parameter the link names. Parameters it leaves out do not count, and a `#` link is never marked. Pass `aria-current` to decide yourself: `undefined` on an "All" filter (`/blog`) while a filter is on, `"true"` on a section link (Docs) below the section's page, from `useSelectedLayoutSegments()` or `usePathname()`.
- `useRouter()`: `push`, `replace`, `prefetch`, `refresh`, `back`, `forward`. The promises of `push`, `replace` and `refresh` resolve once the router has handed the page to React, when the old page can still be on screen; code that needs the new page runs in an effect of that page, or in one that depends on `usePathname()` and `useSearchParams()`.
- After a navigation to another page the router announces it in a polite live region (its title, else its first `h1`, else its path) and starts the focus over at the top of the document. A link to a `#fragment`, on the page on screen or on another, moves the focus to that element instead, which is how the skip link works; Back and Forward restore the scroll position and give a fragment no focus. The region speaks only when the name differs from the previous page's, so give each page its own title, and keep the skip link the first thing in the root layout that takes the focus: `<Link href="#content" prefetch="none" className="sr-only focus:not-sr-only">Skip to content</Link>` and `<main id="content">`. The focus stays on the same page (query changes, refreshes, a post that stays, a modal), on an `autoFocus` element or wherever the app's code moved it during the navigation (a link's `onClick` and a form's `onSubmit` included), on a `scroll={false}` link that still has it, and in a field the user types into. To put it elsewhere on the new page, do it in that page's `useEffect`, which runs after the router's. If a menu closes in an effect after the navigation, give the focus back to its button only while the focus is still inside the menu. In end-to-end tests, focus a field before typing after a navigation (`locator.fill()` does), and read the element you mean rather than a container's whole text.
- `usePathname()`, `useSearchParams(schema?)`, `useParams(schema?)`: with a schema the values are parsed; catch-all params are arrays. Search values a schema rejects are dropped so its defaults apply; a rejected route param goes to the error page, so constrain route params in C#.
- `useSelectedLayoutSegment()`, `useSelectedLayoutSegments()`: the active child segment in a layout (tabs).
- `useNavigation()`: `idle`, `loading` or `submitting`, for progress indicators.
- External URLs, `target`, `download` and modified clicks behave like a plain `<a>`; `reloadDocument` forces a full load.

## Metadata

- `export const metadata: Metadata = { title: 'About' }` on pages and layouts; a layout's `title: { default, template: '%s · Site' }` applies to the pages below it. A `default` below the root goes through the nearest template above it, like a string (`'Docs'` under `'%s · Site'` is `Docs · Site`), never through the one beside it; `{ absolute: 'Docs' }` skips every template.
- From C#: `.WithMetadata(new PageMetadata { Title = post.Title, Description = ... })` on the loader's result, merged over the files' metadata.
- Below a layout that sets a title, use `metadata`, not a `<title>` element (that makes a second title).
- Code the first paint needs, such as a theme set on `<html>`, goes in `<Script id="theme" strategy="beforeInteractive">{code}</Script>` first in the root layout (a script takes no focus, so the skip link after it stays first): the server writes inline code where it stands and the parser runs it there. An external `beforeInteractive` script runs once the shell is parsed, in order, before the page's code; hydration waits for it, and inline code runs before it. That holds in a server-rendered document: a component that mounts in the browser, after a client navigation or when no sidecar was ready, adds its script after mounting, as `afterInteractive` does. Keep them outside Suspense boundaries. Under a hash-based policy, list the hash of the code as the document carries it, which `inlineScriptHashes` from `netjes/testing` gives, and an external script's origin in `script-src`.

## Protecting sections

`app.MapLayoutLoader("/dashboard", ...).RequireAuthorization()` protects every page below `app/dashboard/`; `app.MapPages().RequireAuthorization()` protects every page that has no loader of its own; a page loader takes `.RequireAuthorization()` itself. With cookie authentication, answer 401/403 instead of redirecting (`OnRedirectToLogin`, `OnRedirectToAccessDenied`) so `unauthorized.tsx` and `forbidden.tsx` render. An unknown URL stays a 404 for everyone: its not-found page, like the status and error pages of server actions and minimal APIs and a page's 401, 403 and pre-authorization status pages (a rate limiter's 429), runs a layout loader only for a visitor a page in its folder would let in (its conventions, those above it, `MapPages()`'s or the fallback policy), and renders inside the layouts above it for the rest. So under `MapPages().RequireAuthorization()` the root `unauthorized.tsx` renders without the root layout when that has a loader: make it stand on its own, and never put `.AllowAnonymous()` on a layout loader to keep it, since that opens every page below. `Page.Render(pattern)` from a minimal API runs the page's layout loaders under that endpoint's authorization: protect it as the page is.

A check that depends on data (is this user a member of this project?) goes in the page loader as well as in the layout loader: a page's loader runs before its layouts' loaders, and a layout loader is skipped on client navigations within its section while its own route values, the user and the roles stay the same. A layout loader also binds only the route values of its own URL, so it cannot check a deeper one.

## Page packages

A page package is an npm package and a NuGet package of one version that bring pages, layouts, boundaries, loaders and actions. The app mounts it with the package's extension, before `app.MapPages()`, and lists the npm half in `netjes({ packages: ['@acme/help-center'] })`; both halves are installed at one version, or the app stops at startup in one line that says what to install.

- The prefix is in folder syntax: `/help`, `/(marketing)/help` (the same URL, inside `app/(marketing)/layout.tsx`), `/[tenant]/help` (`tenant` is a route value). The package's folder of pages merges into `app/` under it, folder by folder: its `layout.tsx` at the mount wraps the app's own pages in `app/help/`, and its `(center)` group wraps an app page in `app/help/(center)/`. Link to its pages with the app's `route('/help/articles/[slug]', { slug })`.
- An app page of the same shape as a package's page stops the app. To replace the package's page, put the app's in the same folder and add `.Override("/help/contact")` to the mount: it renders with the package's loader, so it reads `pages['/contact']` from the package's generated file (`import { pages } from '@acme/help-center/generated'`), and the app maps no loader for it. Layouts and boundaries cannot be replaced.
- Conventions on the mount (`.RequireAuthorization()`, `.ExcludeFromExport()`, `.ExcludeFromSitemap()`) cover the package's pages and endpoints, not the app's pages under the prefix; protect those with a layout loader above them, as before. An app layout loader above the mount protects the package's pages too.
- An unknown URL below the mount renders the package's `not-found.tsx`, after the layout loaders on the way, and `MapPages()`'s authorization, have authorized the visitor.
- A package that draws its own shell (a back-office package) should not render inside the site's header: keep `app/layout.tsx` to providers and the skip link, and move the site's header and footer into `app/(site)/layout.tsx` with the site's pages. URLs do not change. The `package-shell` route warning names an app that has not.
- Route warnings for packages: `package-shell`, `package-unmounted` (in the build but not mounted), `package-nonce` (a package whose pages stream, under a Content-Security-Policy without a nonce), and `overlap` for a literal page of one source next to a parameter page of the other (`/help/settings` from the package next to the app's `app/help/[section]`).

## Base path and locales

`app.UsePathBase("/docs")` with `netjes({ basePath: '/docs' })`; links, forms and redirects add it, and app code is written as if at the root (`useHref(path)` for a plain `<a>`). Locale routing is a `[lang]` folder with `AddNetjesLocalization`, `UseNetjesLocalization` and `MapLocaleRedirect`; `useLocale()` reads it.

## More

[Routing](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/routing.md), [Page packages](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/page-packages.md) and [Metadata](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/metadata.md) in the guides, and the [React API](https://github.com/DennisVerhoeven/netjes/blob/main/docs/reference/react-api.md) for every component and hook.
