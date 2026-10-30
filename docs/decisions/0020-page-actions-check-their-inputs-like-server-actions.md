# 0020 – Page actions check their inputs like server actions

Status: accepted

## Context

`MapAction` checks the DataAnnotations and `IValidatableObject` of a server action's inputs before the action runs, and answers a failure with field errors: as JSON for a call from the client, or by rendering the form's page again for a post without JavaScript. A page action (`MapPageAction`) takes the same `[FromForm]` inputs. Without a check of its own, its delegate runs with whatever was posted, so an input's `[Required]` and `[StringLength]` say nothing, and with `AddValidation()` a failure is problem details JSON that no page can show. A page action's delegate also returns the page's props itself, so a check that runs before it has no props to put errors in.

## Decision

- `MapPageAction` adds the filters `MapAction` has for its inputs: the empty input of [ADR 0017](0017-a-form-input-without-fields-is-empty-not-null.md), then the DataAnnotations and `IValidatableObject` checks, after the antiforgery and origin checks. It reads the attributes at startup, and turns `AddValidation()` failures into the same result. A failure is the same `ServerAction.Invalid` a server action's is, carried out for a page action's endpoint.
- The failure renders the page at the post's own URL again in the same request, by its GET pipeline: the page's loader and its layouts' loaders run, the status is 422 and the response goes out `no-store`. It does so for every post, with and without JavaScript. The client router shows whatever page a page action answers with, so it gets that page's payload.
- The errors and the submitted values of the declared fields travel in the payload's action state, as for a server action's failure without JavaScript, named by the page's pattern where a server action's is named by the action. `usePageActionState()` returns them, in the shape of `useAction`'s failure state, and null on every other render.

## Alternatives considered

- **Keep page actions unchecked, and document it.** Every page action would repeat by hand the checks its input type already declares, and an input shared with a server action would be checked in one place and not the other.
- **Run the delegate anyway and hand it the errors.** It would need a parameter or a service for them, and every delegate would have to remember to look.
- **Answer a call from the client with the action's JSON failure, as a server action does.** The router shows a page action's answer as a page, and the page would need a second way to read the errors, one for each kind of post.

## Consequences

- A page reads a failed check with `usePageActionState()`, not from its props. An error only the delegate can find, such as a name that is taken, still goes in the props the delegate returns, so a page that has both reads both.
- The page's loader runs for a failed post, so the page shows its current data next to the errors, as on a reload.
- The delegate never runs for an input its DataAnnotations reject, so a check it repeats by hand never sees one.
