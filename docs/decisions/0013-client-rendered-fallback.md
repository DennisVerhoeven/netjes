# 0013 – A client-rendered fallback when no sidecar is ready

Status: accepted

## Context

Node sidecars render every document ([ADR 0001](0001-react-renders-in-node-sidecars.md)). A process that crashes is restarted with backoff, and the pool keeps serving from the processes that are up. With none up, a document request would wait up to `RenderTimeout` (15 s) for a restart, its error page would wait as long again, and then the plain error page would go out with a 500. A process that keeps crashing would turn that into minutes of errors, although .NET has everything the page needs: the loaders have run, and the client build renders any page from its payload, as it does on every client navigation.

## Decision

- In production, when no sidecar process is ready at the moment a document would be rendered, .NET writes the document itself. Its head is what `Document` renders (charset, viewport, the CSP nonce, the deferred-values bootstrap, stylesheets, module preloads and the client entry), with the title, description and robots from the C# metadata. Its body holds only the payload, marked `document.clientRender`. The client entry renders it with `createRoot(document)` instead of hydrating it; React keeps the shell's scripts and stylesheets and replaces the rest.
- Everything before the render runs as usual: loaders, authorization, the status code, not-found, error and redirect results, antiforgery and the security headers. Client navigations never needed a sidecar. The health check stays Unhealthy, as it is whenever no process is ready.
- `Page.Defer` values follow the shell as the `self.__netjes_d` scripts a streamed document carries, each once it completes, for at most `StreamTimeout`, so the page renders at once with its Suspense fallbacks.
- Each fallback logs a warning naming the route and the path, and adds one to the `netjes.sidecar.fallbacks` counter. The output cache never stores it, and it always goes out with `Cache-Control: no-store` in place of any header the page set, as draft pages do, so no browser keeps it either, nor a CDN that follows `Cache-Control`. The headers a CDN reads ahead of `Cache-Control` are removed from it, as from draft pages ([ADR 0019](0019-forced-no-store-removes-cdn-cache-headers.md)).
- Only the moment of the render counts. A render that went to a process and then fails or times out keeps its error page, because it may be the page that brought the process down. The render that first finds its process gone waits for a restart, unless another process is ready.
- Development never falls back, since the Vite dev server that serves the client runs inside the sidecar. Nor does a command: an export without a sidecar fails rather than write pages without their HTML. Both wait for a restart, up to `RenderTimeout` (`StreamTimeout` for a complete document).
- `NetjesOptions.ClientRenderFallback` (default true) turns the fallback off. Documents then wait for a restart, up to `RenderTimeout` (`StreamTimeout` for a complete document), and get the error page if none comes.

## Alternatives considered

- **Fail until a sidecar is back.** Available with `ClientRenderFallback` off, but an outage of Node becomes an outage of the site.
- **Also fall back when a render fails or times out.** It covers more, but a page that crashes its process would look healthy in the browser, and the request has already waited out its timeout.
- **Wait for the deferred values and put them in the payload.** Simpler for the client, but the page would wait for its slowest value, which streamed documents avoid ([ADR 0012](0012-streamed-documents-and-deferred-values.md)).
- **Let the client fetch the deferred values** with a data request. The loaders would run twice.
- **Keep the page's own `Cache-Control` on the shell.** A page that lets a CDN keep it for an hour would have the degraded shell served for up to an hour after the sidecars are back, and a browser could show it again on Back.

## Consequences

- During a sidecar outage, browsers with JavaScript get working pages. Clients without it (crawlers that run no scripts, link previews, forms without JavaScript) get an empty body with the title, description and robots from C#.
- The shell's `<title>` is the loader's title as it is; a layout's title template applies once the page renders. Metadata exported by page and layout files exists only in the browser.
- The shell's head is .NET's copy of what `Document` renders (`src/Netjes/Rendering/ClientRenderShell.cs`), so a change to one belongs in the other. A .NET test pins the shell of a fixture page (a nonce, every kind of asset, and the title, description and robots from C#) as a file in the package's test fixtures. A Vitest test renders that page's payload with `Document`, as a sidecar would, and compares the two heads element by element, nonces included, so a change to one fails until the other follows. Another test ties the bootstrap to `deferred.ts`, so a hash-based policy allows both by the same hash.
- A page's own `Cache-Control` does not reach the shell, and neither does a header the page set for a CDN (`CDN-Cache-Control`, `Cloudflare-CDN-Cache-Control`, `Surrogate-Control` and the like), so a page meant to be kept by a CDN is not kept while it renders in the browser.
- `StreamDocuments` and crawler detection make no difference in this mode: the shell always goes out first.
