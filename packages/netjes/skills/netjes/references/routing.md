# Routing

ASP.NET Core does the routing: every page file becomes an endpoint with an ordinary route template, so precedence, constraints and conventions such as `RequireAuthorization()` work as for any minimal API. The browser never matches routes itself.

## Files in `app/`

| File | Role |
|---|---|
| `page.tsx` | The page for its folder's URL. |
| `layout.tsx` | Wraps every page below it and stays mounted across navigations (its state survives). The root `app/layout.tsx` imports the global stylesheet. |
| `template.tsx` | Like a layout, but remounted on every navigation. |
| `loading.tsx` | Suspense fallback, streamed first while the page below waits on deferred values or lazy code. |
| `error.tsx` | Error boundary below its layout; props `{ error, reset }` (`ErrorPageProps`), status in `error.status`. In development `error.message` can take several lines (a syntax error's code frame): render it with `whitespace-pre-wrap`. |
| `not-found.tsx` | Rendered for `Page.NotFound()` from a loader below it, and the root one for unknown URLs. |
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

## Loader parameters and route warnings

Loaders bind like minimal APIs: a string or `TryParse` parameter takes the route value of the same name (any case), and otherwise the query string. `(string id)` on `/blog/[slug]` therefore reads `?id=`, never the slug, and `string[] path` on `[...path]` reads `?path=`. Netjes logs a warning for that at startup, and `npm run routes` prints it. Name parameters after the route segment, or mark query parameters `[FromQuery]`. The other warning is two pages that match the same URLs (`[id]` next to `[...rest]`); rename one, or accept it if the overlap is wanted.

## Links and navigation

- `<Link href={route('/blog/[slug]', { slug })}>`: client navigation with prefetch on hover and focus, `aria-current` on the active link. `route()` and the `Href` type come from `generated/netjes.ts`, so a wrong path or a missing parameter is a type error.
- `useRouter()`: `push`, `replace`, `prefetch`, `refresh`, `back`, `forward`.
- `usePathname()`, `useSearchParams(schema?)`, `useParams(schema?)`: with a schema the values are parsed; catch-all params are arrays. Search values a schema rejects are dropped so its defaults apply; a rejected route param goes to the error page, so constrain route params in C#.
- `useSelectedLayoutSegment()`, `useSelectedLayoutSegments()`: the active child segment in a layout (tabs).
- `useNavigation()`: `idle`, `loading` or `submitting`, for progress indicators.
- External URLs, `target`, `download` and modified clicks behave like a plain `<a>`; `reloadDocument` forces a full load.

## Metadata

- `export const metadata: Metadata = { title: 'About' }` on pages and layouts; a layout's `title: { default, template: '%s · Site' }` applies to the pages below it.
- From C#: `.WithMetadata(new PageMetadata { Title = post.Title, Description = ... })` on the loader's result, merged over the files' metadata.
- Below a layout that sets a title, use `metadata`, not a `<title>` element (that makes a second title).

## Protecting sections

`app.MapLayoutLoader("/dashboard", ...).RequireAuthorization()` protects every page below `app/dashboard/`; `app.MapPages().RequireAuthorization()` protects every page that has no loader of its own; a page loader takes `.RequireAuthorization()` itself. With cookie authentication, answer 401/403 instead of redirecting (`OnRedirectToLogin`, `OnRedirectToAccessDenied`) so `unauthorized.tsx` and `forbidden.tsx` render.

A check that depends on data (is this user a member of this project?) goes in the page loader as well as in the layout loader: a page's loader runs before its layouts' loaders, and a layout loader is skipped on client navigations within its section while its own route values, the user and the roles stay the same. A layout loader also binds only the route values of its own URL, so it cannot check a deeper one.

## Base path and locales

`app.UsePathBase("/docs")` with `netjes({ basePath: '/docs' })`; links, forms and redirects add it, and app code is written as if at the root (`useHref(path)` for a plain `<a>`). Locale routing is a `[lang]` folder with `AddNetjesLocalization`, `UseNetjesLocalization` and `MapLocaleRedirect`; `useLocale()` reads it.

## More

[Routing](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/routing.md) and [Metadata](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/metadata.md) in the guides, and the [React API](https://github.com/DennisVerhoeven/netjes/blob/main/docs/reference/react-api.md) for every component and hook.
