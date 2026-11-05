# 0040 – Links from C# are a Href type

Status: accepted; extends [ADR 0006](0006-types-and-zod-from-csharp-contracts.md)

## Context

The generated file types the `href` of `<Link>` and `router.push` as `Href`: a root-relative URL that one of the app's pages serves, with or without a query or fragment, a `?…` or `#…` link, or an `http(s):`, `mailto:` or `tel:` URL. Its `Href` zod schema and `isHref()` check a string against those rules ([ADR 0006](0006-types-and-zod-from-csharp-contracts.md)). A loader that sent a link, such as the main navigation in the shared props, had only `string` to send it as, which the generator writes as `z.string()`, so each component that rendered the link parsed it again with `Href.parse` or cast it. The generator maps CLR types through their System.Text.Json contracts, and a contract says nothing about what a string holds. The `Href` schema was also written at the end of the file, after the schemas of the app's types, so a schema that used it would have read it before its declaration, and importing the file would have thrown.

## Decision

- `Netjes.Href` is a public sealed class that holds a link. The generator writes a property of that type as the file's `Href` schema, so `Href?`, lists, dictionaries and `Deferred<Href>` become `Href.nullable()`, `z.array(Href)`, `z.record(z.string(), Href)` and `deferred(Href)`, as any other type would. The file declares `routePatterns`, `isHref()` and the `Href` schema right after its imports, before the schemas that use them; the route types and `route()` stay at the end, since TypeScript lets a type be named before its declaration.
- C# checks the shape where an `Href` is made. The constructor throws `ArgumentException`, and `TryCreate` returns false, for a URL that starts with none of `/`, `?`, `#`, `http:`, `https:`, `mailto:` and `tel:` (the schemes in any case), that starts with `//` or `/\`, that holds an ASCII control character, or that starts or ends with a space. `isRouteHref`, which `isHref()` and the schema call, now refuses the same characters and spaces. Browsers drop a tab or a newline anywhere in a URL: `/\t/evil.example`, which it took for a path where a root catch-all page matched, goes to `evil.example`.
- Whether a root-relative URL is one of the app's pages is checked where the props are parsed, on the server and in the browser, by the `Href` schema.
- A converter on the type writes the URL as a plain string, and reading checks it as the constructor does.
- It converts to `string`, and nothing converts to it: there is no conversion from `string`, no `TryParse`, no `IParsable` and no `BindAsync`. The constructor's parameter, `url`, matches no property, so `[AsParameters]` refuses the type too. With a parameter that matched the `Value` property, that attribute would build an `Href` from a query value named `value`, and a page loader's startup error for a plain `Href` parameter, which minimal APIs would read from the body, suggests it.

## Alternatives considered

- **An `[Href]` attribute on `string` properties.** No new type, but a positional record needs `[property: Href]`, the items of a list or a dictionary need rules of their own, and C# checks nothing where the value is made.
- **Writing `System.Uri` as the `Href` schema.** A `Uri` holds any scheme, and the schema of every app's `Uri` props would change.
- **Documenting `.extend({ link: Href })` on the generated schema.** It works without a change, and the docs keep it for types the app cannot change, but every page that renders such a link repeats it, and the C# type still says `string`.
- **Checking in C# that a root-relative `Href` is a page.** The route table has no URL matcher, and one would be new public API; the schema makes the same check with the patterns the file already holds. The base path, which a link must not have, is the router's to add.
- **A check while the props are serialized, in Development.** It needs a converter in the app's JSON options, which every JSON endpoint of the app shares.
- **`IParsable<Href>` or a public `TryParse`.** Minimal APIs and form binding would then bind a query value or a form field to it. An `Href` from the request would look checked while it can be any https URL, such as a `returnUrl` for an open redirect, and a value that fails the check would answer a bare 400.

## Consequences

- A loader sends `new Href("/blog")`, and the component passes it to `<Link>` with no parse and no cast.
- A root-relative `Href` that no page serves, which C# cannot catch, fails the whole props parse: the page renders its nearest error boundary, with status 500 from the server. An `Href` is for links the code knows to be pages. A link to an endpoint or a file stays a `string`, and so does a link from a CMS, checked with `isHref` where it is rendered. `LinkGenerator`'s overloads that take an `HttpContext` add the base path, which an `Href` must not hold, so they need `pathBase: PathString.Empty`.
- No query value, route value or header binds to an `Href`, so a URL from the request stays a `string`, checked with `Page.IsLocalUrl`. Form binding still builds one from a field named after the constructor's parameter (`url`, or `next.url` for a property `next`), checked for its shape like any other, but never from the field of the property's own name, so a form that posts the link does not fill it: an action input binds an `Href` from JSON only, and the docs keep it out of form inputs.
- `Netjes` is a global using in apps with implicit usings, so an app's or a library's own type named `Href`, imported from another namespace, becomes ambiguous with this one in the files that import it (CS0104). Qualifying it or an alias settles it.
- Every app's generated file changes once, when it is regenerated: `routePatterns`, `isHref()` and `Href` move to the top.
- `isRouteHref`, `isHref()` and `Href.parse` refuse URLs with control characters and with spaces at either end, which they let through before.
