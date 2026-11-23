# 0068 – A loader sets the document's lang

Status: accepted

## Context

Netjes renders `<html>` itself ([ADR 0003](0003-react-renders-the-whole-document.md)), and its `lang` was `NetjesOptions.Lang`, or the request's locale under locale routing, written once when the server rendered the document. An app whose language is not in the URL had no way to set it: a back office in the language each user chose, a shop with a store per language, or a blog with one post in another language. Such an app set `document.documentElement.lang` in an effect after its layout mounted. The server's document still said `en`, so a screen reader that read the page before the effect ran used the wrong voice, and a page without JavaScript, or the error page of a layout that failed, kept it. Each such component had to put the old value back when it unmounted. A client navigation under locale routing kept the first page's locale as well.

## Decision

- `PageMetadata.Lang`, sent with `.WithMetadata(...)`, sets the document's language. A page loader's wins over its layout loaders', and the innermost layout loader's over those above it. A page none of them gives one has the root's: the request's locale, or `NetjesOptions.Lang`. It is the one part of a layout loader's metadata Netjes uses; the head's tags stay the page's.
- On the wire it is the payload's `lang` for the page loader and the segment's `lang` for a layout loader, not a field of `metadata`, which is the client's `Metadata` type and lists the head tags a page file can set. The document's own `lang` stays the root's, the value a page without one returns to.
- The server writes the resulting value on `<html>` in the first response: the sidecar's render (`documentLang` in the client package, read from the payload), streamed or not, and the document .NET writes when no sidecar is ready (`DocumentLang`, the same rule over the same payload, which the client-render fixture compares).
- After a client navigation the router computes the value for the page it shows, the page's locale coming before the root's, and sets the attribute in the commit that shows the page, only when the value differs from the previous page's. So a page that none of its loaders gives a lang has what a full load of its URL would have, and an app that sets the attribute itself keeps its value until the computed value changes, on a page whose loader gives a lang or in another locale; the page without one after that has the root's value.
- A segment the server marks reused, whose loader it skipped, carries no `lang`: the router keeps the one it holds for that segment, as it keeps its props.
- The value must have the shape of a BCP 47 language tag: a primary subtag of two to eight ASCII letters (or `x` or `i` and a subtag), then subtags of one to eight ASCII letters and digits after single hyphens. Another shape throws `ArgumentException` when `Lang` is set, and the client refuses a payload that carries one, so no value from a user or a database reaches the attribute as anything but a tag. The shape is checked, not the registry: `xx` passes.

## Alternatives considered

- **`lang` in a page file's `export const metadata`.** A page file knows neither the user nor the content's language, which the loaders know, and the document .NET writes when no sidecar is ready cannot read a page file's exports, so it would have the wrong `lang` until the page rendered.
- **A per-request option, such as a `Func<HttpContext, string>` on `NetjesOptions`.** It runs outside the loaders, so it cannot use what they loaded (a post's language), and it cannot differ between a page and the layout around it without repeating the routing.
- **Setting the attribute on every navigation.** It would overwrite a value an app sets itself on pages that give none, which apps did before this; changing it only when the computed value changes keeps theirs until it does.
- **Keeping the first page's lang on a page without one.** A Dutch post would leave the English index after it Dutch. Returning to the value a full load would have keeps the attribute a function of the URL and the user.

## Consequences

- An app that sets no `Lang` sees no change, except under locale routing, where `<html lang>` now follows the locale of the page a client navigation shows, as a full load of it did already.
- An app that set `document.documentElement.lang` in an effect can send `Lang` from the loader of the layout or page instead and drop the effect. Until it does, its value stays only while the router's own value does not change: a page whose loader gives a lang, or a page in another locale, replaces it, and the page without one after that has the root's value.
- A loader that passes a value from outside, such as a user's setting, gets an exception for a malformed one, and checks it against the languages it supports first.
