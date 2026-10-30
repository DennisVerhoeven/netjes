# 0011 – Server actions are C# endpoints with typed references

Status: accepted

## Context

Next.js server actions (`'use server'`) are functions the client calls by an opaque ID made per build, with forms that post without JavaScript, `useActionState` and `useFormStatus`, and cache revalidation. In Netjes the code they would run is C#.

## Decision

- `MapAction(name, delegate)` maps `POST /_netjes/actions/{name}` under a stable name. It binds JSON or `[FromForm]` like any minimal API and returns `ServerAction.Ok`, `Invalid`, `Redirect`, `Forbidden` or `NotFound`, with `.Revalidate(tags)` and `.RevalidatePath(paths)` to evict output-cached pages, `.Refresh()` to reload the page, and `.Invalidate(queryKey)` and `.WithQuery(key, data)` for TanStack Query.
- Netjes validates an action's inputs before it runs: the DataAnnotations on their properties and record constructor parameters, then `IValidatableObject`. A failure answers `ServerAction.Invalid(errors)` without calling the action, and needs no `AddValidation()`.
- Every action requires an antiforgery token and a same-origin request (`Origin`, or `Sec-Fetch-Site`), as Next.js checks the origin of action calls. Pages the output cache stores carry no token, so the client fetches one from `GET /_netjes/antiforgery` before it posts. A call the server refused for its token (a 400 with `X-Netjes-Antiforgery: invalid`) did not run, so the client sends it once more with a fresh token.
- An action's request body is limited to `ActionBodySizeLimit` (1 MB by default) unless the action sets its own limit. The setting only ever lowers the server's limit.
- On the client, `generated/netjes.ts` has a typed reference for each action (`serverActions`), from its C# signature. `serverAction(name)` makes a callable reference by name, and `useAction` wraps `useActionState`. `<Form action={formAction}>` renders a real `action` and `method` on the server, with the token and a return URL, and a function action in the browser; React keeps the server's attributes when it hydrates.
- Without JavaScript, success redirects to the return URL, and a validation failure runs the return page's GET pipeline again in the same request, with the errors and the submitted values, which `useAction` starts from. Only fields the action declares come back, and never one whose name has a word from `PrivateFormFields` in it, such as `password`.
- A form can also post to its own page: `MapPageAction(pattern, delegate)` maps a POST on the page's URL that answers with the page, `Page.Props` with whatever errors the handler puts there, or a redirect. It has the same token and origin checks; `.DisableAntiforgery()` turns both off for posts from other sites. Its inputs are checked as an action's are ([ADR 0020](0020-page-actions-check-their-inputs-like-server-actions.md)).

## Alternatives considered

- **Opaque action IDs, as in Next.js.** They break after every deploy ("Failed to find Server Action") and cannot be generated from C#.
- **Only `MapPageAction`.** It ties every action to a page URL, while actions such as "like" are called from many pages and from code.
- **Leave validation to ASP.NET Core's `AddValidation()`.** Its source generator does not see the inputs of delegates passed to `MapAction`, so actions would run on invalid input unless each app checked by hand.

## Consequences

- Actions are ordinary endpoints: authorization, rate limiting and filters work as usual, and they are left out of OpenAPI documents.
- After a revalidating action the client loads its page again in a second request.
- A page action's input is checked as a server action's is, before the handler runs, and a failure renders the page again with status 422 ([ADR 0020](0020-page-actions-check-their-inputs-like-server-actions.md)). An error only the handler can find goes in the `Page.Props` it returns.
- An action that sets or deletes a cookie changes what the client keeps for back and forward ([ADR 0014](0014-signing-in-or-out-clears-what-back-shows.md)).
