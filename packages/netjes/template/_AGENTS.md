# __TITLE__

How this app is built, for coding agents and the people working with them. It is a [Netjes](https://github.com/DennisVerhoeven/netjes) app: ASP.NET Core receives every request and owns routing, data loading, server actions, authorization and caching; a Node sidecar renders the React pages; the browser hydrates them and navigates on the client.

## Netjes docs

- https://raw.githubusercontent.com/DennisVerhoeven/netjes/main/llms.txt: an index of the docs; https://raw.githubusercontent.com/DennisVerhoeven/netjes/main/llms-full.txt has the ones for building apps in one file. For people, the same docs are at https://github.com/DennisVerhoeven/netjes/blob/main/docs/README.md.
- `.claude/skills/netjes/`: the conventions in short, with references for routing, data and actions, caching and streaming, testing and deployment. Read `SKILL.md` before changing pages, loaders or actions.
- The docs on main can be ahead of the version this app uses (`npx netjes --version`). The installed package, with its types, is in `node_modules/netjes/`.

## Layout

| Path | What |
|---|---|
| `app/` | Pages, as in the Next.js App Router: `page.tsx` is `/`, `about/page.tsx` is `/about`, `blog/[slug]/page.tsx` is `/blog/{slug}`. `layout.tsx` wraps the pages below it and stays mounted. Netjes renders `<html>`, `<head>` and `<body>` around the root layout, so no layout renders them: the root one returns what goes inside `<body>`. `loading.tsx`, `error.tsx`, `not-found.tsx`, `forbidden.tsx` and `unauthorized.tsx` are boundaries. `(group)` folders add no URL segment, `_private` folders are not routed. `document.test.ts` renders the home page's whole document, as the server sends it, with `renderDocument` from `netjes/testing`. |
| `Features/` | The C# side, one folder per feature (`namespace __NAME__.Features.Home`): page loaders (`MapPageLoader`), layout loaders (`MapLayoutLoader`), server actions (`MapAction`) and JSON endpoints, with records for what they take and return. Each feature has a `Map...` extension method that `Program.cs` calls. |
| `Program.cs` | Services and the pipeline: `AddNetjes`, `UseNetjes`, authentication (when you add it), `UseAntiforgery`, each feature's `Map...`, and `app.MapPages()` last. |
| `components/` | React components, with their Vitest tests next to them (`*.test.tsx`). |
<!--netjes:if tests-->
| `tests/__NAME__.Tests/` | The C# tests, on xUnit v3: `AppFactory.cs` runs the real app in memory with `WebApplicationFactory`, with no Node and no client build, and `HomePageTests.cs` requests the home page and posts its form as the client router does. A new test of a loader or an action goes here. The app's build leaves out the root folders `tests/` and `e2e/` (`DefaultItemExcludes` in `__NAME__.csproj`), so C# for the app does not go in them. |
<!--netjes:end-->
<!--netjes:if e2e-->
| `e2e/` | Playwright tests, which `playwright.config.ts` runs against the app twice: in Development (the Vite dev server) and in Production (the build). |
<!--netjes:end-->
| `generated/netjes.ts` | Generated from the C# types and the page files: zod schemas for page and layout props (`pages`, `layouts`), `sharedProps`, typed `serverActions`, `api` schemas for JSON endpoints, `endpointUrl()` for the URLs of the app's endpoints, `route()` and the `Href` type. Never edit it. |
| `styles/global.css` | Tailwind CSS (`@import 'tailwindcss'`), imported by `app/layout.tsx`. |
| `public/` | Static files, served at the site root. `favicon.svg` is the Netjes mark, linked by `icons` in the root layout's `metadata`; replace it with the app's own. |

## Commands

```bash
npm run dev                 # dotnet watch + Vite HMR on http://localhost:5080; rewrites generated/netjes.ts on start
npm test                    # Vitest
npm run typecheck           # strict TypeScript
dotnet build                # C#, warnings are errors
<!--netjes:if tests-->
dotnet test tests/__NAME__.Tests   # the C# tests, in memory: no Node and no client build
<!--netjes:end-->
<!--netjes:if e2e-->
npm run test:e2e            # Playwright, on a development and a production server (run npx playwright install chromium once first)
<!--netjes:end-->
npx netjes generate         # rewrite generated/netjes.ts after changing a C# type (--check: fail when stale)
npm run routes              # the route table as ASP.NET Core sees it, with route warnings
npx netjes build            # types, then the client and server bundles
dotnet publish -c Release   # runs the build and ships dist/
```

## Adding a page with data

1. Write the C# loader in `Features/<Feature>/`: a record for the props and `app.MapPageLoader("/blog/[slug]", (string slug, PostStore posts) => ...)` returning `Page.Props(new PostProps(...))`, `Page.NotFound()` or `Page.Redirect(url)`. Route parameters bind by name, like any minimal API.
2. Call its `Map...` method from `Program.cs`, before `app.MapPages()`.
3. Run `npx netjes generate` (or restart `npm run dev`).
4. Write `app/blog/[slug]/page.tsx`: `export default definePage(pages['/blog/[slug]'], function Post({ post }) { ... })`.
5. Test the component with Vitest and `createTestApp` from `netjes/testing`; run the checks below.

A page without a loader is a plain default export and gets empty props. Forms call server actions: `app.MapAction("posts.like", ...)` in C#, `useAction(serverActions['posts.like'])` and `<Form action={formAction}>` in React; they also work without JavaScript.

## Rules

- C# decides: data access, validation, authorization (`.RequireAuthorization()` on a loader, a layout loader or `MapPages()`; `Page.Forbidden()`), redirects and caching. React renders what the loader sends; hiding a button is not access control.
- Never edit `generated/netjes.ts`. Change the C# type and regenerate; commit the file with the change. CI runs `npx netjes generate --check`.
- zod at every boundary: `definePage` and `defineLayout` with the generated schemas (extended, as in `pages['/x'].extend({ link: Href })`, only where no C# type fits), `useSearchParams(schema)` and `useParams(schema)` for the URL, `api['GET /api/...'].response.parse(...)` for JSON you fetch from `useHref(endpointUrl('GET /api/...', params))`, `usePublicConfig(schema)` for public config, `Href` (or `isHref`) from the generated file for URLs that come from data, such as a CMS. No `any`, no casts of server data.
- A link that C# sends to one of the app's pages is a `Href` in the C# record (`new Href("/about")`): it is generated as the `Href` schema, checked against the pages when the props are parsed, and goes to `<Link href>` as it is. C# checks only its shape, and a root-relative `Href` that no page serves fails the whole page, so a link to an endpoint or a file stays a `string`.
- Server state goes through TanStack Query (`useQuery`), not `useEffect` and `fetch`. Seed it from the loader with `.WithQuery(key, data)` so it is server-rendered; actions update it with `.Invalidate(key)` or `.WithQuery(key, data)`.
- Internal links are `<Link href={route('/blog/[slug]', { slug })}>`; a typo in an href is a type error. `route(pattern, params, search, { hash })` builds the query from `search` and a `#fragment` from `hash`, the id of an element on the page; pass `undefined` as `search` for a link with a fragment and no query.
- After a client navigation to another page, the router starts the focus over at the top of the document, as a page load does, and announces the page to screen readers by its title. Keep the "Skip to content" link the first thing in `app/layout.tsx` that takes the focus, with the content in `<main id="content">`, and give each page a title of its own (`metadata`, or `PageMetadata` from C#): the router says nothing for a page whose title is the previous page's.
- Action inputs are records with `init` properties: `public sealed record NoteInput { [Required] public string? Text { get; init; } public bool Pinned { get; init; } }`. A field the post leaves out, such as an unchecked checkbox, keeps its default; form binding refuses (400) a post that lacks one of a positional record's constructor fields. DataAnnotations on them (`[Required]`, `[StringLength]`) are checked before the action runs and come back as field errors, with no setup, also for a post that has none of the input's fields. Return `ServerAction.Invalid(field, message)` for other errors the user can fix.
- `.CachePage(...)` only for pages that are the same for every visitor: their loaders and shared props must not depend on cookies or other per-visitor state. In the Development environment pages are never output-cached (each request renders them), so check caching on `npx netjes build && npx netjes start`, or in a `WebApplicationFactory` test in the `Testing` environment with `Netjes:Mode` set to `Development`.
- An `error.tsx` renders `error.message` only when `!error.withheld`, and shows `error.status ?? 500` if it shows a status.
- `Netjes:PublicConfig` is sent to the browser: never put secrets there. So is the route of each endpoint with an `endpointUrl` key, one mapped only in Development included, once any component calls `endpointUrl`: keep secrets out of routes, and authorize on the endpoint ([security](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/security.md#errors-and-configuration)).
- Write the failing test first, then the change.

## Before you finish

<!--netjes:if tests-->
```bash
npm test && npm run typecheck && dotnet build && dotnet test tests/__NAME__.Tests && npx netjes generate --check
```
<!--netjes:else-->
```bash
npm test && npm run typecheck && dotnet build && npx netjes generate --check
```

This app has no C# test project yet: [testing](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/testing.md#c-tests-of-loaders-and-actions) says how to add one.
<!--netjes:end-->

Then `npm run routes` should print no warnings, and the page should work in `npm run dev`, including with JavaScript turned off for forms.
<!--netjes:if e2e-->

For a change to a page or a form, also run `npm run test:e2e`.
<!--netjes:end-->
