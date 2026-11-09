---
name: netjes
description: Use when building or changing a Netjes app (React pages in app/ served by ASP.NET Core): adding pages, layouts, C# loaders, server actions, JSON endpoints, caching, streaming, tests or deployment. Covers the app/ and Features/ conventions, generated/netjes.ts, and the commands that check the work.
---

# Building a Netjes app

Netjes renders React pages from ASP.NET Core. C# receives every request and owns routing, loaders, server actions, authorization and caching. A Node sidecar renders the React tree to HTML; the browser hydrates it, and later navigations fetch the next page's props as JSON from the same C# endpoints. The file conventions in `app/` are the Next.js App Router's, but there are no React Server Components and no `route.ts`: data comes from C# loaders, and APIs are minimal API endpoints.

## Where things go

- `app/**/page.tsx`: a page. `app/blog/[slug]/page.tsx` serves `/blog/{slug}`. Layouts, boundaries and folder rules: [references/routing.md](references/routing.md).
- `Features/<Feature>/*.cs`: the C# side of a feature, mapped by an extension method that `Program.cs` calls before `app.MapPages()`. Loaders and actions: [references/data-and-actions.md](references/data-and-actions.md).
- `generated/netjes.ts`: zod schemas and typed references generated from the C# types and the page files. Read it to see what a page receives; never edit it.
- Page packages: pages, layouts and loaders from an npm and NuGet package pair, mounted at a prefix by the package's extension in `Program.cs` (`app.MapHelpCenter("/help")`, before `app.MapPages()`) and listed in `netjes({ packages })` in `vite.config.ts`. Their files stay in `node_modules` and merge into `app/` folder by folder; never copy them into `app/`. Mounting, replacing a page and the root layout: [references/routing.md](references/routing.md#page-packages).
- `components/`: React components and their tests. Testing: [references/testing.md](references/testing.md).
- `tests/<App>.Tests/`: the C# tests (xUnit v3 and `WebApplicationFactory`), where the app has them, as an app made with `netjes new` does.
- `e2e/`: the Playwright specs, which `npm run test:e2e` runs.
- The root folders `tests/` and `e2e/` are outside the app's build (`DefaultItemExcludes` in a new app's project file), so the app's own C# does not go there.
- `scripts/`: code that runs only in Node. In the TypeScript configs `netjes new` writes, `tsconfig.app.json` checks the app's code without Node's types, so a component that reads `process` or imports `node:*` fails `npm run typecheck`; `tsconfig.node.json` checks `scripts/`, `e2e/`, `vite.config.ts` and `playwright.config.ts` with them. Do not add `node` to the `types` of `tsconfig.app.json`.

## The loop for any change

1. Write the failing test first: a Vitest component test, or a C# test for loader and action logic (in `tests/<App>.Tests`, where a new app has its test project).
2. Change the C# record, loader or action, then run `npx netjes generate` so `generated/netjes.ts` matches (a running `npm run dev` rewrites it when it restarts). Run it after updating a page package too: the app's file routes the package's pages.
3. Use the generated names in React: `pages['/pattern']`, `layouts['app/folder']`, `serverActions['name']`, `api['GET /api/...']`, `route('/pattern', params)`, `endpointUrl('GET /api/...', params)`.
4. Check: `npm test`, `npm run typecheck`, `dotnet build` (warnings are errors), `npx netjes generate --check`, and `npm run routes` for route warnings. Where the app has them, which an app made with `netjes new` does: `dotnet test tests/<App>.Tests` for the C# tests, and `npm run test:e2e` for a change to a page or a form.

## A page with data, end to end

```csharp
// Features/Posts/PostPages.cs
namespace MyApp.Features.Posts;

public static class PostPages
{
    public sealed record PostProps(string Slug, string Title, string Body);

    public static void MapPostPages(this IEndpointRouteBuilder app)
    {
        // The parameter name matches [slug]; Page.Props names the props type for the generated schema.
        app.MapPageLoader("/blog/[slug]", (string slug, PostStore posts) =>
            posts.Find(slug) is { } post
                ? Page.Props(new PostProps(post.Slug, post.Title, post.Body))
                    .WithMetadata(new PageMetadata { Title = post.Title })
                : Page.NotFound());
    }
}
```

