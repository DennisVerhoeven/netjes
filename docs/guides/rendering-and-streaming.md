# Rendering and streaming

This guide is for app developers who want to know how a Netjes page becomes HTML: the Node processes that render React, streamed documents and `loading.tsx`, slow data with `Page.Defer`, what crawlers get, what happens when no Node process is ready, components that load late or only in the browser, and the error pages. Routing and loaders have guides of their own: [routing](routing.md) and [data loading](data-loading.md).

## How a page is rendered

A request for a page goes through ASP.NET Core first. The endpoint for the page file runs its C# loaders (the layouts' and the page's), and Netjes builds the page payload: which page and layouts to show, their props, the route parameters, the shared props and the seeded queries. The next step depends on who asked:

- A document request (a browser loading the URL) sends the payload to a Node process, the sidecar, which renders the whole HTML document with React and streams it back. The payload is embedded in the document, and the browser hydrates it.
- A client navigation (`<Link>`, `router.push`, a `<Form>` post) asks the same URL for the payload as JSON. No Node process is involved: the browser loads the page module and renders it itself.

Only document requests need the sidecar. Redirects, not-found and forbidden results, authorization and the response status are all decided in .NET before anything is rendered.

## The sidecar pool

Netjes starts the Node processes when the app starts and stops them when it stops. You do not run or deploy them yourself: `UseNetjes()` and `AddNetjes()` are enough, as long as `node` is on the path (`NetjesOptions.NodeExecutable` names another one).

- In development there is one process. It also runs the Vite dev server (port 5173, `ViteDevPort`), which compiles your pages on demand, so an edit shows on the next request.
- In production each process imports the server build in `dist/server/`. `NetjesOptions.SidecarProcesses` sets how many run (default 1). A process renders one page at a time on one core, so an app whose pages take real CPU time to render wants about one per core. Each render goes to the ready process with the fewest renders in flight.

```json
{
  "Netjes": {
    "SidecarProcesses": 4
  }
}
```

The app does not start until every process has reported ready, within `StartupTimeout` (60 seconds). When a process cannot be started at all (Node.js is not found, or the sidecar script is not installed or built), or in development the Vite dev server does not start, the app logs why in one log entry, with what to do, and exits with code 1. A process listens on a random port on `127.0.0.1`, accepts only requests that carry the token .NET gave it, and exits when the app's end of its standard input closes, so it never outlives the app.

### Timeouts

| Option | Default | Limits |
|---|---|---|
| `RenderTimeout` | 15 s | How long a process may take to render the shell of a streamed document: everything outside Suspense boundaries that are still waiting. |
| `StreamTimeout` | 60 s | How long a whole document may take, deferred values and Suspense boundaries included. A complete document (for a crawler, below) waits up to this long. |

A render that runs out of time fails with the error page.

### Restarts

A process that exits is restarted after 1 second, then after 2, 4, 8 and 16 seconds for each crash in a row, and after 30 seconds from then on. The delay goes back to 1 second once a process has stayed up for 30 seconds. Each exit is logged with the last lines the process printed.

Renders while a process is down go like this:

- A render that has already been sent to the process when it dies fails with the error page. Netjes never sends it again, because it may be the render that brought the process down.
- A render that finds its process gone before anything was sent goes to another ready process, or, when there is none, waits for the restart within its own `RenderTimeout` (`StreamTimeout` for a complete document). A render with deferred values stays on the process it started on and waits for that one.
- In production, a document request that finds no process ready at all does not wait: it gets the [client-rendered fallback](#when-no-sidecar-is-ready).

Waiting for a restart applies with `ClientRenderFallback` off, in development, to `netjes export`, and to the render that is first to find its process gone. Other production documents get the fallback.

### Health

`AddNetjes()` on the health checks builder adds a check named `netjes`:

```csharp
builder.Services.AddHealthChecks().AddNetjes();
// ...
app.MapHealthChecks("/healthz");
```

It reports Healthy when every process is ready, Degraded while some are restarting (renders go to the others), and Unhealthy when none is ready. The template's Docker health check calls `/healthz`. See [deployment](deployment.md#health-checks).

## Streamed documents

A document streams. The sidecar sends the shell as soon as it is ready, with a fallback in place of each Suspense boundary that is still waiting, and sends the content of those boundaries as it becomes ready. The browser hydrates the shell while the rest is still arriving, so the page is interactive before its slow parts are there.

A boundary is either a `loading.tsx` file, which wraps the page below its folder in `<Suspense fallback={<Loading />}>`, or a `<Suspense>` you write in a component. A page waits inside a boundary for two things: a `Page.Defer` value it reads with `use()`, and code that loads late (`React.lazy`, `dynamic()`).

A boundary whose content is ready when the shell goes out is written in place, at its position in the HTML, however large it is. So a page that waits on nothing renders complete, and shows without JavaScript. Only what is still waiting comes later, after the shell, with a small inline script that puts it in place of the fallback. Without JavaScript that script does not run, and the fallback stays. ([ADR 0012](../decisions/0012-streamed-documents-and-deferred-values.md) explains the choice.)

Under a hash-based Content-Security-Policy, which output-cached pages need, those inline scripts are blocked, so such pages must not stream. See [security](security.md#content-security-policy).

## Slow data with Page.Defer

`Page.Defer` puts a value in the props that is still being computed. The page renders without waiting for it, and the value streams to the page when it is ready, like a slow `fetch` behind a Suspense boundary in a Next.js server component. Use it for parts of a page that can come a moment later: comments, an activity feed, recommendations.

```csharp
public sealed record PostProps(PostDetail Post, Deferred<IReadOnlyList<PostSummary>> Related);

app.MapPageLoader("/blog/[slug]", (string slug, PostStore store, HttpContext http) =>
    store.Find(slug) is { } post
        ? Page.Props(new PostProps(
            new PostDetail(post.Slug, post.Title, post.Body, post.Tags, post.PublishedOn),
            Page.Defer(RelatedAsync(store, slug, http.RequestAborted))))
        : Page.NotFound());
```

The prop's type is `Deferred<T>`. In the generated types it is a `deferred()` schema, which `definePage` parses into a thenable. Read it with React's `use()` inside a Suspense boundary:

```tsx
import { definePage } from 'netjes';
import { Suspense, use } from 'react';
import { type PostSummary, pages } from '../../../generated/netjes.ts';

function Related({ related }: { related: PromiseLike<PostSummary[]> }) {
  const posts = use(related);
  return <ul>{posts.map((post) => <li key={post.slug}>{post.title}</li>)}</ul>;
}

export default definePage(pages['/blog/[slug]'], function Post({ post, related }) {
  return (
    <article>
      <h1>{post.title}</h1>
      <Suspense fallback={<p>Finding related posts…</p>}>
        <Related related={related} />
      </Suspense>
    </article>
  );
});
```

The value is validated against its schema when it arrives. `deferred(schema)` from `netjes/zod` builds the same schema by hand.

### The two overloads

| Overload | When the work starts | Services |
|---|---|---|
| `Page.Defer(Task<T> task)` | When you call it: you hand over a task that is already running. | Whatever the task captured, in the request's own scope. |
| `Page.Defer(Func<IServiceProvider, Task<T>> load)` | Once the page's payload is written, after the loaders have finished. | A DI scope of its own, disposed when the value is done. |

A task that uses a scoped service runs at the same time as the rest of the request, and an EF Core `DbContext` must not be used by two operations at once. The TaskBoard example creates a context of its own for the deferred work, from an `IDbContextFactory<TaskBoardDb>`:

```csharp
var activity = Page.Defer(ActivityAsync(contexts, access.ProjectId, number, http.RequestAborted));

static async Task<IReadOnlyList<ActivityItem>> ActivityAsync(
    IDbContextFactory<TaskBoardDb> contexts, Guid projectId, int number, CancellationToken cancellationToken)
{
    await using var db = await contexts.CreateDbContextAsync(cancellationToken);
    return await db.Activity
        .Where(a => a.ProjectId == projectId && a.TaskNumber == number)
        .OrderByDescending(a => a.At)
        .Take(30)
        .Select(a => new ActivityItem(a.Id, a.Summary, a.Actor.Name, a.At))
        .ToListAsync(cancellationToken);
}
```

The second overload does the same without the factory: it hands the work a service provider of its own scope, so the `TaskBoardDb` it resolves is not the one the loaders use.

```csharp
var activity = Page.Defer(services => ActivityAsync(
    services.GetRequiredService<TaskBoardDb>(), access.ProjectId, number, http.RequestAborted));

static async Task<IReadOnlyList<ActivityItem>> ActivityAsync(
    TaskBoardDb db, Guid projectId, int number, CancellationToken cancellationToken) =>
    await db.Activity
        .Where(a => a.ProjectId == projectId && a.TaskNumber == number)
        .OrderByDescending(a => a.At)
        .Take(30)
        .Select(a => new ActivityItem(a.Id, a.Summary, a.Actor.Name, a.At))
        .ToListAsync(cancellationToken);
```

`Page.Defer` works in the props of pages and layouts, anywhere in the props object, and in any number.

### On the wire

- A document carries a placeholder for each value, and the value follows in the same response, as a small inline script, once it is ready.
- A client navigation gets NDJSON instead of JSON: the payload on the first line, then one line per value as it completes.

A value that throws, or has not arrived when `StreamTimeout` runs out, rejects on the client, and `use()` throws it to the nearest `error.tsx`. In production the message is "Something went wrong"; in development it is the exception's message. The failure is logged as a warning (category `Netjes.Deferred`).

## Complete documents

Some readers get the whole document at once, with every boundary in place and no fallbacks:

- Crawlers and link previews, whose `User-Agent` matches `NetjesOptions.CrawlerUserAgents` (a case-insensitive regular expression; the default covers search engine bots, social previews, Lighthouse and the like).
- Every document, when `NetjesOptions.StreamDocuments` is `false`.
- `netjes export`, which writes complete documents to files (see [static export](static-export.md)).

The response then starts once every deferred value has arrived, up to `StreamTimeout`. The values still follow as inline scripts, for the browser to hydrate what they rendered. Client navigations stream their NDJSON either way.

## When no sidecar is ready

In production, a document request that finds no Node process ready (they have all crashed and are restarting) does not wait for one. Loaders and authorization run as usual and the response has the status it would have had, but .NET writes the document itself: a head with the stylesheets, the client entry and the CSP nonce, the `<title>`, description and robots from the loader's `PageMetadata`, and a body with only the page payload. The browser renders the page instead of hydrating it, and `Page.Defer` values stream in after it as usual. ([ADR 0013](../decisions/0013-client-rendered-fallback.md) has the reasoning.)

- Each such document logs a warning, `No Node sidecar was ready to render {Route} ({Path}); the page was sent for the browser to render.`, and adds one to the `netjes.sidecar.fallbacks` counter on the `Netjes` meter, tagged `netjes.route`.
- It is never stored in the output cache, and it goes out with `Cache-Control: no-store` in place of any header the page set, and without the headers a CDN reads ahead of `Cache-Control` (`CDN-Cache-Control`, `Surrogate-Control` and the others listed in [caching](caching.md#cdns-and-proxies)), so no browser, and no CDN that goes by the response's headers, keeps it once the processes are back.
- Visitors without JavaScript, and crawlers and link previews that run none, get an empty page with the title, description and robots from C#. Layout title templates, and the metadata that page and layout files export, apply once the page renders.
- The health check reports Unhealthy while it lasts.
- A render that reached a process and failed there, or timed out, still gets the error page.
- Development never falls back, since its process also runs the Vite dev server the browser loads from, and neither does `netjes export`.

`NetjesOptions.ClientRenderFallback = false` (`Netjes__ClientRenderFallback=false`) turns it off: such a request then waits for a restart, up to `RenderTimeout` (`StreamTimeout` for a complete document), and gets the error page if none comes.

## Components that load late or only in the browser

`dynamic()` loads a component's module on demand, in a chunk of its own, like `next/dynamic`:

```tsx
import { dynamic } from 'netjes';

const Chart = dynamic(() => import('./Chart.tsx'), { loading: Spinner });
const Map = dynamic(() => import('./Map.tsx'), { ssr: false, loading: Spinner });
```

With `ssr` (the default), the component renders on the server like any other; on a page the browser has not loaded the chunk for yet, it suspends until the module is there. With `ssr: false`, the server and the first render in the browser show `loading`, and the component renders after hydration: use it for code that needs `window` or a browser-only library.

`clientOnly(Component, { fallback })` renders an existing component only in the browser, after hydration, and `fallback` (default nothing) on the server and before that.

Both are in the [React API reference](../reference/react-api.md).

## Error pages

If something fails while Netjes serves a page, it renders the nearest boundary file instead, with the response's status:

- An exception in a loader, in an action without JavaScript, or in the React render: the nearest `error.tsx` above the segment that failed, inside the layouts above it, with status 500 (or the status of a `BadHttpRequestException`). A layout loader that throws renders the boundary above its own layout.
- `Page.NotFound()`, `Page.Forbidden()`, `Page.Unauthorized()`, and empty 401, 403 and 404 responses (from authorization, say): the nearest `not-found.tsx`, `forbidden.tsx` or `unauthorized.tsx`, else the error page. A user who fails a layout loader's `RequireAuthorization(...)` gets that section's own boundary (or the nearest above it) inside the layouts above the section, and the section's loader never runs.
- If the error page fails to render too: `app/global-error.tsx`, without any layout. If that is missing or fails as well: a plain HTML page with the status.

An `error.tsx` gets `{ error, reset }`, with the status as `error.status`. In production `error.message` is the status's reason phrase ("Internal Server Error"), so nothing about the exception reaches the browser. In development it is the exception's message, and the stack is there too: the JavaScript stack for a render that failed in the sidecar (`SsrRenderException.JsStack`), the .NET one otherwise. `NetjesOptions.IncludeErrorDetails` sets this either way. Both are plain text, without the terminal colour codes Vite puts in its errors, and the message can take several lines, such as the code frame Vite reports for a syntax error in a page: show it with `white-space: pre-wrap` (`whitespace-pre-wrap` in Tailwind CSS), as the built-in error page and the template's `error.tsx` do.

Every such failure is logged: unhandled exceptions as errors, bad requests as warnings (category `Netjes.Rendering.PageErrorMiddleware`). The [routing guide](routing.md) describes where each boundary file goes.
