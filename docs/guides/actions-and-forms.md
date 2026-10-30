# Actions and forms

This guide is for app developers. It covers how a page changes data: server actions, which are C# endpoints that React calls through typed references, and page actions, which take a form post at the page's own URL. Both work without JavaScript. The guide also covers validation, what a failed post hands back, the checks every post goes through, revalidation, what signing in or out does to the client, and uploads and downloads. Server actions are C# endpoints rather than `'use server'` functions ([ADR 0011](../decisions/0011-server-actions-as-csharp-endpoints.md)).

## A server action

Map a server action with `MapAction`, a name and a minimal API delegate. It answers `POST /_netjes/actions/{name}`:

```csharp
// Features/Home/HomePage.cs
using Microsoft.AspNetCore.Mvc; // FromFormAttribute

public sealed record GreetInput
{
    public string? Name { get; init; }
}

public sealed record Greeting(string Text);

app.MapAction("home.greet", ([FromForm] GreetInput input) =>
    string.IsNullOrWhiteSpace(input.Name)
        ? ServerAction.Invalid("name", "Tell us your name.")
        : ServerAction.Ok(new Greeting($"Hello, {input.Name.Trim()}!")));
```

`[FromForm]` is in `Microsoft.AspNetCore.Mvc`, which a project's implicit usings do not include, so the file that uses it (or `[FromQuery]`, `[FromRoute]`, `[FromHeader]`, `[FromServices]`) needs `using Microsoft.AspNetCore.Mvc;`. `[AsParameters]` is in `Microsoft.AspNetCore.Http`, which implicit usings cover. The samples below leave it out.

```tsx
// components/HelloForm.tsx
import { Form, useAction } from 'netjes';
import { serverActions } from '../generated/netjes.ts';

export function HelloForm() {
  const [state, formAction, pending] = useAction(serverActions['home.greet']);
  const error = state?.ok === false ? state.errors['name']?.[0] : undefined;
  return (
    <Form action={formAction}>
      <input name="name" defaultValue={state?.ok === false ? String(state.values?.['name'] ?? '') : ''} aria-invalid={error ? true : undefined} />
      {error && <p>{error}</p>}
      <button type="submit" disabled={pending}>{pending ? 'Saying hello…' : 'Say hello'}</button>
      {state?.ok && <p role="status">{state.result.text}</p>}
    </Form>
  );
}
```

