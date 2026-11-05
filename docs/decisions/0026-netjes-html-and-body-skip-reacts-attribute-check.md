# 0026 – Netjes's own `<html>` and `<body>` skip React's attribute check

Status: accepted; extends [ADR 0003](0003-react-renders-the-whole-document.md)

## Context

Netjes renders the whole document, `<html>` and `<body>` included ([ADR 0003](0003-react-renders-the-whole-document.md)), and an app's root layout renders inside the body. The app's markup therefore cannot give either element an attribute, but a script can: a theme script that sets a class or `data-theme` on `<html>` before the first paint, a browser extension that marks `<body>`, a page translator. In development, React compares the attributes of every element it hydrates with the props it renders. When an element has an attribute that its props do not, it logs "A tree hydrated but some attributes of the server rendered HTML didn't match …" and leaves the attribute alone. The message points at the page, not at a script. An end-to-end test that fails on any console error fails on it. An app cannot silence it either, because React's way to do so is a prop on the element, and the element is Netjes's. React's production build does not compare attributes at all.

## Decision

- `Document` renders `<html>` and `<body>` with `suppressHydrationWarning`, always, so the server and the browser render the same tree.
- The prop ends the attribute check for the element's own attributes and goes no deeper. The head and everything inside the body are compared as before, so a mismatch in the app's own markup is still reported.
- React does not write the prop into the markup. The document the server sends does not change, and neither does the head that .NET writes when no sidecar is ready ([ADR 0013](0013-client-rendered-fallback.md)), which tests compare with the head `Document` renders.
- The check is off for `lang` as well. The server and the browser read it from the same payload, so it differs only if a script changed it or Netjes has a bug.
- This stops React from reporting what a script wrote. It does not give an app a way to set attributes on these two elements.

## Alternatives considered

- **A root-layout export that sets attributes on `<html>` and `<body>`**, rendered by the server and the browser alike, for a class or `dir` that the server knows, such as one read from a cookie. It is a new API to type, merge and document, and it would not stop the message for an extension or a translator, which write after the server did. It can be added later without changing this decision.
- **Leave it.** Every app that sets a theme before the first paint, and every browser with an extension that marks the body, would see the message in development, and the app could not remove it.
- **Filter the message out of `console.error` in the client entry.** It depends on the wording of a React message, and it would hide the same message for the app's own mismatches.

## Consequences

- In development, an attribute that a script sets on `<html>` or `<body>` before hydration logs nothing. React does not patch the attribute up or remove it when it hydrates.
- A different `lang` is not reported either. If Netjes ever rendered one `lang` on the server and another in the browser, nothing would say so.
- A mismatch in the head, or anywhere inside the body, is reported as before.
- Only hydration is covered. When no sidecar is ready, the browser renders the document itself instead of hydrating it ([ADR 0013](0013-client-rendered-fallback.md)), and React's client render then removes the attributes of `<html>` and `<body>` before it sets its own, so a script's attributes are gone there. This decision does not change that.
