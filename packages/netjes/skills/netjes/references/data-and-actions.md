# Data and actions

Everything a page shows comes from C#: page loaders, layout loaders, shared props and JSON endpoints. Everything a page changes goes through C#: server actions. Their C# types become zod schemas and typed references in `generated/netjes.ts`.

## Page loaders

```csharp
app.MapPageLoader("/projects/[id]", async (string id, ClaimsPrincipal user, AppDb db, CancellationToken ct) =>
{
    var project = await db.FindProjectAsync(id, ct);
    if (project is null) return Page.NotFound();
    if (!project.HasMember(user)) return Page.Forbidden();
    return Page.Props(new ProjectProps(project.Name, project.Tasks))
        .WithQuery(["projects", id, "stats"], await db.StatsAsync(id, ct))
        .WithMetadata(new PageMetadata { Title = project.Name });
});
```

- It binds like a minimal API: route values by name, services, `ClaimsPrincipal`, `HttpContext`, `CancellationToken`, `[FromQuery]`.
- It reads no request body, so a complex type that is not a registered service needs `[AsParameters]` or a static `BindAsync`. A loader or action whose parameters minimal APIs cannot bind stops the app at startup, saying which and why. So does a loader parameter that reads the body (`[FromBody]`, `[FromForm]`, `IFormFile`): posted data belongs in a page action or a server action. A page loader stops the app too for a type that adds accepts metadata (`IAcceptsMetadata`) for itself, and must not get `.Accepts<T>()`: routing would match the page only for requests with that Content-Type, and navigations would get the not-found page. Repeated query values bind to an array (`int[] ids`); `List<int>` and `IEnumerable<int>` stop the app at startup, the latter because minimal APIs would bind it from services.
- `Page.Props(record)` names the props type for `pages['/projects/[id]']`. `Page.NotFound()`, `Page.Forbidden()`, `Page.Unauthorized()` render the nearest boundary file; `Page.Redirect(url)` and `Page.LocalRedirect(returnUrl)` (for URLs from the request) redirect, and the client router follows them. `.WithStatus(422)` changes the status.
- It returns a `RouteHandlerBuilder`: `.RequireAuthorization()`, `.CachePage(...)` and filters apply.
- A page's loader runs before its layouts' loaders, so a page under a layout that checks access checks it too.

## Layout loaders

`app.MapLayoutLoader("/projects/[id]", ...)` loads the data of `app/.../projects/[id]/layout.tsx` (by URL, or by folder: `app/(shop)`). The layout reads it with `defineLayout(layouts['app/(app)/projects/[id]'], function Layout({ project, children }) { ... })`. Its conventions (`.RequireAuthorization()`) apply to every page below. `NotFound`/`Forbidden` from it render the nearest boundary above the layout.

- It binds only the route values of its own URL (`id` here, never a deeper `taskId`; a `[FromRoute]` naming one stops the app at startup), the query string and services. Nothing binds from a body: a complex type needs to be a service, `[AsParameters]` or bind itself, and `[FromBody]`, `[FromForm]` or a file stops the app at startup.
- On client navigations within the section it is skipped while its route values, user and roles are unchanged, so checks that must run on every request belong in conventions or page loaders, and it must not depend on the query string or cookies.

## Shared props

`options.UseSharedProps(http => ValueTask.FromResult(new SiteProps(...)))` in `AddNetjes` gives every page and layout the same props: `useSharedProps(sharedProps)`. Typical: the signed-in user, from `http.User`. They are sent to the browser.

## Server actions

```csharp
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc; // [FromForm]: not in the implicit usings

public sealed record Renamed(string Name);

public sealed record RenameInput
{
    [Required] public string Id { get; init; } = "";
    [Required(ErrorMessage = "Give it a name."), StringLength(100)] public string Name { get; init; } = "";
}

app.MapAction("projects.rename", async ([FromForm] RenameInput input, AppDb db, CancellationToken ct) =>
    await db.TryRenameAsync(input.Id, input.Name, ct)
        ? ServerAction.Ok(new Renamed(input.Name)).RevalidatePath($"/projects/{input.Id}")
        : ServerAction.Invalid("name", "That name is taken."));
```

```tsx
const [state, formAction, pending] = useAction(serverActions['projects.rename']);
const nameError = state?.ok === false ? state.errors.name?.[0] : undefined;

<Form action={formAction}>
  <input type="hidden" name="id" value={id} />
  <input name="name" defaultValue={state?.ok === false ? String(state.values?.name ?? '') : name} aria-invalid={nameError ? true : undefined} />
  {nameError && <p>{nameError}</p>}
  <button disabled={pending}>Rename</button>
</Form>
```

