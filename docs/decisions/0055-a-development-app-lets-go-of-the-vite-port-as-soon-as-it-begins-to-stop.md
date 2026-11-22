# 0055 – A development app lets go of the Vite port as soon as it begins to stop

Status: accepted; amends [ADR 0018](0018-startup-failures-end-in-one-line.md)

## Context

In development the sidecar runs the Vite dev server, on a fixed port (`ViteDevPort`, 5173) with `strictPort`, since browsers load modules from it. When a C# edit needs a restart, `dotnet watch` sends SIGTERM to the process it started (`dotnet run`), which passes it on to the app, and waits five seconds for it to exit. Then it kills `dotnet run` alone, which leaves the app running until its graceful stop is over, and starts the app again. The host stops its hosted services one after the other: the server first, which waits for the requests in flight (up to `HostOptions.ShutdownTimeout`, 30 seconds by default), and only then the sidecar pool. An app that took longer than five seconds to finish its requests kept the Vite port that long, and the app `dotnet watch` started next stopped with `vite-port-in-use` ([ADR 0018](0018-startup-failures-end-in-one-line.md)). `dotnet watch` then waited for a file to change, and the app stayed down, as it does in the template's development container.

## Decision

- In development, `NodeSidecarHostedService.StoppingAsync`, which the host runs before it stops any hosted service, has each sidecar write the line `stopping` on its process's stdin. The default sidecar script then stops the Vite dev server listening and ends its connections, so the port is free at once, and renders go on through Vite until stdin closes.
- When stdin closes, or a SIGTERM or SIGINT comes, the script lets go of the port before anything else, ahead of the handlers Vite adds for the same events, which close the dev server's HTTP server after its watcher and its module graph.
- A script that finds the Vite port taken at startup says so on stdout and tries the port every 100 ms for up to five seconds before it reports `vite-port-in-use`. The wait covers a process that sees its stdin close late (its app was killed outright, or it was busy with a render); five seconds is what `dotnet watch` gives the app it stops.

## Alternatives considered

- **Only wait for the port, for longer.** The old app's requests can take up to the host's shutdown timeout, or whatever the app sets it to, and a port that another program really keeps would be reported that much later.
- **Stop the whole sidecar when the app begins to stop.** The requests still in flight would fail their renders.
- **A request, such as `POST /stopping`, instead of a line on stdin.** A sidecar script of the app's own has to answer `GET /health` only; one that takes every other request for a render would fail on it, or exit. A line on stdin can be left unread.
- **Have the template's container wait longer** (`DOTNET_WATCH_PROCESS_CLEANUP_TIMEOUT_MS`). Every restart with a request in flight would wait for that request, and an app run another way would still hit the port.

## Consequences

- A restart under `dotnet watch` while the app has requests in flight starts the new app; the old one finishes them in the background. Browsers lose their connection to the old dev server at once, and their Vite client reloads the page once a dev server answers on the port again.
- From the moment it begins to stop, the old app's dev server serves no modules: its server renders go on, and pages in the browser wait for the new dev server.
- A Vite port that another program keeps is reported five seconds later than before, after a line that says the sidecar waits; `netjes dev` still checks the port before it starts the app.
- A sidecar script of the app's own gets `stopping` on its stdin in development.
