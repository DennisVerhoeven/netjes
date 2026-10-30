# Data loading

This guide is for app developers. It covers how a page and its layouts get their data: C# loaders that run in the request, props shared by every page, public configuration, the TypeScript types and zod schemas generated from the C# types, and server state through TanStack Query. Netjes has no React Server Components: a C# loader does the work a server component would, and React renders what it is sent ([ADR 0010](../decisions/0010-app-router-with-csharp-layout-loaders.md)).

## A page loader

A page loader is a minimal API handler for a page's URL. Map it with `MapPageLoader`, using the page's pattern as its file path spells it, and return `Page.Props(...)`:

```csharp
// Features/Posts/PostPages.cs
namespace Blog.Features.Posts;

public static class PostPages
{
    public sealed record PostDetail(string Slug, string Title, IReadOnlyList<string> Body, DateOnly PublishedOn);

    public sealed record PostProps(PostDetail Post);

    public static void MapPostPages(this IEndpointRouteBuilder app)
    {
        app.MapPageLoader("/blog/[slug]", (string slug, PostStore store) =>
            store.Find(slug) is { } post
                ? Page.Props(new PostProps(new PostDetail(post.Slug, post.Title, post.Body, post.PublishedOn)))
                    .WithMetadata(new PageMetadata { Title = post.Title, Description = post.Summary })
                : Page.NotFound());
    }
}
```

```csharp
// Program.cs
app.UseNetjes();
app.UseAntiforgery();

app.MapPostPages();
app.MapPages(); // every other page file, plus the 404 fallback
```

Map loaders before `app.MapPages()`. `MapPageLoader` fails at startup when no page file matches the pattern, and lists the pages there are. Put each feature's loaders and actions in one static class under `Features/`, as the template and the examples do.

Under `npm run dev`, `dotnet watch` applies a change inside a loader to the running app. A new `MapPageLoader`, like any code that runs once at startup, waits for a restart (Ctrl+R in the terminal that runs it): until then the page renders without the new loader's props.

The loader runs for the first document and again for every client navigation to the page: the same endpoint answers both, with HTML for a document and the page's payload as JSON for the client router.

## Reading props in a page