- Results: `ServerAction.Ok(result)` or `Ok()`, `Invalid(field, message)` or `Invalid(errors)` (422), `Redirect(url)`, `Forbidden()`, `NotFound()`. Chain `.Revalidate(tags)` and `.RevalidatePath(paths)` (evict output-cached pages and refresh the page), `.Refresh()`, `.Invalidate(queryKey)` and `.WithQuery(queryKey, data)` (TanStack Query on the client).
- `[FromForm]` inputs make the form work without JavaScript: the browser posts, a success redirects back, a failure renders the page again with the errors and the typed values. Keep that working.
- An input without `[FromForm]` binds JSON: `await serverActions['posts.like']({ slug })` resolves to `{ ok: true, result }` or `{ ok: false, errors }`; server failures throw to the nearest error boundary.
- Validation: DataAnnotations (`[Required]`, `[StringLength]`, `[Range]`, `[EmailAddress]`) and `IValidatableObject` on an action's input are checked before the action runs (for a polymorphic JSON input, those of the `[JsonDerivedType]` the call sends). Failures come back as field errors (422, `state.errors`), also without JavaScript, and the action is not called. This needs no setup: `builder.Services.AddValidation()` is for your other minimal API endpoints, not for `MapAction` or `MapPageAction`. An attribute that cannot be read, such as a `[Display]` whose resource type has no such name, stops the app at startup, also on a derived type a JSON input lists with `[JsonDerivedType]`. A `[FromForm]` input is never `null` (unless declared nullable): a post with none of its fields, such as a form of unchecked checkboxes, gets an empty input, checked like any other. Give form inputs `init` properties, as above, not constructor parameters: a field the post leaves out (an unchecked checkbox, a list with nothing chosen) keeps its default, while form binding refuses such a post (400) for a positional record. A form input, and each type its properties and lists hold, needs exactly one public constructor: form binding never builds a type with several or none, or an abstract class or an interface, so that stops the app at startup. Bind a checkbox group or a list as a property of the input type (`public string[] Tags { get; init; } = [];`), not as a top-level `[FromForm]` parameter: a top-level list or dictionary reads fields named `[0]` or `[key]`, not its own name, and is `null` when none is posted.
- Optimistic UI: `useAction(action, { onSubmit(formData) { setOptimistic(...) } })` with React's `useOptimistic`.
- Every action needs `app.UseAntiforgery()` (after authentication) and checks the origin; `<Form>` and `serverActions` send the token. `MapAction` bodies are limited to 1 MB (`ActionBodySizeLimit`); an upload action sets `[RequestSizeLimit(bytes)]`. Page actions keep the server's limit.
- Name actions `feature.verb`. Check authorization inside the action, or with `.RequireAuthorization()`, as for any endpoint.
- `app.MapPageAction("/guestbook", ...)` is the other kind: a POST to the page's own URL from `<Form method="post">`, fields typed in `forms['/guestbook']`. Its input's DataAnnotations and `IValidatableObject` are checked as for `MapAction`: on a failure the delegate does not run, the page renders again from its loader (422), and `const failed = usePageActionState()` gives `failed.errors` and `failed.values` (null otherwise). Checks that need data stay in the delegate: return `Page.Props(...)` with your own errors and the typed values (`.WithStatus(422)`), or `Page.Redirect(...)` after a change.
- An action or post that sets or deletes a cookie (signing in or out) makes the client router forget the pages it keeps for back and forward, reload the page on screen and clear the query cache, in every tab of the app in that browser. Nothing to do in the action; a proxy must pass `X-Netjes-Cookies` through.

## JSON endpoints and TanStack Query

```csharp
app.MapGet("/api/projects/{slug}/stats", async Task<Results<Ok<ProjectStats>, NotFound>> (string slug, AppDb db) => ...)
    .RequireAuthorization();
```

```ts
async function fetchStats(slug: string) {
  const response = await fetch(`/api/projects/${encodeURIComponent(slug)}/stats`);
  if (!response.ok) throw new Error(`Stats request failed with ${response.status}`);
  return api['GET /api/projects/{slug}/stats'].response.parse(await response.json());
}
const stats = useQuery({ queryKey: ['projects', slug, 'stats'], queryFn: () => fetchStats(slug) });
```

Typed results (`TypedResults`, `Results<...>`) give the endpoint its `api` schemas, keyed by method and route template. Seed the same query key from the loader with `.WithQuery(key, data)` and the query renders on the server with no fetch; keep the key in one place on each side. Seeded data is fresh for the default `staleTime` (one minute).

## More

[Data loading](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/data-loading.md) and [Actions and forms](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/actions-and-forms.md) in the guides.