```tsx
// app/blog/[slug]/page.tsx
import { Link, definePage } from 'netjes';
import { pages, route } from '../../../generated/netjes.ts';

export default definePage(pages['/blog/[slug]'], function Post({ title, body }) {
  return (
    <article>
      <Link href={route('/blog')}>All posts</Link>
      <h1>{title}</h1>
      <p>{body}</p>
    </article>
  );
});
```

`Program.cs` calls `app.MapPostPages();` before `app.MapPages();`. A page without a loader is a plain default export with no props.

## Rules

- Decisions live in C#: data access, validation, authorization (`.RequireAuthorization()`, `Page.Forbidden()`), redirects, caching. React renders what it is sent.
- Loaders and actions return Netjes results (`Page.Props(...)`, `Page.NotFound()`, `ServerAction.Ok(...)`), so their types reach `generated/netjes.ts`. Use records for everything that crosses to the browser.
- zod at every boundary: `definePage`/`defineLayout` with the generated schemas, `useSearchParams(schema)` and `useParams(schema)` (schemas at module level), `api[...].response.parse(...)` for fetched JSON (from `useHref(endpointUrl(...))`, never a hand-built URL), `usePublicConfig(schema)`, `Href.parse(...)` or `isHref(...)` for URLs from a CMS. A link C# sends to one of the app's pages is a `Href` in the record (`new Href("/about")`), generated as the `Href` schema, so it reaches `<Link href>` checked; a link to an endpoint or a file stays a `string`. No `any`, no casts of server data.
- Server state goes through TanStack Query, seeded from the loader with `.WithQuery(key, data)`; no `useEffect` fetching.
- Internal links use `<Link>` and `route()`; forms use `<Form>` with `useAction`, and must work without JavaScript.
- Give each page a title of its own (`metadata`, or `PageMetadata` from C#), and keep the "Skip to content" link the first thing in the root layout, `app/layout.tsx`, that takes the focus, also when the root layout is kept bare and the site's chrome is in a group layout such as `app/(site)/layout.tsx`: after a client navigation the router announces the page by its title and starts the focus over at the top ([references/routing.md](references/routing.md)).
- Never edit `generated/netjes.ts`; regenerate it and commit it with the change.
- Anything in `Netjes:PublicConfig` or shared props is sent to the browser. So is the route of each endpoint with an `endpointUrl` key, one mapped only in Development included, once any component calls `endpointUrl`: keep secrets out of routes, and authorize on the endpoint.
- Caching, streaming and static export have sharp edges: read [references/caching-and-streaming.md](references/caching-and-streaming.md) before using `.CachePage`, `Page.Defer` or `netjes export`.
- Shipping: [references/deployment.md](references/deployment.md).

## Commands

| Command | What it does |
|---|---|
| `npm run dev` | `dotnet watch` with Vite HMR on http://localhost:5080 |
| `npm test`, `npm run typecheck` | Vitest; strict TypeScript |
| `dotnet test tests/<App>.Tests` | The C# tests, in an app that has that project; no Node and no client build |
| `npm run test:e2e` | Playwright, in an app that has that script; `npx playwright install chromium` once first |
| `npx netjes generate [--check]` | Rewrite `generated/netjes.ts`; `--check` fails when it is stale |
| `npm run routes` (`netjes routes [--json]`) | The route table and route warnings, as ASP.NET Core sees them |
| `npx netjes build` | Types, then the client and server bundles |
| `npx netjes start` | Run in Production |
| `npx netjes export [dir]` | Write the site as static HTML |
| `npx netjes info` | Versions and a check of the project setup |

## More

The guides go further than these references: [routing](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/routing.md), [data loading](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/data-loading.md), [actions and forms](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/actions-and-forms.md), [page packages](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/page-packages.md), [rendering and streaming](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/rendering-and-streaming.md), [caching](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/caching.md), [images](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/images.md), [metadata](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/metadata.md), [security](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/security.md), [testing](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/testing.md) and [deployment](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/deployment.md), with the [React API](https://github.com/DennisVerhoeven/netjes/blob/main/docs/reference/react-api.md), the [C# API](https://github.com/DennisVerhoeven/netjes/blob/main/docs/reference/csharp-api.md) and the [options](https://github.com/DennisVerhoeven/netjes/blob/main/docs/reference/options.md) as reference.

The same docs as plain text: https://raw.githubusercontent.com/DennisVerhoeven/netjes/main/llms.txt (an index) and https://raw.githubusercontent.com/DennisVerhoeven/netjes/main/llms-full.txt (everything in one file). They follow the main branch; the installed version's sources and types are in `node_modules/netjes/`.
