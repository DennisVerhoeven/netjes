# 0062 – `endpointUrl()` has a key for a POST endpoint that reads a form

Status: accepted; extends [ADR 0045](0045-typed-urls-for-the-apps-endpoints.md)

## Context

A page action answers a post with a page or a redirect. A page whose form downloads something, such as a person's personal data as a file, posts to a minimal API endpoint of its own instead, which binds a `[FromForm]` field, so that ASP.NET Core's form binding checks the antiforgery token, and answers `Results.File(...)` as an attachment, which `<Form>` saves. ADR 0045 gave `endpointUrl()` keys for JSON endpoints and for the `GET` of the others, and none for a `POST` that is not JSON, since a webhook is posted to by another site. The form's endpoint had no key, so its URL was written by hand from the route template, and kept in step with the C# route by hand.

## Decision

- A `POST` endpoint that is not JSON has a key when it accepts a posted form: its accepts metadata (`IAcceptsMetadata`) lists `application/x-www-form-urlencoded` or `multipart/form-data`, which minimal APIs add for a `[FromForm]` parameter, an `IFormFile`, an `IFormFileCollection` or an `IFormCollection`. The key is written as the others are, `POST /users/{id}/export`, in the app's file and, below the mount, in a page package's own file.
- The values are the endpoint's route values and the query values its handler binds, as for any key. The form's fields are not in the URL, so they are not part of the type.
- Only `POST`. `<Form>` posts with `get` or `post`, and a browser form does no more, so the other methods of such an endpoint get no key.
- Nothing else changes: an endpoint that reads no form (a webhook, a `POST` that takes a header or nothing) has no key, and a JSON endpoint has its keys as before.
- A form uses the URL as its action: `<Form method="post" action={endpointUrl('POST /users/{id}/export', { id })}>`. `<Form>` puts the base path in front and adds the antiforgery token, and a page package's `Form` puts the mount in front too.

## Alternatives considered

- **`Page.File(...)` for a page action**, so that the form posts to its own page. It is a larger design (the result type, the payload, a post that answers a file while the page stays) and waits for the framework's other server results.
- **Keys for every `POST` endpoint.** A webhook or a callback that another site posts to would be listed, and typed as if the app's code called it.
- **Keys for the other methods of a form endpoint.** No form sends them.
- **Typing the form's fields in the key.** The generated file already holds the fields of page actions (`forms`); an endpoint's fields would need a map and a schema of their own, for a URL builder that does not use them.

## Consequences

- After `netjes generate`, an app's file lists each of its form endpoints, and its comment above `EndpointParams` says so. An app that wrote the URL by hand can use `endpointUrl()` and lose the second copy of the template.
- Every form endpoint of the app, one mapped only in Development included, has its route in the generated file, as every key has ([ADR 0045](0045-typed-urls-for-the-apps-endpoints.md)). Authorization stays on the endpoint.
- An endpoint that accepts a form and also answers JSON has its keys already, for each method.
