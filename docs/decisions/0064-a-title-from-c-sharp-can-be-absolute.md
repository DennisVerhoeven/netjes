# 0064 – A title from C# can be absolute

Status: accepted

## Context

A title that a page's C# loader sends (`PageMetadata.Title`) goes through the nearest title template above it, as a string title in a page file does. A page file can skip the templates with `title: { absolute }`, but a loader had no way to say so. A page package that sends its whole title from C#, `Users · Back office`, got the app's root template around it (`Users · Back office · My app`), and the package could not know the template, or avoid it, from C#.

## Decision

- `PageMetadata.AbsoluteTitle` is a title that no template wraps. When both `Title` and `AbsoluteTitle` are set, `AbsoluteTitle` is the document title.
- On the wire it is the title's existing absolute form: the payload's `metadata.title` is `{ "absolute": "…" }` instead of a string. The client's metadata already merges that form, so the router, the server render and a client navigation need no change, and `Metadata`'s TypeScript type is unchanged.
- The page the browser renders itself, when no sidecar is ready, writes the same title into the document it sends (`AbsoluteTitle`, else `Title`).

## Alternatives considered

- **A `Title` that can be absolute** (a union type or a second constructor). A string stays the common case, and `Title = "x"` would silently be one or the other.
- **A `metadata` export that is a function of the layouts' props**, as Next.js's `generateMetadata` is. It would give the page a way to read the template, which a page does not need to know; it is a larger design that this change does not need.
- **Dropping the app's template for a page by a layout's opt-out.** It would move the choice from the page that knows its title to a layout above it.

## Consequences

- An app that sent a string title sees no change. A back office or page package can send complete titles from its loaders and no longer needs a `metadata` export in each page file whose `template: '%s'` ends the app's template.
- `og:title` and `twitter:title` fall back to the resolved document title, so an absolute title is what they carry when they set none.
