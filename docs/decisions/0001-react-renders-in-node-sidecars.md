# 0001 – React renders in a pool of Node sidecars

Status: accepted

## Context

Server-side rendering React needs a JavaScript runtime, and .NET has none built in. The options are an engine embedded in the .NET process (ClearScript with V8, or Jint) or separate Node processes.

React's server renderer is also synchronous while it renders a page: `renderToString`, and the shell of `renderToPipeableStream`, hold the JavaScript thread until they finish. One thread renders one page at a time, however many cores the machine has.

## Decision

- .NET runs Node processes next to the app, the sidecars, and renders a page by posting its payload to a sidecar's `/render` endpoint on loopback.
- .NET starts and supervises each process. A sidecar listens on a random port on 127.0.0.1 and reports it in a `NETJES_READY {"port":…}` line on stdout. Only the first such line of a process counts, because page code shares stdout. Every request carries a secret token, made for each process and passed in its environment; the sidecar removes it from `process.env` before page code runs. A sidecar exits when its stdin closes, so it goes down with the .NET process, even after a SIGKILL.
- .NET restarts a process that crashes after a successful start, with exponential backoff: 1 s, doubling up to 30 s, reset once a process has stayed up for 30 s. .NET retries a failed relaunch on the same backoff. It does not restart a process that never became ready, since that would fail again, and reports the process's error instead.
- The sidecar aborts a render when .NET disconnects, when its shell is not ready within `RenderTimeout`, or when its stream runs past `StreamTimeout`.
- `NetjesOptions.SidecarProcesses` (default 1) sets how many processes run in production. Development runs one, because that process also hosts the Vite dev server ([ADR 0004](0004-vite-for-dev-server-and-builds.md)).
- .NET picks the process for each render: a ready one before one that is restarting, then the one with the fewest renders in flight, rotating on ties. A streamed render holds its process until its stream is disposed, and its render id maps to that process, so the `/resolve` posts that carry its deferred values ([ADR 0012](0012-streamed-documents-and-deferred-values.md)) reach the process that is waiting for them.
- A render that finds its process gone goes to another ready process. A production document gets the client-rendered fallback when none is ready ([ADR 0013](0013-client-rendered-fallback.md)). A render waits for a restart, up to `RenderTimeout` (`StreamTimeout` for a complete document), in four cases: with that fallback turned off, in development, for a command such as `netjes export`, and when it is the render that first finds its process gone.
- The health check (`AddHealthChecks().AddNetjes()`) reports Healthy when every process is ready, Degraded while some are restarting, and Unhealthy when none is ready.

## Alternatives considered

- **ClearScript (V8 embedded).** Fast and in-process, but it ships large native binaries per platform, and React's server renderer expects Node or web stream APIs that would need polyfills. Vite's `ssrLoadModule`, which loads fresh modules on every edit in development, would not be available.
- **Jint (a managed interpreter).** No native dependencies, but far too slow for React SSR on real pages, and incomplete for modern bundles.
- **No SSR, client rendering only.** The simplest option, but it gives up first paint, search indexing and forms that work without JavaScript.
- **One Node process.** Less memory, but CPU-heavy pages under load queue behind each other on one core while .NET has cores to spare.
- **Node's `cluster` module behind one port.** Less code in .NET, but the primary hands out connections round-robin without knowing about renders in flight, and a deferred value's `/resolve` post could reach a worker that is not holding that render.
- **Worker threads in one process.** Cheaper in memory, but each thread needs its own copy of the server bundle and module graph, as a process does, and a crash in one thread's native code takes the others down.

## Consequences

- Production images need the Node binary (about 100 MB) next to the ASP.NET Core runtime. The template's Dockerfile copies it from the official Node image.
- In development one process hosts the Vite dev server and renders, so HMR and SSR share one module graph.
- A render is one request on loopback: a few milliseconds on warm pages.
- Memory grows with `SidecarProcesses`: each process loads Node, React and the server bundle.
- A process's in-memory state, such as a module-level cache in page code, is not shared with the other processes and does not survive a restart.
