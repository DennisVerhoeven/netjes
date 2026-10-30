# 0017 – A form input without any of its fields is empty, not null

Status: accepted

## Context

ASP.NET Core's form binding maps a `[FromForm]` class or record from the fields of a post. When the post carries none of the type's fields, it builds no object and hands the delegate `null`, even when the parameter is not nullable. A missing `[FromForm]` string and a missing JSON body get a 400 instead. A form posts none of an input's fields when all of them are checkboxes left unchecked, or when nothing but the form's hidden fields reaches the server: the antiforgery token and the page's URL to return to, which belong to no input. The delegate then threw a `NullReferenceException`, so the post got a 500, and the DataAnnotations on the input were never checked, because there was no object to check.

## Decision

- When form binding gives a `[FromForm]` class or record parameter `null`, `MapAction` and `MapPageAction` give the delegate an empty input instead: one made with the type's parameterless constructor, or with its only constructor (a positional record's) and every argument at its default. For a type with `init` properties, missing fields then mean the same whether the post left out some of them or all of them.
- The empty input is checked like any other, so its `[Required]` members come back as field errors (422). When it passes, the delegate gets it. Page actions check their inputs as server actions do ([ADR 0020](0020-page-actions-check-their-inputs-like-server-actions.md)).
- A parameter declared nullable (`NoteInput? input`) keeps its `null`. A JSON input is left alone: minimal APIs already refuse a call without a body, or with `null`, with a 400 when the parameter is not nullable.
- A post that form binding could not read, such as a value it cannot parse or a positional record without one of its fields, also leaves `null`. It keeps its 400: outside Development, form binding sets that status before the action's filters run, and the empty input is only made while the status is below 400.

## Alternatives considered

- **Answer 422 whenever the input is `null`, with one form-level error or with the `[Required]` errors.** A form of checkboxes could then never be posted with every box unchecked, and an input without `[Required]` members would get an error that no field shows.
- **Answer 400, as minimal APIs do for a missing string.** That refuses a valid post too, and shows the user an error page rather than the fields to fill in.
- **Leave `null` and tell apps to declare their inputs nullable.** Every action would need a check that the parameter's type says it does not need, and the tutorial's own action would keep failing with a 500.

## Consequences

- An action never sees `null` for a form input it did not declare nullable. `[Required]` on the input's members is how an action requires a field. ASP.NET Core MVC behaves the same way: it creates a top-level model that has no values and validates it.
- An empty positional record is built for a post without any of its fields, although form binding still refuses one that has some of its fields and lacks others (400). The [actions guide](../guides/actions-and-forms.md#validation) recommends `init` properties for form inputs, and a [route warning](../guides/routing.md#a-form-input-that-refuses-a-post-without-a-checkbox) names a positional form input whose constructor takes a checkbox or a list.
- A type that form binding cannot build at all, one with several public constructors or none, stops the app at startup: form binding would bind it `null` whatever was posted, and the empty input made in its place would drop what was posted.
