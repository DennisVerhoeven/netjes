# 0012 – Streamed documents, deferred values, and ready boundaries in place

Status: accepted

## Context

`loading.tsx` and Suspense are only useful if the shell reaches the browser before the slow parts. Netjes loaders run in C# before rendering, so what is slow is either lazy-loaded code or data a loader chose not to wait for.

React's streaming renderer also decides where a finished Suspense boundary goes. It writes a ready boundary in place, inside the shell, until what it has written so far and the boundary pass `progressiveChunkSize` (12.8 kB by default). A boundary past that point goes after the shell, in a hidden element, and an inline script swaps it in for the fallback, so that a browser can paint the shell before a large boundary's bytes arrive. Netjes renders the whole document, payload included ([ADR 0003](0003-react-renders-the-whole-document.md)), and in development the page's CSS sits in the head, so that limit comes early. With React's default, a large page under a `loading.tsx` would show only its skeleton to a browser without JavaScript, to a crawler, under `StreamDocuments = false` (which waits for every boundary but does not change where React puts it), and under a hash-based Content-Security-Policy, which blocks the reveal script.

## Decision

- The sidecar answers `/render` once the shell is ready (`onShellReady`) and streams the rest; .NET relays it with a flush after each chunk. A shell that fails becomes the error page, since nothing has been sent yet. Crawlers (`CrawlerUserAgents`), `StreamDocuments = false` and `netjes export` get the document once everything is ready (`onAllReady`).
- `Page.Defer(task)` puts a placeholder (`{ "$netjes": "deferred", "id": … }`) in the props instead of the value. `Page.Defer(services => …)` does the same for work that needs scoped services such as a DbContext: it runs in its own DI scope, started once the payload is written, so it never shares a scoped service with the request's other loaders.
- For a client navigation the response is NDJSON: the payload, then one line per value as it completes. For a document, .NET posts each value to the sidecar's `/resolve` under a per-render id. The sidecar settles the promise the page reads with `use()` and writes `<script>self.__netjes_d(id, value)</script>` into the stream, which the client entry reads before it hydrates.
- A value has at most `StreamTimeout` (60 s by default) to complete. One that has not arrived by then is reported as failed, so no boundary waits forever.
- `deferred(schema)` from `netjes/zod` validates a deferred value when it arrives. It returns a thenable rather than a Promise, which zod would treat as an async schema, and the thenable stays the same across renders.
- The sidecar renders with `progressiveChunkSize: Number.POSITIVE_INFINITY` in both of its render paths (`render` and `renderToStream` in `packages/netjes/src/server-render.tsx`). A boundary that is ready when the shell goes out is written in place, whatever its size. A boundary still waiting then, for a deferred value or lazy code, streams after the shell and needs JavaScript to replace its fallback.

## Alternatives considered

- **Await deferred values for documents.** Simpler, but the first load would wait for the slowest value.
- **A duplex request between .NET and the sidecar.** One connection for the payload and the values, but HTTP/1.1 duplex is fragile across `HttpClient` versions. Separate `/resolve` posts are plain requests.
- **A larger finite `progressiveChunkSize`.** It moves the threshold rather than removing it: a long enough page still loses its content without JavaScript, and nobody can say which size is safe.
- **A `<noscript>` style that hides fallbacks and shows the hidden elements.** The hidden elements sit at the end of the body, so the content would show in the wrong place, after the footer.
- **Wait for everything, for every visitor.** It would also show what is still pending, but the first byte would wait for the slowest `Page.Defer` value, which is what streaming exists to avoid.

## Consequences

- Status and headers are fixed when the shell is sent: a deferred value cannot turn the page into a 404.
- Without JavaScript, every boundary that was ready with the shell shows. Only what is still pending keeps its fallback, as in Next.js.
- The first byte comes no later: a ready boundary has rendered by the time the shell is ready, and the limit only decides where its bytes go within the same flush. Timed in the sidecar's process on 20 kB and 200 kB documents, the first chunk left at the same moment with and without the limit (0.81 ms and 8.8 ms at the median; [architecture](../architecture.md#streaming) has the measurement). A document with a large ready boundary is about 1 kB smaller (no fallback, hidden element or reveal script), and the browser paints it from the top as its bytes arrive instead of painting the fallback first.
- Even so, React holds back a boundary inside a `<ViewTransition>` that contains an image, to reveal it once the image has loaded; that one needs JavaScript as well.
- `Page.Defer(task)` takes a task the loader has already started, in the loader's own scope. Work that uses scoped services takes the overload with a service provider.