[`definePage`](../reference/react-api.md#definepage) takes the page's schema from the generated file and the component. It validates the props before rendering, so the component gets parsed, typed props instead of trusting the JSON:

```tsx
// app/blog/[slug]/page.tsx
import { Link, definePage } from 'netjes';
import { pages, route } from '../../../generated/netjes.ts';

export default definePage(pages['/blog/[slug]'], function Post({ post }) {
  return (
    <article>
      <Link href={route('/blog')}>All posts</Link>
      <h1>{post.title}</h1>
      {post.body.map((paragraph) => <p key={paragraph}>{paragraph}</p>)}
    </article>
  );
});
```

`pages['/blog/[slug]']` is the zod schema of `PostProps`, written from the loader's return type. Props the schema rejects throw `Invalid props: ...` to the nearest error boundary. `definePage` works with any [Standard Schema](https://standardschema.dev) library, so a hand-written valibot or arktype schema works as well. Dates arrive as ISO strings (`z.iso.date()`, `z.iso.datetime()`), as System.Text.Json writes them.

A page without a loader renders with no props, and its schema is `z.object({})`.

## What a loader returns

| Result | What it does |
|---|---|
| `Page.Props(props)` | Render the page with these props. Returns `PageResult<T>`, which names the props type for the generated schema. |
| `Page.Props()` | Render the page with no props. |
| `Page.NotFound()` | Render the nearest `not-found.tsx`, in the page's folder or one above it and inside the layouts above that file, else the root 404 page, with status 404. |
| `Page.Forbidden()` | Render the nearest `forbidden.tsx` (else the error page) with status 403. |
| `Page.Unauthorized()` | Render the nearest `unauthorized.tsx` (else the error page) with status 401. |
| `Page.Redirect(url, permanent: false)` | Redirect: 307, or 308 when permanent, for a GET; 303 after a form post. The client router follows it without a full page load. |
| `Page.LocalRedirect(url, permanent: false)` | Like `Redirect`, but throws unless the URL is a path on this site. Use it for a `returnUrl` from the request. |
| `Page.Render(pattern, props)` | Render the page for `pattern` from any endpoint, such as a minimal API that is no page loader. |

On a `PageResult`:

| Method | What it does |
|---|---|
| `.WithStatus(code)` | Render with another status, such as 422 for a form shown again with errors. |
| `.WithMetadata(new PageMetadata { ... })` | Title, description, Open Graph and other head tags from C# ([Metadata](metadata.md#metadata-from-c)). |
| `.WithQuery(key, data)` | Seed the page's TanStack Query cache ([Server state](#server-state-with-tanstack-query)). |
| `.Props` | On `PageResult<T>`: the props, for a unit test of the loader; null for a redirect or a not-found. |

Every untyped result converts to `PageResult<T>`, so a loader can return either from the same lambda, and C# infers `PageResult<PostProps>` as its type. That inferred type is how the generator knows the props.

`Page.Redirect` accepts relative URLs and http(s) URLs. It throws for any other scheme, and for a `%` before the first `:` or `/`, which could hide a scheme (`javascript%3A`); start a relative URL with `/` or `./`. Root-relative URLs get the app's base path. `Page.IsLocalUrl(url)` checks a URL by the same rules as `LocalRedirect`, for a fallback instead of an exception:

```csharp
private static PageResult<AuthPageProps> AuthPage(string? returnUrl, ClaimsPrincipal user)
{
    var target = Page.IsLocalUrl(returnUrl) ? returnUrl : null;
    return SignedInUser.From(user) is null
        ? Page.Props(new AuthPageProps(target))
        : Page.LocalRedirect(target ?? "/projects");
}
```

## Binding

A page loader binds like a minimal API GET handler. Its parameters can be:

- route values, by name in any case: `string slug` for `[slug]`, and the parameters of a route group the loader is mapped in (`app.MapGroup("/{tenant}")`);
- the query string, for simple parameters that are not route values: `string? q`, `int? page`, `string[] tags`;
- services from DI: a store, a `DbContext`, `TimeProvider`;
- `HttpContext`, `ClaimsPrincipal`, `CancellationToken`;
- anything with a binding attribute: `[FromQuery]`, `[FromHeader]`, `[FromRoute(Name = "slug")]`, `[FromServices]`, `[AsParameters]`.

```csharp
app.MapPageLoader("/projects/[id]", async (
    string id, string? q, string? priority, bool? mine, int? done, ClaimsPrincipal user, TaskBoardDb db, CancellationToken cancellationToken) =>
{
    // ...
});
```

A route value binds only to a single value: a catch-all's value is its segments joined by `/` (`"getting-started/routing"`), and an optional catch-all without segments is `null`. A parameter whose name matches no route value binds from the query string, so `(string id)` on `/blog/[slug]` reads `?id=`. Netjes warns about that at startup ([Route warnings](routing.md#route-warnings)).

Minimal APIs refuse an inferred request body on a GET endpoint, so a loader reads none. A complex type that is not a registered service needs `[AsParameters]`, which binds its properties from the route and the query string, or a static `BindAsync` method that binds it from the request:

```csharp

public sealed record TaskFilter(string? Q, string? Priority);

// Binds filter.Q and filter.Priority from ?q=login&priority=high.
app.MapPageLoader("/projects/[id]", (string id, [AsParameters] TaskFilter filter, TaskBoardDb db) => ...);
```

Without `[AsParameters]`, minimal APIs would take `TaskFilter` for the request body, so `MapPageLoader` stops the app at startup with an `InvalidOperationException` that names the page, the parameter and its type, and the fixes that fit the type:

```text
The loader of app/projects/[id]/page.tsx (/projects/[id]) cannot bind parameter 'filter' of type TaskFilter: it is not a registered service, has no binding attribute and does not bind itself, so minimal APIs would read it from the request body, which loaders do not read. Mark the parameter [AsParameters] to bind the properties of TaskFilter from the route and the query string, give TaskFilter a static BindAsync method to bind it yourself, or register TaskFilter as a service.
```

An interface that was never registered as a service gets such a message too, and so does a list: take an array instead (`int[] ids`, not `List<int> ids`), which binds from repeated query string values. `IEnumerable<int> ids` stops the app as well, with a message of its own: the service container supplies any `IEnumerable<T>`, an empty one when nothing is registered, so minimal APIs would bind it from services and never read the query string. Take `int[] ids` or `[FromQuery] int[] ids`, or mark the parameter `[FromServices]` when a service is meant. A parameter that reads the body explicitly stops the app as well, with a message that names it: `[FromBody]`, `[FromForm]`, or a file, files or the form (`IFormFile`, `IFormFileCollection`, `IFormCollection`), as a parameter or as a member of an `[AsParameters]` type. Minimal APIs would bind it on a GET endpoint too, but a navigation carries no body, so every request for the page would fail. Read the value from the route, the query string or a service, and posted data in a [page action](actions-and-forms.md#page-actions) or a [server action](actions-and-forms.md#a-server-action). A page loader also stops the app, naming the parameter, when a type that binds itself adds accepts metadata for a request body (`IAcceptsMetadata`, through `IEndpointParameterMetadataProvider` or `IEndpointMetadataProvider`), as a type might that reads a JSON body on a POST endpoint: routing then matches the page only for requests with that Content-Type, so every navigation, which has none, would get the not-found page. Accepts metadata on the endpoint itself, from `.Accepts<T>()` on the builder `MapPageLoader` returns or `[Consumes]` on the loader, does the same and is not caught at startup, so leave it off a page loader. Any other parameter minimal APIs refuse, such as a `[FromRoute]` naming no route value, stops the app with minimal APIs' own reason. `netjes generate` and `netjes routes` start the app as well, so they print the message and exit with a non-zero code ([the netjes command](../reference/cli.md#netjes-generate)).

`MapPageLoader` returns a `RouteHandlerBuilder`, so endpoint conventions work as usual: `.RequireAuthorization()`, `.CachePage(...)` ([Caching](caching.md)), `.AddEndpointFilter(...)`, `.StaticParams(...)` and `.ExcludeFromExport()` ([Static export](static-export.md)), and `.ExcludeFromSitemap()` ([Metadata](metadata.md#sitemap-and-robots)).

## Layout loaders

A layout loader is the C# loader of a `layout.tsx`. Map it by the layout's URL, or by its folder when route groups put several layouts at one URL:

```csharp
// app/docs/layout.tsx: the table of contents.
app.MapLayoutLoader("/docs", (DocsStore docs) =>
    Page.Props(new DocsLayoutProps([.. docs.All.Select(d => new DocLink(d.Path, d.Title))])));

// app/(app)/layout.tsx: (app) and (marketing) both sit at "/".
app.MapLayoutLoader("app/(app)", async (ClaimsPrincipal user, TaskBoardDb db, CancellationToken cancellationToken) =>
        Page.Props(new ShellProps(await SidebarProjectsAsync(db, user.UserId(), cancellationToken))))
    .RequireAuthorization();
```

The layout declares its props with `defineLayout` and the generated `layouts` schema, keyed by folder. The component gets them next to `children` and `params`:

```tsx
// app/docs/layout.tsx
import { Link, defineLayout } from 'netjes';
import { layouts } from '../../generated/netjes.ts';

export default defineLayout(layouts['app/docs'], function DocsLayout({ toc, children }) {
  return (
    <div className="grid md:grid-cols-[14rem_1fr]">
      <nav aria-label="Documentation">
        {toc.map((entry) => <Link key={entry.path} href={`/docs/${entry.path}`}>{entry.title}</Link>)}
      </nav>
      <div>{children}</div>
    </div>
  );
});
```

A layout loader returns a `PageResult`, as a page loader does:

- `Page.Props(...)` gives the layout its props. `.WithQuery(...)` seeds the query cache from here too.
- `Page.NotFound()`, `Page.Forbidden()` and `Page.Unauthorized()` render the nearest boundary above the layout, outside it: the TaskBoard project layout returns `Forbidden` for non-members, and `app/(app)/projects/forbidden.tsx` renders it in the app shell.
- `Page.Redirect(...)` redirects the page.

Conventions on the result apply to every page below the layout. `.RequireAuthorization()` on the loader of `app/(app)/layout.tsx` protects every page in `(app)` before any loader runs. A user who fails it gets the section's own `unauthorized.tsx` or `forbidden.tsx` (or the nearest above), rendered inside the layouts above the section, and the section's loader never runs.

`MapLayoutLoader` throws at startup when no `layout.tsx` serves the URL or folder, when several do (pass the folder instead), and when a layout already has a loader.

### What a layout loader binds

A layout loader binds like a page loader, with two limits:

- **Only the route values of its own URL.** The loader of `app/projects/[id]/layout.tsx` binds `id`, never the `taskId` of `/projects/[id]/tasks/[taskId]`, as a Next.js layout gets the params of its own segment and those above it. A `[FromRoute(Name = "taskId")]` on it, as a parameter or an `[AsParameters]` member, stops the app at startup, naming the layout and the parameter. A plain `(string taskId)` binds `?taskId=` from the query string, and the route warnings point it out.
- **No request body.** Navigations are GETs, and a page action's render posts a form, so nothing is inferred from a body: arrays bind from the query string, and a complex type binds only as a registered service, with `[AsParameters]`, or by itself (`BindAsync`). A complex type that binds none of these ways stops the app at startup, naming the layout file and its URL, as on a page loader ([Binding](#binding)). So does a parameter that reads the body explicitly (`[FromBody]`, `[FromForm]`, `IFormFile`, `IFormFileCollection` or `IFormCollection`): a page action's render posts a form, but a navigation has no body to read. A type that binds itself and adds accepts metadata for itself binds as usual: a layout loader is called while the page renders, and routing never matches it.

### Partial navigation

On a client navigation the router sends the keys of the layouts it holds, and the server skips each loader whose key is unchanged; the client keeps that layout's props. Moving between docs pages loads only the page, while the table of contents stays.

A key is an HMAC, signed with `NetjesOptions.SegmentKeySecret`, of the layout's folder, the client build, the user (authentication type, name, identifier and roles) and the route values of the layout's own URL. A layout loader runs again when one of those changes, and is skipped otherwise. Documents, form posts, `router.refresh()` and the refresh after a server action load every layout in full.

A loader must not depend on anything its key does not hold: the query string, cookies, other route values, anything read through `HttpContext`. A loader that reads them keeps what it loaded first while the client navigates within its section.

`SegmentKeySecret` is random per process unless you set it (at least 32 bytes). Instances behind a load balancer then do not accept each other's keys and load layouts in full; set the same secret on every instance to share them ([Deployment](deployment.md)).

### Access checks belong in the page loader too

Checks that must see every change belong in authorization conventions, which run on every request, or in the page loader:

- A layout loader is skipped while its key holds, so a change it would notice and the key does not hold, such as a membership revoked in the database, goes unseen until the user or the route values change.
- A layout loader sees only its own URL's route values, so a check on a deeper value (the task in `/projects/[id]/tasks/[taskId]`) cannot be made there. Reading that value through `HttpContext` or a type that binds itself is not caught at startup, and the check is skipped on navigations within the section.
- A page's loader runs before its layouts' loaders. A layout that returns `Forbidden` keeps the page's props from the browser, but the page loader has already done its work.

TaskBoard checks membership in the project layout and again in each page loader below it:

```csharp
app.MapPageLoader("/projects/[id]/tasks/[taskId]", async (
    string id, string taskId, ClaimsPrincipal user, TaskBoardDb db, IDbContextFactory<TaskBoardDb> contexts, HttpContext http) =>
{
    var access = await db.AccessAsync(id, user.UserId(), http.RequestAborted);
    if (access is null)
    {
        return Page.NotFound();
    }

    if (!access.IsMember)
    {
        return Page.Forbidden();
    }

    // ...
});
```

## Protecting pages

Authorization is ASP.NET Core's, on the endpoints:

```csharp
app.MapPageLoader("/account", ...).RequireAuthorization();          // one page with a loader
app.MapLayoutLoader("/dashboard", ...).RequireAuthorization();      // every page below app/dashboard/
app.MapPages().RequireAuthorization();                              // every page without a loader of its own
```

A fallback policy (`AddAuthorizationBuilder().SetFallbackPolicy(...)`) covers every endpoint without authorization metadata, and `.AllowAnonymous()` makes an exception. Authorization runs before any loader. An empty 401 or 403 from it renders `unauthorized.tsx` or `forbidden.tsx` ([Routing](routing.md#error-not-found-forbidden-and-unauthorized-pages)); with cookie authentication, answer those statuses from `OnRedirectToLogin` and `OnRedirectToAccessDenied` instead of redirecting. Checks that depend on data go in the loader, which returns `Page.Forbidden()` or `Page.NotFound()`.

Call `app.UseAuthentication()` and `app.UseAuthorization()` after `app.UseNetjes()`, and `app.UseAntiforgery()` after them.

## Shared props

Shared props reach every page and layout, with the first document and with every navigation: the signed-in user, the site's name, the main navigation. Set them with `UseSharedProps`, which also records their type for the generated `sharedProps` schema:

```csharp
builder.Services.AddNetjes(options =>
    options.UseSharedProps(http => ValueTask.FromResult(new SharedProps(SignedInUser.From(http.User)))));
```

```tsx
import { useSharedProps } from 'netjes';
import { sharedProps } from '../generated/netjes.ts';

const { user } = useSharedProps(sharedProps);
```

Everything in them is sent to the browser. An output-cached page stores them with the page and serves them to every visitor, so derive them from the user (a signed-in user bypasses the output cache), never from cookies or other per-visitor state ([Caching](caching.md)). `NetjesOptions.SharedProps` is the untyped form, for when the type is not known; the generated `sharedProps` is then `z.unknown()`.

## Public configuration

`Netjes:PublicConfig` is a configuration section sent to the browser with the first document. Set it in `appsettings.json`, an environment variable (`Netjes__PublicConfig__ApiUrl`) or any other configuration source, and it changes per environment without rebuilding the client ([ADR 0009](../decisions/0009-public-config-at-runtime.md)):

```json
{
  "Netjes": {
    "PublicConfig": {
      "ApiUrl": "https://api.example.com",
      "Features": { "NewEditor": "true" }
    }
  }
}
```

```tsx
import { usePublicConfig } from 'netjes';
import { z } from 'zod';

const PublicConfig = z.object({
  apiUrl: z.url(),
  features: z.object({ newEditor: z.stringbool() }),
});

const config = usePublicConfig(PublicConfig);
```

Keys follow the app's JSON naming policy (camelCase by default), nested sections become objects, and sections keyed `0`, `1`, `2` become arrays. Values are strings, as configuration values are, so use coercing schemas (`z.coerce.number()`, `z.stringbool()`). Everything in the section is public: keep secrets in other configuration.

## Generated types

`generated/netjes.ts` holds zod schemas, and the TypeScript types inferred from them, for everything C# sends the browser:

| Export | What it is |
|---|---|
| A schema and a type per C# type | `export const PostProps = z.object({ ... })` and `export type PostProps` |
| `pages` | Each page's props by pattern: `pages['/blog/[slug]']` |
| `layouts` | Each layout loader's props by folder: `layouts['app/docs']` |
| `sharedProps` | The type given to `UseSharedProps<T>` |
| `api` | Request and response schemas of JSON minimal APIs, by method and route: `api['GET /api/projects/{slug}/stats']` |
| `forms` | The `[FromForm]` type of each page action, by pattern: `forms['/guestbook']` |
| `serverActions` | A typed reference to each `MapAction` ([Actions and forms](actions-and-forms.md#calling-an-action-from-react)) |
| `route()`, `Route`, `Href`, `isHref()`, `RouteParams`, `RoutePattern`, `routePatterns` | Typed routes, from the page files ([Typed links](#typed-links)) |

The schemas are written from the System.Text.Json contract the app serializes with (`ConfigureHttpJsonOptions`), so property names, nullability, ignored properties, enums (strings with `JsonStringEnumConverter`, numbers without), `[JsonDerivedType]` unions and number handling match the JSON on the wire. Recursive types become zod getters, and a C# type whose name the file already uses (`Record`, `Route`) gets another name. Server action inputs are typed by what System.Text.Json reads: nullable and defaulted properties are optional, computed ones are left out ([ADR 0006](../decisions/0006-types-and-zod-from-csharp-contracts.md)).

The app writes the file when it starts in Development, when its content changed, so a running `npm run dev` picks up a C# change on its next restart. `npx netjes generate` writes it on demand, also while `npm run dev` runs, and `npx netjes generate --check` exits 1 when it is out of date: run that in CI ([the netjes command](../reference/cli.md#netjes-generate)). Commit the file and never edit it by hand. `NetjesOptions.TypesFile` moves it; `null` turns generation off.

`netjes generate` and `netjes routes` run the app once in command mode: it binds only a throwaway loopback port, starts no Node sidecar, needs no build, writes its result and exits. Skip startup work those commands do not need, such as migrating a database:

```csharp
if (app.Environment.IsDevelopment() && app.GetNetjesCommand() is null)
{
    await Database.MigrateAndSeedAsync(app.Services);
}
```

### Typed links

The generated file registers the app's routes with `netjes`, which types the `href` of `<Link>` and `router.push`. A path no page serves, or a missing parameter, is a type error:

```tsx
import { Link } from 'netjes';
import { Href, route } from '../generated/netjes.ts';

<Link href={route('/projects/[id]', { id: project.slug })}>{project.name}</Link>
<Link href={`/projects/${project.slug}`}>{project.name}</Link>   // a template literal matches /projects/${string}
<Link href={Href.parse(item.href)}>{item.label}</Link>            // a URL from data, checked at the boundary
```

`Route` is the union of URLs the pages serve (`` '/about' | `/blog/${string}` ``), and `Href` adds a query or fragment, `?…` and `#…` on their own, and `http(s):`, `mailto:` and `tel:` URLs. `route(pattern, params, search)` builds an encoded URL. `isHref(value)` and the `Href` zod schema check a string that comes from data, such as a CMS or shared props.

## JSON endpoints

An API is a minimal API endpoint next to the pages. Return `TypedResults` (or declare `Results<...>`) and the endpoint gets request and response schemas in the generated `api` map, keyed by method and route template as written:

```csharp
app.MapGet("/api/projects/{slug}/stats", async Task<Results<Ok<ProjectStats>, NotFound, ForbidHttpResult>> (
        string slug, ClaimsPrincipal user, TaskBoardDb db, CancellationToken cancellationToken) =>
    await db.AccessAsync(slug, user.UserId(), cancellationToken) switch
    {
        null => TypedResults.NotFound(),
        { IsMember: false } => TypedResults.Forbid(),
        var access => TypedResults.Ok(await StatsAsync(db, access.ProjectId, cancellationToken)),
    })
    .RequireAuthorization();
```

```ts
import { api } from '../generated/netjes.ts';

async function fetchStats(slug: string) {
  const response = await fetch(`/api/projects/${encodeURIComponent(slug)}/stats`);
  if (!response.ok) throw new Error(`Stats request failed with ${response.status}`);
  return api['GET /api/projects/{slug}/stats'].response.parse(await response.json());
}
```

A route group's `"/"` keeps its slash in the key: `app.MapGroup("/api/posts/{slug}/likes").MapGet("/", ...)` is `api['GET /api/posts/{slug}/likes/']`. A JSON endpoint that browsers post to should require a JSON body, so a cross-site form post cannot reach it.

## Server state with TanStack Query

Netjes provides a TanStack Query `QueryClient`: one per render on the server, and one per page load in the browser, which lasts across client navigations. There is no provider to set up. A loader seeds it with `.WithQuery(key, data)`, and a component that calls `useQuery` with the same key renders that data on the server and on the first client render, without waiting for a fetch:

```csharp
app.MapPageLoader("/blog/[slug]", (string slug, PostStore store) =>
    store.Find(slug) is { } post
        ? Page.Props(new PostProps(ToDetail(post)))
            .WithQuery(["likes", slug], new LikesResponse(slug, store.LikesOf(slug)))
        : Page.NotFound());
```

```tsx
import { useQuery } from '@tanstack/react-query';

const likes = useQuery({ queryKey: ['likes', slug], queryFn: () => fetchLikes(slug) });
// likes.data is there on the first render
```

Keep each query key in one place on each side (a function in C#, a key factory in TypeScript), so the seed and the query match. Layout loaders can seed queries too.

- Seeded data is fresh for the client's `staleTime`, one minute by default, and is fetched again once stale. Data the browser fetched more recently is kept: an older payload, such as an output-cached page or a page shown again on back, does not replace it. Netjes moves the server's timestamps onto the browser's clock first, so a clock that is off does not make data look older or newer than it is.
- After a server action, `.Invalidate(key)` refetches the queries under a key and `.WithQuery(key, data)` sets one, without a refetch ([Actions and forms](actions-and-forms.md#revalidation)).
- An action or form post that signs someone in or out clears the client, seeded queries included, in every tab of the app ([Signing in and out](actions-and-forms.md#signing-in-and-out)).
- Configure the client in `vite.config.ts` with `netjes({ query: { defaultOptions } })`, or remove it with `netjes({ query: false })` ([Vite plugin](../reference/vite-plugin.md)).

## Slow data: `Page.Defer`

A value that is slow and not essential, such as comments or an activity feed, can follow the page instead of holding it up. Make the prop a `Deferred<T>` and fill it with `Page.Defer(...)`:

```csharp
public sealed record TaskPageProps(TaskDetail Task, Deferred<IReadOnlyList<ActivityItem>> Activity);

// A task of its own: give it its own DbContext, never the request's.
Page.Props(new TaskPageProps(task, Page.Defer(ActivityAsync(contexts, projectId, number, http.RequestAborted))));

// Or let Netjes run the work in a DI scope of its own, started once the page's payload is written:
// a scoped DbContext from that scope is not shared with the request's loaders.
Page.Props(new TaskPageProps(task, Page.Defer(services =>
    LoadActivityAsync(services.GetRequiredService<TaskBoardDb>(), projectId, number, http.RequestAborted))));
```

The generated schema types the prop with `deferred(...)` from `netjes/zod`, as a promise whose value is validated when it arrives. Read it with React's `use()` inside a Suspense boundary, or let the folder's `loading.tsx` be the fallback:

```tsx
function ActivityFeed({ activity }: { activity: PromiseLike<ActivityItem[]> }) {
  const items = use(activity);
  return <ol>{items.map((item) => <li key={item.id}>{item.summary}</li>)}</ol>;
}

<Suspense fallback={<p>Loading activity…</p>}>
  <ActivityFeed activity={activity} />
</Suspense>
```

The page goes out first and each value streams in when its task completes, on documents and client navigations alike. [Rendering and streaming](rendering-and-streaming.md) covers what crawlers, the static export and browsers without JavaScript get, timeouts and errors.
