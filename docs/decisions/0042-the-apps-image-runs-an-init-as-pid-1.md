# 0042 – The app's image runs an init as PID 1, so a crash ends the container

Status: accepted

## Context

The production images of the template and both examples started the app with `ENTRYPOINT ["dotnet", "<App>.dll"]`, so .NET was the container's first process, PID 1. The kernel treats a PID namespace's first process as its init: a signal sent to it, by itself included, does nothing unless the process handles that signal, SIGKILL and SIGSTOP from outside the namespace aside. .NET ends a process that crashes with `abort()`, which raises SIGABRT: an exception the app does not catch, `Environment.FailFast`, a stack overflow. As PID 1 the process was not ended by it. On arm64 the thread that crashed spun at 100% of a CPU and the process never exited, so the container stayed up and `restart: always` never acted. A crash at startup left the container unhealthy, but after a later one, such as on a thread of the app's own, the rest of the app kept serving and its health check still passed. On x86-64, under emulation, the process ended after a few seconds, but with code 139 instead of 134. Netjes's own startup failures were not affected, since they exit with code 1 ([ADR 0018](0018-startup-failures-end-in-one-line.md)); every other crash was.

Compose's `init: true` and `docker run --init` put Docker's init in front of the app, but only in the stacks that set them: `docker run` without the flag, Kubernetes and most other container hosts run the image's entry point as PID 1.

## Decision

- The Dockerfiles of the template, the Blog and TaskBoard have a stage `tini`, built from the SDK image the build stage uses, that installs Ubuntu's `tini` package. The production stage copies its static build, `/usr/bin/tini-static`, to `/usr/bin/tini`.
- The production image's entry point is `tini -s -- dotnet <App>.dll`. tini is PID 1 and runs the app as its child. It passes the signals it gets on to the app, so `docker stop` stops the app at once, as before; it reaps processes left without a parent; and it exits with the app's exit code, or 128 plus the number of the signal that ended the app, which is 134 for a crash.
- `-s` makes tini a subreaper, so under another PID 1, such as Docker's own init with `docker run --init`, it still reaps what the app leaves behind and does not warn that it is not PID 1.
- Arguments still reach the app: TaskBoard's `migrate` service runs `tini -s -- dotnet TaskBoard.dll migrate`.
- The library does not change: Netjes's own startup failures exit with code 1, and other crashes now end the container with 134.

## Alternatives considered

- **Install tini in the production stage.** It ties that stage to a runtime image with a package manager, which a chiseled image does not have. The static binary from a builder stage runs on any `RUNTIME_IMAGE`.
- **A tini stage from the runtime image.** It breaks the same way with a chiseled `RUNTIME_IMAGE`, and the SDK image is there for the build anyway.
- **tini's release binary, downloaded in the Dockerfile.** It needs a checksum for each architecture, kept up to date by hand.
- **A pinned package version.** A version belongs to one Ubuntu release, so moving `SDK_IMAGE` to the next one would break the build, and a pin holds back fixes.
- **An exit handler in the app**, `AppDomain.UnhandledException` calling `Environment.Exit(1)`. It covers exceptions only: `Environment.FailFast` and a stack overflow still abort, and spin. Registered by the library for every exception, it would also report every crash as code 1, where ADR 0018 keeps code 1 for Netjes's own failures and leaves the rest to .NET.
- **Only `init: true` in the Compose files.** `docker run` and the hosts that run the image without Compose would still run .NET as PID 1.
- **A shell as the entry point** (`sh -c`). A shell that stays PID 1 ignores the SIGTERM of `docker stop` and does not pass it on, and one that hands its process over to the app makes .NET PID 1 again.
- **dumb-init.** It does the same job; tini is the init that Docker's own `--init` runs, and Ubuntu's package ships a static build of it.

## Consequences

- A crash ends the container with code 134 within seconds, and a restart policy, such as `restart: always` in the production Compose files, starts it again.
- Monitoring that expects code 1 for every failed start sees 134 for the exceptions that are not Netjes's own startup failures.
- `docker stop` stops the app at once, and the container exits with the app's own code.
- Building the image needs Ubuntu's package archive, or a mirror of it, in a builder stage, and installs whatever version of tini the archive has (0.19.0 in Ubuntu 24.04). The production image grows by the size of the binary, about 550 KB.
- An app made with an earlier template keeps .NET as PID 1 until it copies the stage, the `COPY` and the `ENTRYPOINT`, or runs its container with an init.
