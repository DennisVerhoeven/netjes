# 0002 – The server owns routing

Status: accepted

## Context

Both sides need routes: .NET to map requests to pages and loaders, the browser to navigate without reloading. Next.js ships a route manifest to the client and matches URLs there.

## Decision

Netjes implements the file conventions once, in C# (`RouteTable.Build`), and maps every page as an ordinary ASP.NET Core endpoint. The client router never matches URLs. For each navigation it requests the URL from the server with an `X-Netjes-Data: 1` header, and the payload that comes back names the page module and the layout segments to render.

## Alternatives considered

- **Generate a manifest at build time and match on both sides.** Faster navigations for pages without data, but two matchers that must agree on precedence, constraints and catch-alls, and C# endpoint conventions (authorization, rate limiting) would not apply to matches made in the browser.
- **Scan the files in the Vite plugin and send the routes to .NET.** .NET would then depend on the JavaScript toolchain to know its own routes.

## Consequences

- Every navigation is a request, even to a page without a loader. Links prefetch on hover, focus and touch to hide the latency, and back and forward show pages the router has kept in memory.
- Authorization, output caching, rate limiting and other endpoint conventions apply to client navigations as they do to full page loads.
- Redirects, 404s and errors behave the same with and without JavaScript.
