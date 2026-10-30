# 0018 – A startup failure that says what to do ends the app in one line

Status: accepted

## Context

Some reasons an app cannot start are known, and their message says all there is to say, with what to do: there is no production build, Node.js cannot be started, the sidecar script is not installed or built, or in development the Vite dev server does not start because something else has its port, `vite.config.ts` has an error, or a package it imports is not installed. Left to .NET, such a failure ends the process as any exception at startup does. A hosted service whose start throws makes the host log the exception with its stack, and `Run()` throws it again, unhandled: the runtime prints it once more, with its stack, and aborts the process, which exits with code 134 (SIGABRT) on Linux and macOS. The missing build is thrown from `UseNetjes()`, in the app's own `Program.cs`, before the host starts, and ends the same way without the host's entry. In a terminal, under `dotnet watch` or in a container's log, the message is lost among the stack frames, and the exit code reads as a crash.

A test host needs the failure as an exception. `WebApplicationFactory` runs the app's `Program` in the test runner's process, and hands the test a client once the host has started: the exception from a failed start is how the test learns why.

## Decision

- These failures are a `NetjesStartupException` (internal), whose message says what is wrong and what to do.
- When the app is the program its process runs, it ends with that message as one critical log entry, without the stack, and exit code 1. The app is that program when the host's application name is the entry assembly's: the host sets it so by default, and `WebApplicationFactory` gives the app its own name in the test runner's process.
  - A failure of the Node processes to start is kept rather than thrown. Once the host has started, the app sets exit code 1 and stops, since stopping during the start would cancel the web server's own start, which fails the same way. It logs the reason as the host stops, after the host's own messages ("Application started. Press Ctrl+C to shut down."), so that it is the last thing the app says. The export does not run while such a failure is pending.
  - The missing build is still thrown from `UseNetjes()`, so the app can catch it. If nothing does, a handler of the process's unhandled exceptions, registered for that exception alone, logs the reason, writes out the console logger and exits with code 1 before the runtime prints anything.
- In any other process, such as a test host's, the start fails with the exception.
- The development sidecar reports why the Vite dev server did not start on stdout (`NETJES_FAILED`: `vite-port-in-use` with the port, or `vite-failed` with Vite's message in plain text and the error's code) and exits, so that Node does not print the error with its own stack. .NET builds the line from it: the port and how to move Vite, or what Vite said, its code frame included, and `npm install` when a package could not be found.

## Alternatives considered

- **Leave them to .NET.** The message is there, but after a screen of stack frames, twice for a hosted service, and the exit code says the process crashed.
- **Stop instead of throwing in every process.** A test host then gets a client whose first request fails with an `ObjectDisposedException` from the disposed host, which does not say why.
- **Tell a test host by its lifetime or its server.** `WebApplicationFactory` keeps the app's `ConsoleLifetime`, and it can run the app on Kestrel rather than `TestServer`.
- **Exit from `UseNetjes()` at once.** That skips the app's own `catch` and `finally` blocks, where an app that wraps its startup in them logs the failure its own way and flushes its log.
- **Check for the build in a hosted service, and let `UseNetjes()` go on without it.** The `Map...` calls that check a page or layout against the routes (`MapPageLoader`, `MapLayoutLoader`, `MapPageAction`) would need routes the app does not have, or fail with a misleading message.

## Consequences

- Under `dotnet run`, an IDE, `netjes dev`, `netjes start`, `netjes export` and in a container these failures end in one log entry (several lines for a Vite code frame) and exit code 1; `dotnet watch` then waits for a change, as after any exit.
- When the Node processes do not start, the app listens for a moment between its start and its stop: a page requested then fails, and `/healthz` reports Unhealthy.
- An app whose application name is set to another name than its assembly's gets the exception, as a test host does, and unhandled it ends the process with the stack.
- A host that a test project builds itself with `WebApplication.CreateBuilder`, rather than through `WebApplicationFactory`, has the test assembly's name, which is the entry assembly's, so it counts as the program. When its Node processes do not start, its start succeeds, the app stops on its own and sets `Environment.ExitCode` to 1, and the test gets no exception; `UseNetjes()` still throws for a missing build. Given another name (`WebApplicationOptions.ApplicationName`), as `WebApplicationFactory` gives the app its own, its start fails with the exception.
- Other exceptions at startup, such as a route conflict, a `pages/` folder of the pages router, or a loader whose parameters minimal APIs cannot bind, are not startup failures in this sense: they point at the app's own code, and keep their stack.
