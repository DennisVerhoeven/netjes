# 0045 – Typed URLs for the app's endpoints

Status: accepted; extends [ADR 0006](0006-types-and-zod-from-csharp-contracts.md)

## Context

The generated file typed the URLs of pages (`route()` and `Href`) and the bodies of JSON endpoints (`api`), but not the URLs of endpoints. A component that fetched `GET /api/posts/{slug}/likes/` wrote `` `/api/posts/${encodeURIComponent(slug)}/likes` `` by hand: a renamed route or parameter still compiled, and the URL had no base path, which only `useHref` and `useBasePath` add, so under `UsePathBase` it asked the root. An endpoint that answers text or a file, such as `/llms.txt`, a feed, or a Markdown copy of each page next to a catch-all page, was in no map at all, since `api` lists only endpoints that declare JSON. `route()` builds page patterns in file syntax, while an endpoint's route is an ASP.NET Core template, with optional values, defaults, catch-alls that keep or encode slashes, and segments of several parts.

## Decision

- After `api`, the generator writes `EndpointParams`, the values each endpoint reads from its URL by key, `EndpointKey`, a module-local map of route templates, and `endpointUrl(key, params?, search?)`, which calls `buildEndpointUrl` from `netjes`. The keys are those of `api`, the method and the route as written. Every endpoint of the app that is not a page has keys, JSON or not: a JSON endpoint one for each of its methods except `HEAD`, any other endpoint one for `GET`, if it answers `GET`.
- An endpoint is the app's own when it names an HTTP method and is no page, page action, server action, fallback, static asset or MVC conventional route, and not one of Netjes's own. The image optimizer, the antiforgery token, the vitals beacon and the locale redirect carry an internal marker for that. `ExcludeFromDescription` is not read, so `MapSitemap` and `MapRobotsTxt`, which carry it, have keys.
- The values are read from what minimal APIs report: the parameters of the route, each typed from the handler parameter that binds it, then the query keys the handler binds (inferred, named by `[FromQuery]`, or members of an `[AsParameters]` type). They are TypeScript types, not zod schemas, since the browser writes these values and reads none of them. The types are what binding accepts, not what the JSON writes: an enum is its C# names and its values, which the query string binds, also when `JsonStringEnumConverter` writes other names.
- A route value is required unless routing has a default for it, or the template makes it optional and no parameter that needs a value binds it: binding answers 400 without one. `{slug}` stays required when the handler takes a `string? slug`, and `x` in `/a/{x?}/b` is required, since the matcher does not match `/a/b`. Where the types cannot say it (`y` without `x` in `/a/{x?}/{y?}`, which routing would read as `x`), `buildEndpointUrl` throws, as `LinkGenerator` gives no URL.
- The values object is closed and the third argument is open: a key the handler does not name, such as a tracking parameter or one that a `BindAsync` type reads itself, goes there.
- The URL is root-relative without the base path, as `route()` builds one. `<Link>` refuses the `string` it returns, since an endpoint is not a page; `useHref` puts it under the base path for a `fetch` or an `<a href>`.

## Alternatives considered

- **zod schemas for the values, in `api`.** They would check, in every chunk that builds a URL, values the browser itself writes; the types catch the same mistakes when the app is compiled.
- **A closed type without a third argument.** A key that a `BindAsync` type or `HttpRequest` reads would then be a type error, and so would a tracking parameter.
- **A map of its own for files and text, or an intersection with `SearchParamsInput` in each endpoint's type.** One set of keys, as `api` has, lets a JSON endpoint and a feed be called the same way, and an open third argument keeps the typed values checked.
- **Keys for every method of an endpoint that is not JSON.** Webhooks and uploads are posted to by other sites or by forms; a link or a fetch asks an endpoint for something with `GET`.
- **Honouring `ExcludeFromDescription`.** It hides an endpoint from OpenAPI documents, and it would drop the sitemap and `robots.txt`, which the app links to.
- **A public opt-out per endpoint.** The set is already the app's own endpoints, and an opt-out would be public API that no case needs yet; it can be added without changing the keys.
- **The text `LinkGenerator` writes, or ApiExplorer as the source.** The URL routes to the same endpoint, which is what a caller needs; matching `LinkGenerator`'s text would mean its rules for values equal to their defaults. ApiExplorer leaves out what `ExcludeFromDescription` hides and describes parameters for OpenAPI, not for a URL.
- **An OpenAPI client** ([ADR 0006](0006-types-and-zod-from-csharp-contracts.md)), or **typing `route()`'s query from the loaders**, which leaves endpoints as they were, or **documentation only**, which leaves each URL written by hand.

## Consequences

- After `netjes generate`, a renamed route, a parameter the handler no longer binds or a missing route value is a type error, and a fetch through `useHref` goes under the base path.
- Every app's generated file changes once, and imports `buildEndpointUrl`. A C# type named like one of the five new names gets a schema name qualified with its declaring type or its namespace.
- The file holds the route of each endpoint of the app, one mapped only in Development included, since the file is generated in Development, and a page that calls `endpointUrl` ships them in its chunk, as one that imports `api` ships its keys. Authorization stays on the endpoint.
- The generator infers binding again, from `IParameterBindingMetadata`, and can differ from minimal APIs where those report little: a custom `TryParse` type is a `string`, a nested `[AsParameters]` type may bind otherwise, and an MVC attribute route, which reports no binding, gets string route values and no typed query values. The open third argument covers what the types miss.
- An enum's values in a URL and in the JSON can differ, on purpose: the URL takes the names binding accepts.