That is the form `netjes new` writes. With JavaScript it calls the action with `fetch` and shows the result in place. Without JavaScript the browser posts the form, and the page comes back with the error or the result ([Forms without JavaScript](#forms-without-javascript)).

- Names are letters, digits, `.`, `-` and `_`, and must be unique; `feature.verb` reads well. `MapAction` throws at startup otherwise.
- The delegate binds like any minimal API handler. A `[FromForm]` parameter binds form fields, which a `<Form>` posts and which also work without JavaScript. A parameter without it binds a JSON body, for calls from code. Services, `ClaimsPrincipal`, `HttpContext` and `CancellationToken` bind as usual. A delegate minimal APIs cannot bind, such as one with two inferred bodies or with `[FromForm]` fields next to a JSON body, stops the app at startup with an `InvalidOperationException` that names the action and gives minimal APIs' reason.
- `MapAction` returns a `RouteHandlerBuilder`: `.RequireAuthorization()`, `.RequireRateLimiting(...)` and filters work as on any endpoint. Check what the user may do inside the action as well, as for any endpoint that changes data.
- Under `npm run dev`, `dotnet watch` applies a change inside an action to the running app, but maps a new `MapAction` only after a restart (Ctrl+R): until then a post to it answers 405, and `generated/netjes.ts` gains its reference on that start or with `npx netjes generate`.

### Results

| Result | What the client gets |
|---|---|
| `ServerAction.Ok(result)` | Success with a result: `{ ok: true, result }`. Returns `ServerActionResult<T>`, which types the result in the generated reference. |
| `ServerAction.Ok()` | Success without a result. |
| `ServerAction.Invalid(field, message)`, `ServerAction.Invalid(errors)` | Status 422 with the errors per field: `{ ok: false, status: 422, errors }`. |
| `ServerAction.Redirect(url)` | Success, then the client navigates to `url` (relative or http(s), checked as `Page.Redirect` checks it). |
| `ServerAction.Forbidden()`, `ServerAction.NotFound()` | 403 or 404. A call from React throws `ServerActionError` to the nearest error boundary; a post without JavaScript gets the error page. |

Field names in `errors` are written with the app's JSON naming policy, so `Title` comes back as `title`, matching the field and the zod schema. Chain these on any result:

| Method | What it does |
|---|---|
| `.Revalidate(tags)` | Evict output-cached pages with these tags, then refresh the client's page. |
| `.RevalidatePath(paths)` | Evict the output-cached pages at these paths, then refresh the client's page. |
| `.Refresh()` | Refresh the client's page: every loader runs again. |
| `.Invalidate(queryKeys)` | Invalidate these TanStack Query keys, and those they prefix, so they are fetched again. |
| `.WithQuery(key, data)` | Set a TanStack Query entry on the client, without a fetch. |

```csharp
return ServerAction.Ok(new CreatedTask(number))
    .RevalidatePath($"/projects/{access.Slug}")
    .Invalidate(ProjectLayout.StatsKey(access.Slug));
```

## Calling an action from React

The generated `serverActions` map holds a typed reference to every `MapAction`. Its input is what the action reads: `FormData` for a `[FromForm]` action, the JSON input type otherwise (nullable and defaulted properties optional, computed ones left out), and `void` for an action that binds only services. Its result is the type given to `ServerAction.Ok(result)`, or `void` for an action that returns the untyped `ServerActionResult`.

```ts
export const serverActions = {
  "auth.logout": serverAction<void, void>("auth.logout"),
  "posts.like": serverAction<z.input<typeof LikeInput>, LikesResponse>("posts.like", { result: LikesResponse }),
  "tasks.create": serverAction<FormData, CreatedTask>("tasks.create", { result: CreatedTask }),
} as const;
```

Call a reference with an object, sent as JSON, or with `FormData`, sent as a form post. It resolves to an `ActionState`: `{ ok: true, result }`, or `{ ok: false, status, errors, values }` after a 422. Any other failure (403, 404, 413, 500, a refused token) throws a `ServerActionError` with the `status`. The Blog's like button calls its action from a click, with an optimistic count:

```tsx
const likePost = serverActions['posts.like'];

const [pending, startTransition] = useTransition();
const [count, addLike] = useOptimistic(likes.data?.likes, (current) => (current ?? 0) + 1);

<button type="button" onClick={() => startTransition(async () => {
  addLike(undefined);
  await likePost({ slug });
})}>
  {count} likes
</button>
```

`serverAction<TInput, TResult>(name, { result })` from `netjes` writes such a reference by hand, with a schema to parse the result. An action that answers `ServerAction.Redirect(...)` resolves with no result even when its type promises one, and by then the router has moved on.

### Calls as the page starts

In the browser a call goes through the client router, which adds the antiforgery token and the URL to return to, and acts on the answer. The router registers itself in an effect, which React runs after the page's own effects, and a call can come sooner: a form submitted while the page hydrates, or a call from a component's `useEffect` or `useLayoutEffect` as it mounts. Such a call waits for the router, for up to 10 seconds, and then goes out as usual. Links, `router.push`, `router.refresh()` and page action forms reach the router directly and need no wait.

A call with no router on its way fails at once with an error saying that the action "was called before the page was ready": on the server, where no router registers, and in a component test without `createTestApp`'s `Wrapper` ([Testing](testing.md#answering-server-actions)). If the router does not register within the 10 seconds, because React threw its render away, the call fails with the same error.

### `useAction` and `<Form>`

[`useAction(action, options)`](../reference/react-api.md#useaction) wraps React's `useActionState`: it returns `[state, formAction, isPending]`. Pass `formAction` to `<Form action>`:

- React runs the action, so `useFormStatus()` works in the form's buttons.
- On the server the form renders as a real post to the action, with the antiforgery token and the page's URL to return to, so the same form works without JavaScript.
- After a failed post without JavaScript, `state` starts as that failure.
- `options.initialState` sets the state before the first submission (default `null`).
- `options.onSubmit(formData)` runs inside the action's transition, before the call: the place for `useOptimistic` updates, which React keeps until the action and the refresh after it have finished.

```tsx
const [shown, addPending] = useOptimistic<ShownProject[], ShownProject>(projects, (current, next) => [...current, next]);
const [state, formAction] = useAction(serverActions['projects.create'], {
  onSubmit: (data) => addPending({ slug: String(data.get('slug') ?? ''), name: String(data.get('name') ?? ''), pending: true }),
});
```

React resets a form after its action runs. To keep what the user typed after a failure, give each field `defaultValue={state.values.name}`: a failed state carries the submitted values of the fields the action declares, with or without JavaScript ([What a failed post hands back](#what-a-failed-post-hands-back)).

## Validation

`MapAction` and `MapPageAction` check the DataAnnotations of their inputs before the action runs. A server action answers with their errors as `ServerAction.Invalid`, also without JavaScript; a page action renders its page again with them ([Validation in page actions](#validation-in-page-actions)). No setup is needed:

```csharp
public sealed record CreateTaskInput
{
    [Required] public string ProjectSlug { get; init; } = "";

    [Required(ErrorMessage = "Give the task a title."), StringLength(200)]
    public string Title { get; init; } = "";

    public string? Priority { get; init; }
}

app.MapAction("tasks.create", async ([FromForm] CreateTaskInput input, ...) => ...);
```

- Attributes count on properties and on a record's constructor parameters (`record SignUpInput([Required, EmailAddress] string Email, ...)`).
- An input that implements `IValidatableObject` is validated once its attributes pass. Its errors without a member name are keyed `""`.
- A JSON input whose type lists derived types with `[JsonDerivedType]` is checked by the attributes of the type the call sends, also when the declared type is abstract or has none.
- When there are errors, the action does not run.
- A `[FromForm]` input is never `null`. For a post that carries none of its fields, such as a form whose only fields are unchecked checkboxes, form binding builds no object, so the action gets an empty input, as if each field were missing. It is checked like any other: its `[Required]` members come back as field errors, and an input that passes reaches the action. Declare the parameter nullable (`[FromForm] NoteInput? input`) to get `null` instead ([ADR 0017](../decisions/0017-a-form-input-without-fields-is-empty-not-null.md)).
- `builder.Services.AddValidation()` is not needed for this. It is for the app's other minimal API endpoints, which keep their 400 problem details.
- Rules that need data, such as a name that is taken, go in the action: `return ServerAction.Invalid("email", "There is already an account with this email.")`.
- `MapAction` and `MapPageAction` read the attributes as they map the action. One that cannot be read, such as a `[Display]` whose resource type has no property of that name, or a validation attribute whose constructor throws, stops the app at startup, naming the action, the parameter and the property. So does one on a derived type that a JSON input lists with `[JsonDerivedType]`.

Give a form input `init` properties, as above, rather than constructor parameters. ASP.NET Core's form binding builds a positional record only from a post that has every one of its constructor's fields, and refuses any other post with 400 before the action runs: the user gets the error page, or a `ServerActionError` from the call with JavaScript. A browser leaves an unchecked checkbox out of the post, and a list with nothing checked or chosen, so a positional record with a `bool` or a list refuses a valid post. With `init` properties a missing field keeps its default (`false` for a checkbox), and `[Required]` names the fields that must be there. A positional record is fine for text fields only, which a browser posts even when they are empty; bind a checkbox next to it as a parameter of its own: `([FromForm] SignInInput input, [FromForm] string? returnUrl, [FromForm] bool? stay, ...)`. Netjes warns at startup about a positional form input whose constructor takes a `bool` or a list ([Route warnings](routing.md#a-form-input-that-refuses-a-post-without-a-checkbox)).

Form binding builds a form input only through its one public constructor. From a type with several public constructors or none, or from an abstract class or an interface, it builds nothing, whatever the post holds, so the action would get an empty input or `null`. `MapAction` and `MapPageAction` stop the app at startup for such a type, naming the action and the type: give it one public constructor, a parameterless one with `init` properties or a positional record's, or use a concrete type in its place. The same goes for the types of the properties form binding fills (those with a public getter and setter, not marked `[IgnoreDataMember]`) and the items of its lists. A type it reads from one field, one with a `TryParse` method or one that implements `IParsable<T>`, such as a strongly-typed id, may have any constructors.

A checkbox group, several fields with one name, binds to a `string[]` property of the input type, for repeated fields (`Tags=a&Tags=b`) and indexed ones (`Tags[0]=a`) alike, and stays empty when nothing is checked:

```csharp
public sealed record TagInput { public string[] Tags { get; init; } = []; }

app.MapAction("posts.tag", ([FromForm] TagInput input) => ...);
```

A top-level `[FromForm] string[] tags` parameter does not work: ASP.NET Core's minimal APIs refuse it ("TryParse method found on string with incorrect format"), so Netjes stops the app at startup, naming the action and that reason. Bind the list as a property of an input type, as above.

Other top-level lists bind in ways a form rarely fits:

- An array of numbers, dates or other values with a `TryParse` method (`[FromForm] int[] ids`) binds from repeated fields of its name (`ids=1&ids=2`), and is empty when none is posted.
- A list or dictionary of any other kind (`[FromForm] List<string> tags`, `[FromForm] List<LineInput> lines`, `[FromForm] Dictionary<string, string> notes`) binds from fields named by index or key alone, without the parameter's name (`[0]=a`, `[0].product=…`, `[gift]=…`), whatever `[FromForm(Name = ...)]` says. A checkbox group named `tags` never reaches it, and it is `null` when no such field is posted. Netjes leaves that `null`, unlike an input object without any of its fields ([ADR 0017](../decisions/0017-a-form-input-without-fields-is-empty-not-null.md)): an empty list would also stand for checked boxes posted under the parameter's name, which form binding drops.

Both work as properties of an input type, where fields carry the property's name and a list keeps its default (`= []`) when nothing is posted.

## Forms without JavaScript

Every `<Form action={formAction}>` is also a plain HTML form. Without JavaScript, or before the page's JavaScript has run, the browser posts it to `/_netjes/actions/{name}` with the antiforgery token and a hidden `__netjes_return` field holding the page's URL:

- **Success:** a 303 back to the page, or to the action's `ServerAction.Redirect` target.
- **`Invalid`:** the page is rendered again in the same request, status 422, with the errors and the declared fields' values, and `useAction` starts with that state. The response goes out `Cache-Control: no-store`, since it holds what was typed.
- **`Forbidden`, `NotFound` and errors:** the error page with that status.

The page rendered for a failure runs the pipeline again from `UseNetjes`'s routing, as a GET of the return URL. Middleware registered before `app.UseNetjes()` does not run for it, so guard pages with endpoint authorization, not with a middleware in front. The return URL must be a page of the app: one that routes anywhere else, such as a static file or an API, renders the home page instead, or the error page with 422 where `/` is no page either.

After an `Invalid` result, the address bar of a browser without JavaScript shows the action's URL (`/_netjes/actions/home.greet`) while the page is the one the form is on. Reloading asks to post the form again, and opening that URL with a GET is a 404, since an action answers only `POST`. This is how a plain form post behaves, and it only matters when users reload or share the address after a failure. If it does matter, use a page action: it posts to the page's own URL, so the address bar keeps showing the page ([page actions](#page-actions)).

Test forms with JavaScript turned off. Playwright's `test.use({ javaScriptEnabled: false })` does that ([Testing](testing.md)).

## What a failed post hands back

A failure hands back what was typed only for the fields the action declares:

- its `[FromForm]` parameters, by name;
- the fields form binding reads into a `[FromForm]` type, by their C# or JSON names, nested and indexed ones included (`address.street`, `tags[0]`, `lines[0].product`, `notes[gift]`);
- the `[FromForm]` members of an `[AsParameters]` type.

It never hands back files, the antiforgery token, Netjes's own `__netjes_` fields, or a field whose name marks it as private. An action that reads the form itself (`IFormCollection`, `HttpRequest`) declares nothing, so it hands back nothing.

Without JavaScript the values are written into the page. A value over 16 KB comes back empty, and the values stop at 64 KB in all, field names included. With JavaScript the browser keeps the submitted values itself, and the server names the fields it may hand back, up to 64 KB of names.

`NetjesOptions.PrivateFormFields` lists the words that mark a field as private. The defaults are `password`, `passwd`, `pwd`, `pass`, `pw`, `passcode`, `passphrase`, `secret`, `token`, `apikey`, `privatekey`, `credential`, `pin`, `otp`, `totp`, `mfa`, `recovery`, `cvc`, `cvv`, `csc`, `cc`, `card`, `iban`, `ssn` and `bsn`. Names are compared ignoring case:

- A word of five letters or more matches anywhere in the name, with `_`, `-` and `.` ignored: `newPassword`, `password2`, `api_key`, `recoveryEmail`.
- So does a word written with those separators: `auth-code` also hides `authCode`.
- A shorter word matches as a word of the name, or at the start or end of one: `PINCode`, `card_number`, `cardnumber`, `newpwd`, and also `passengerName` and `compass`, but not `spinner`. `cc` and `csc` cover the browser's autocomplete names (`cc-number`, `cc-csc`).

Hiding a field by mistake only costs a retype. Set your own list in code to replace the defaults: `options.PrivateFormFields = ["password", "secret", "card"]`. Configuration (`Netjes:PrivateFormFields`) adds its words to the defaults instead ([Options](../reference/options.md#actions-and-forms)).

## Page actions

A page action takes a form post at the page's own URL, for a page whose form should post where the page is. Map it with `MapPageAction` and the page's pattern, next to the page's loader:

```csharp
public sealed record SignForm
{
    public string? Name { get; init; }
    public string? Message { get; init; }
}

public sealed record GuestbookProps(
    IReadOnlyList<GuestbookEntry> Entries,
    IReadOnlyDictionary<string, string>? Errors = null,
    SignForm? Values = null);

app.MapPageLoader("/guestbook", (GuestbookStore store) => Page.Props(new GuestbookProps(store.Recent())));

app.MapPageAction("/guestbook", ([FromForm] SignForm form, GuestbookStore store) =>
{
    var errors = Validate(form);
    if (errors.Count > 0)
    {
        return Page.Props(new GuestbookProps(store.Recent(), errors, form)).WithStatus(StatusCodes.Status422UnprocessableEntity);
    }

    store.Sign(form.Name!.Trim(), form.Message!.Trim());
    return Page.Redirect("/guestbook");
});
```

```tsx
// app/guestbook/page.tsx
export default definePage(pages['/guestbook'], function Guestbook({ entries, errors, values }) {
  const submitting = useNavigation().state === 'submitting';
  return (
    <Form method="post">
      <input name="name" defaultValue={values?.name ?? ''} aria-invalid={errors?.name ? true : undefined} />
      {errors?.name && <p>{errors.name}</p>}
      <textarea name="message" defaultValue={values?.message ?? ''} />
      <button type="submit" disabled={submitting}>Sign the guestbook</button>
    </Form>
  );
});
```

- A `<Form method="post">` without an `action` posts to the current URL. With JavaScript the client router posts it with `fetch` and treats the answer like a navigation: it follows a redirect, or shows the page the action rendered, in place of the current history entry when the URL stays the same.
- Return `Page.Redirect(...)` after a change (a 303 after a post), or `Page.Props(...)` to render the page again. The action supplies all the page's props itself: the page's loader does not run for it, though its layouts' loaders do.
- The DataAnnotations of its inputs are checked before the delegate runs, as for a server action; a failure renders the page again with the errors ([Validation in page actions](#validation-in-page-actions)). Checks the delegate makes itself, such as one that needs data, return the page's props with the errors and values, as above.
- A `[FromForm]` input is never `null` unless the parameter is declared nullable: a post with none of its fields gets an empty input, checked like any other, as for a server action.
- The delegate binds like a minimal API POST handler: route values, `[FromForm]` fields, services. One minimal APIs cannot bind, such as one with two inferred bodies, form fields next to a JSON body, or a `[FromRoute]` naming no route value, stops the app at startup, naming the page file and its URL. So does a form input that form binding could never build ([Validation](#validation)).
- The `[FromForm]` type is in the generated `forms` map: `forms['/guestbook']`.
- A page with a page action cannot be exported to static files.

### Validation in page actions

A page action's inputs are checked like a server action's, with or without `AddValidation()`: their DataAnnotations, then `IValidatableObject`. When a check fails, the delegate does not run. The page is rendered again in the same request from its loader, with status 422 and `Cache-Control: no-store`, and `usePageActionState()` returns the errors and the submitted values of the fields the action declares ([What a failed post hands back](#what-a-failed-post-hands-back)). With JavaScript the client router shows that page in place; without it the browser shows it, and the address bar keeps the page's URL.

```csharp
public sealed record SignForm
{
    [Required(ErrorMessage = "Tell us who you are."), StringLength(40)]
    public string? Name { get; init; }

    [Required(ErrorMessage = "Leave a message."), StringLength(280)]
    public string? Message { get; init; }
}

app.MapPageAction("/guestbook", ([FromForm] SignForm form, GuestbookStore store) =>
{
    store.Sign(form.Name!.Trim(), form.Message!.Trim());
    return Page.Redirect("/guestbook");
});
```

```tsx
// app/guestbook/page.tsx
export default definePage(pages['/guestbook'], function Guestbook({ entries }) {
  const failed = usePageActionState();
  return (
    <Form method="post">
      <input name="name" defaultValue={String(failed?.values.name ?? '')} aria-invalid={failed?.errors.name ? true : undefined} />
      {failed?.errors.name && <p>{failed.errors.name[0]}</p>}
      <textarea name="message" defaultValue={String(failed?.values.message ?? '')} />
      <button type="submit">Sign the guestbook</button>
    </Form>
  );
});
```

`failed` has the shape of `useAction`'s failure state, `{ ok: false, status: 422, errors, values }`: `errors` is keyed with the app's JSON naming policy, as for a server action, and `values` by the names the form posted. It is null on every other render, also after a server action's failure, which `useAction` reads. An error that only the delegate can find, such as a name that is taken, goes in the page's props, as in the first example.

## Antiforgery and the origin check

Every server action and page action requires a valid antiforgery token and a same-origin request. `AddNetjes()` registers antiforgery; put `app.UseAntiforgery()` in the pipeline after `UseNetjes()` and after authentication:

```csharp
app.UseNetjes();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
```

- **The token.** Each page's payload carries a token, and `<Form>` and the action references send it, in a hidden field or a header. A page action requires it even when it binds no form fields, such as a delete button. A call refused for its token gets 400 with `X-Netjes-Antiforgery: invalid`, and the client sends that call again once, with a fresh token; no other answer is retried, since the action may have run. Output-cached pages carry no token: the client fetches one from `/_netjes/antiforgery` before the first post, so forms on cached pages need JavaScript ([Caching](caching.md)).
- **The origin.** A post whose `Sec-Fetch-Site` is `cross-site`, or whose `Origin` is not the request's host, gets 403. `NetjesOptions.AllowedActionOrigins` adds origins, such as a public host a proxy rewrites; it helps only for same-site ones, since a cross-site post is always refused. Behind a proxy that rewrites `Host`, call `UseForwardedHeaders()` so the app sees its public host.
- **`.DisableAntiforgery()`** on a page action turns off both checks, for a payment or identity provider that posts a form back from its own site. On a server action it turns off the token check only; the origin check stays.

[Security](security.md) covers these checks in full, with the antiforgery cookie's settings.

## Body limits

A server action accepts a request body of up to 1 MB (`NetjesOptions.ActionBodySizeLimit`, like Next.js's `serverActions.bodySizeLimit`). A larger one gets 413, apart from a form post without JavaScript sent chunked, without a `Content-Length`: that one gets 400, because the antiforgery check fails to read its token from the body. Calls from the client send the token in a header, so they get 413 either way. The option only lowers the server's limit, never raises it, and `null` leaves the server's limit. Give one action its own limit, higher or lower, with `[RequestSizeLimit(bytes)]` or `[DisableRequestSizeLimit]` on its delegate or group, or `.WithMetadata(new RequestSizeLimitAttribute(bytes))`; like any such limit, it replaces the server's.

```csharp
app.MapAction("photos.upload", [RequestSizeLimit(20 * 1024 * 1024)] (IFormFile photo, PhotoStore photos) => ...);
```

Page actions keep the server's limit (30 MB with Kestrel).

## Revalidation

After an action, the client refreshes what changed:

- `.RevalidatePath("/projects/web")` and `.Revalidate("posts")` evict output-cached pages by path or by tag, then refresh the page on screen, so it shows the change. `IPageCache` does the same from anywhere else, such as a webhook ([Caching](caching.md)).
- `.Refresh()` refreshes the page on screen without evicting anything.
- `.Invalidate(["projects", slug, "stats"])` makes TanStack Query fetch those queries again; `.WithQuery(key, data)` sets one without a fetch.
- `ServerAction.Redirect(url)` navigates instead.

A refresh runs every loader of the page again, layouts included, in the same history entry, without scrolling. If the user moved to another page while the action ran, the router neither redirects nor refreshes. Every action and form post also drops the pages the router prefetched.

## Signing in and out

An action that signs someone in or out changes what every page shows. Script cannot read `Set-Cookie`, so when a call from the client router sets or deletes a cookie, the response carries `X-Netjes-Cookies: changed`, and the router acts on it as soon as it arrives ([ADR 0014](../decisions/0014-signing-in-or-out-clears-what-back-shows.md)):

- It forgets the pages it keeps for back and forward, and its prefetched pages, which showed whoever was signed in before. Back and forward load those pages from the server again.
- Navigations and prefetches on their way went out with the old cookies, so they start over.
- It loads the page on screen from the server once, as Next.js does: by the action's redirect or revalidation, or else by a refresh.
- The built-in `QueryClient` is cleared, seeded queries included; the action's own `.WithQuery` entries are applied after that. A memoized component whose props did not change keeps its last result until it renders or refetches.
- The app's other tabs in the same browser send the same cookies, so the tab that made the call tells them ([ADR 0021](../decisions/0021-signing-in-or-out-reaches-the-other-tabs.md)). Each forgets its pages, clears its `QueryClient` and loads the page on screen from the server again, so every tab shows the user the change signed in, or none.

A sign-out action needs nothing more than signing out:

```csharp
app.MapAction("auth.logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return ServerAction.Redirect("/");
});
```

The header follows these rules:

- Cookies the action writes count, its own `OnStarting` callbacks included. The antiforgery cookie does not, and neither do cookies a middleware wrote before the action ran or as the response starts, such as cookie authentication's sliding renewal.
- Server actions and page actions are both marked, when the client router made the call. A post without JavaScript is not marked; the browser loads the next page in full anyway.
- A sign-in that changes no cookie, such as one kept in a server-side session, is not seen. Set or delete a cookie in the action to mark it.
- Only the tab that made the call sees the header, and it tells the app's other tabs (those under the same base path) on a `BroadcastChannel`, which every current browser has. In a browser without one, another tab keeps its router's memory and its query cache until it makes such a call itself.
- A proxy in front of the app must pass `X-Netjes-Cookies` and `X-Netjes-Antiforgery` through.

A form post is never sent twice: one still on its way when another call's header arrives shows what it answered, and the router keeps that page for no history entry, so it comes from the server when the user returns to it. Pages also go out with `Cache-Control: no-store, private` by default, so the browser's own cache does not show the previous user's pages on Back after a full page load ([Caching](caching.md)).

## `<Form>` in detail

| Prop | What it does |
|---|---|
| `method` | `'get'` (default) or `'post'`. A GET form navigates to its action with the fields as the query string. |
| `action` | Where to submit: a URL (default: the current one, where page actions listen), or a form action function such as `useAction`'s `formAction`. |
| `replace` | For a GET form: replace the history entry instead of adding one. |
| `reloadDocument` | Let the browser submit the form itself, with a full page load. |

The other props are those of a `<form>`. Without JavaScript every form is a native form; with it, `<Form>` submits with `fetch` and handles the answer:

- A page, or a redirect to one, is shown as a navigation.
- An HTML document that is not a Netjes page is shown as the browser would show it, and is never requested again as a GET.
- A response with `Content-Disposition: attachment` is saved as a download.
- Anything else, or no answer, leaves the page and what was typed as they are, and `useNavigation()` reports the failure with its status ([Navigation state](routing.md#navigation-state)).

A submit button's `formaction`, `formmethod` and `formtarget` are honoured. A form or button whose target is not `_self`, and an action on another origin, are left to the browser. Under a base path, use `<SubmitButton formAction="/x">` instead of a plain button with `formAction`, so the base path is added with and without JavaScript.

POST forms carry the antiforgery token when the page has one. A form that posts to your own minimal API endpoint validates that token through ASP.NET Core's form binding, as any endpoint that binds a form does.

## Uploads and downloads

Bind uploaded files as `IFormFile` (or `IFormFileCollection`) in a page action or a server action, and give an action that takes large files its own size limit ([Body limits](#body-limits)). Files are never handed back after a failure.

For an upload that must also work without JavaScript, post the form to a page action with `encType="multipart/form-data"`. With JavaScript, `<Form>` sends it as multipart too:

```tsx
<Form method="post" encType="multipart/form-data">
  <input type="file" name="photo" accept="image/*" />
  <button type="submit">Upload</button>
</Form>
```

```csharp
app.MapPageAction("/photos", async (IFormFile photo, PhotoStore photos, CancellationToken cancellationToken) =>
{
    await photos.SaveAsync(photo, cancellationToken);
    return Page.Redirect("/photos");
});
```

A server action called with `FormData` that holds a file sends it as multipart as well, so `useAction` uploads with JavaScript. Without JavaScript the browser sends files only from a form with `encType="multipart/form-data"`, and React warns about an `encType` on a form whose action is a function, which is why the page action is the way for uploads that must work either way.

A form post that answers with a file (`Content-Disposition: attachment`) is downloaded, but the client router reads the whole file into memory first. For a large file, give the form `reloadDocument`, so the browser streams it to disk, or link to a GET endpoint with `<Link reloadDocument>` or a plain `<a download>`.
