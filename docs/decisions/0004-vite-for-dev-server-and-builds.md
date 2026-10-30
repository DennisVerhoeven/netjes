# 0004 – Vite for the dev server and builds

Status: accepted

## Context

Netjes needs a dev server with hot module replacement and React Fast Refresh, an SSR module loader that sees edits at once, and production builds for the browser and the server.

## Decision

Netjes uses Vite with `@vitejs/plugin-react`, wrapped in the `netjes()` plugin.

- In development the sidecar hosts the Vite dev server and renders with `ssrLoadModule`. The browser loads modules straight from the dev server, cross-origin to the .NET port, which Vite's default CORS policy allows for localhost.
- The sidecar inlines the CSS that the page and its layouts import into the server-rendered HTML, as `<style data-vite-dev-id>` elements, which the Vite client adopts, so development has no flash of unstyled content.
- `vite build` writes the client bundle and its manifest. `vite build --ssr` writes the server bundle with every dependency inlined, plus a copy of the sidecar script (`dist/server/netjes-sidecar.mjs`).

## Alternatives considered

- **Proxy Vite through ASP.NET Core**, so the browser talks to one origin only. That avoids CORS, but needs WebSocket proxying for HMR and a list of Vite URL prefixes that could collide with the app's routes.
- **esbuild or Rolldown on their own.** Fewer moving parts, but HMR, Fast Refresh and the SSR module graph would have to be built by hand.

## Consequences

- Development uses two ports: the app's and Vite's (5173, `Netjes:ViteDevPort`). Reaching the dev server from another host, such as from outside a container, needs `NETJES_VITE_HOST` or `NETJES_VITE_ORIGIN` and Vite's CORS settings.
- Production needs no `node_modules`: `dist/` is self-contained.
