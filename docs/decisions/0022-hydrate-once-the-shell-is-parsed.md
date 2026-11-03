# 0022 – The client entry hydrates once the shell is parsed

Status: accepted

## Context

The client entry is an `async` module in the head, so that a streamed document hydrates its shell while slower Suspense boundaries are still arriving ([ADR 0012](0012-streamed-documents-and-deferred-values.md)). It waited only for the payload, the body's first element, before it called `hydrateRoot(document)`. A browser that has the entry and the page's modules in its cache runs it as soon as the payload is parsed, while the parser can still be in the middle of the shell. React then meets half a document, reports a hydration error (#418) and renders the page in the browser instead, and the parser adds the rest of the server's HTML after React's: the page shows twice. Under parallel load about one Production load in 40 to 80 hit it, and a slow connection with warm caches hit it every time.

## Decision

- `Document` ends the body with an empty `<template id="__NETJES_SHELL_END__">`. It sits outside every Suspense boundary, so React writes it last in the shell, before anything that streams later.
- The client entry still loads early from the head, reads the payload and loads the page's modules as before, but calls `hydrateRoot` only once that element is in the document, or the document has been parsed.
- A document the server wrote for the browser to render (the client-render fallback, [ADR 0013](0013-client-rendered-fallback.md)) has no shell to wait for, and is rendered at once, as before.

## Alternatives considered

- **React's `bootstrapModules`**, which writes the entry after the shell. It is React's own answer, but the entry would leave the head `Document` renders, while the client-render shell .NET writes keeps it there; the two heads are checked against each other and would differ for every page.
- **A plain, deferred module.** It runs once the whole document is parsed, which for a streamed document is when the last Suspense boundary has arrived, so the shell would no longer hydrate early.
- **The payload at the end of the body.** Waiting for it would mark the end of the shell too, but the client could only tell it was complete once something followed it: the first late chunk, or the end of the stream.

## Consequences

- Hydration starts when the shell has been parsed, as it would with React's own bootstrap, however fast the scripts arrive.
- Every server-rendered document ends its body with one empty `template` element. It renders nothing, but a stylesheet that targets the body's last child now sees it.
- The Blog example's end-to-end suite reloads a post with the scripts cached and the document throttled to 8 KB/s, and checks that the page shows once, without a hydration error.
