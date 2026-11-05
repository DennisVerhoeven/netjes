# 0031 – A sidecar that stops answering is restarted like one that crashed

Status: accepted; extends [ADR 0001](0001-react-renders-in-node-sidecars.md)

## Context

.NET restarts a Node process that exits ([ADR 0001](0001-react-renders-in-node-sidecars.md)), but a process that stops answering without exiting stayed ready: one stuck in a synchronous loop, deadlocked in native code, or stopped by a signal. Its port still took connections, so nothing was refused and nothing exited. Every server-rendered page went to it and waited out `RenderTimeout` (15 s), and its error page as long again, before a plain 500 went out; the health check answered Healthy; and the client-rendered fallback ([ADR 0013](0013-client-rendered-fallback.md)), which serves documents while no process is ready, never stepped in. Only restarting the app ended it.

## Decision

- In production, each sidecar watches its process from the moment it reports ready until it exits, is replaced, or the sidecar stops. Every five seconds it looks at the process: one that answered a render or a deferred value in the last five seconds is left alone, and any other is pinged.
- A ping is `GET /health` with the process's token, on a loopback connection of its own, written by hand rather than sent through the `HttpClient` that carries renders. Any bytes back count as an answer, whatever the status.
- A process that sends nothing back within `RenderTimeout`, and never less than five seconds, connecting included, is taken out of the ready ones and its process tree killed, with an error in the log. It then restarts as a crashed process does, with the same backoff, and the renders waiting for it wait for the restart. A connection that is refused, reset or closed without an answer counts as no verdict: the exit handler and the renders' own refusal handling deal with a process that exited or does not listen.
- A process that a refused connection took out is still pinged, so one that listens again but does not answer is killed rather than taken back after the two seconds a refusal waits.
- A process that exits while a ping waits for its answer gets a warning in the log that a sidecar script of the app's own (`Netjes:SidecarScript`) must answer `GET /health` without reading a request body. The sidecar Netjes ships has always answered it.
- Development is left out: there the process also hosts the Vite dev server, and a developer can stop it at a breakpoint in page code.
- The health check is unchanged. It reports a process that was taken out as restarting, as it reports one that crashed: Unhealthy when none is ready, Degraded when others are.

## Alternatives considered

- **A ping in the health check only.** `/healthz` would report the hang, but nothing would restart the process: Docker and Compose mark a container unhealthy without restarting it, and the template's `restart: always` acts when the container exits.
- **Take the process out without killing it.** A stopped or deadlocked process would never come back, and nothing would restart it.
- **Count render timeouts.** It misses a process that hangs while idle, and takes out a process for one slow page.
- **A setting of its own for the hang timeout.** `RenderTimeout` already says how long a render may hold a process, and a ping waits as long.
- **Ping through the `HttpClient`.** Each ping would record an HTTP client span and an `http.client.request.duration` sample next to the renders', every five seconds for every process, and the HTTP client instrumentation that apps add to OpenTelemetry has no way to leave one client out.
- **Ping a process that is busy.** A render that finished proves what a ping would, and a ping under load would only queue behind the renders.

## Consequences

- A process that hangs is out of the ready ones within about `RenderTimeout` plus ten seconds of its last answer (25 s with the defaults), and back after the restart's backoff. Meanwhile documents get the client-rendered fallback when no process is ready, and `/healthz` reports Unhealthy or Degraded.
- A host needs no health-based restart for this: a restart policy that acts when the container exits is enough, since the app itself restarts the process.
- A synchronous render that holds its process for longer than `RenderTimeout`, a long garbage-collection pause or a starved CPU can get a process killed that would have recovered. The five-second floor, pinging only a process that has been silent, and production only keep that rare; such a render fails at `RenderTimeout` anyway.
- On Linux a process whose accept queue is full counts as hung, since the connection is not refused but waits; on Windows a full queue refuses, which is no verdict.
- A sidecar script of the app's own must answer `GET /health`. One that reads every request as a render and exits on the empty body is restarted after every ping, and the warning says why.
