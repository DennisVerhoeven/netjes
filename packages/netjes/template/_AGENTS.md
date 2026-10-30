# __TITLE__

How this app is built, for coding agents and the people working with them. It is a [Netjes](https://github.com/DennisVerhoeven/netjes) app: ASP.NET Core receives every request and owns routing, data loading, server actions, authorization and caching; a Node sidecar renders the React pages; the browser hydrates them and navigates on the client.

## Netjes docs

- https://raw.githubusercontent.com/DennisVerhoeven/netjes/main/llms.txt: an index of the docs; https://raw.githubusercontent.com/DennisVerhoeven/netjes/main/llms-full.txt has the ones for building apps in one file. For people, the same docs are at https://github.com/DennisVerhoeven/netjes/blob/main/docs/README.md.
- `.claude/skills/netjes/`: the conventions in short, with references for routing, data and actions, caching and streaming, testing and deployment. Read `SKILL.md` before changing pages, loaders or actions.
- The docs on main can be ahead of the version this app uses (`npx netjes --version`). The installed package, with its types, is in `node_modules/netjes/`.

## Layout

| Path | What |
|---|---|
| `app/` | Pages, as in the Next.js App Router: `page.tsx` is `/`, `about/page.tsx` is `/about`, `blog/[slug]/page.tsx` is `/blog/{slug}`. `layout.tsx` wraps the pages below it and stays mounted; `loading.tsx`, `error.tsx`, `not-found.tsx`, `forbidden.tsx` and `unauthorized.tsx` are boundaries. `(group)` folders add no URL segment, `_private` folders are not routed. |
| `Features/` | The C# side, one folder per feature (`namespace __NAME__.Features.Home`): page loaders (`MapPageLoader`), layout loaders (`MapLayoutLoader`), server actions (`MapAction`) and JSON endpoints, with records for what they take and return. Each feature has a `Map...` extension method that `Program.cs` calls. |
| `Program.cs` | Services and the pipeline: `AddNetjes`, `UseNetjes`, authentication (when you add it), `UseAntiforgery`, each feature's `Map...`, and `app.MapPages()` last. |
| `components/` | React components, with their Vitest tests next to them (`*.test.tsx`). |
| `generated/netjes.ts` | Generated from the C# types and the page files: zod schemas for page and layout props (`pages`, `layouts`), `sharedProps`, typed `serverActions`, `api` schemas for JSON endpoints, `route()` and the `Href` type. Never edit it. |
| `styles/global.css` | Tailwind CSS (`@import 'tailwindcss'`), imported by `app/layout.tsx`. |
| `public/` | Static files, served at the site root. `favicon.svg` is the Netjes mark, linked by `icons` in the root layout's `metadata`; replace it with the app's own. |

## Commands

```bash
npm run dev                 # dotnet watch + Vite HMR on http://localhost:5080; rewrites generated/netjes.ts on start
npm test                    # Vitest
npm run typecheck           # strict TypeScript
dotnet build                # C#, warnings are errors
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
- zod at every boundary: `definePage` and `defineLayout` with the generated schemas, `useSearchParams(schema)` and `useParams(schema)` for the URL, `api['GET /api/...'].response.parse(...)` for JSON you fetch, `usePublicConfig(schema)` for public config, `Href` (or `isHref`) from the generated file for URLs that come from data. No `any`, no casts of server data.
- Server state goes through TanStack Query (`useQuery`), not `useEffect` and `fetch`. Seed it from the loader with `.WithQuery(key, data)` so it is server-rendered; actions update it with `.Invalidate(key)` or `.WithQuery(key, data)`.
- Internal links are `<Link href={route('/blog/[slug]', { slug })}>`; a typo in an href is a type error.
- Action inputs are records with `init` properties: `public sealed record NoteInput { [Required] public string? Text { get; init; } public bool Pinned { get; init; } }`. A field the post leaves out, such as an unchecked checkbox, keeps its default; form binding refuses (400) a post that lacks one of a positional record's constructor fields. DataAnnotations on them (`[Required]`, `[StringLength]`) are checked before the action runs and come back as field errors, with no setup, also for a post that has none of the input's fields. Return `ServerAction.Invalid(field, message)` for other errors the user can fix.
- `.CachePage(...)` only for pages that are the same for every visitor: their loaders and shared props must not depend on cookies or other per-visitor state.
- `Netjes:PublicConfig` is sent to the browser: never put secrets there.
- Write the failing test first, then the change.

## Before you finish

```bash
npm test && npm run typecheck && dotnet build && npx netjes generate --check
```

Then `npm run routes` should print no warnings, and the page should work in `npm run dev`, including with JavaScript turned off for forms.
