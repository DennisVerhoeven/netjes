# Benchmarks

Netjes has a benchmark you run on demand: `bench/netjes/run.sh` publishes a small Netjes app in Release, starts it in Production, puts load on four kinds of request, kills its sidecar under load with the client-rendered fallback on and then off, and writes the results as JSON and as the markdown tables below. Absolute numbers belong to the machine they were taken on, so compare runs from the same machine with the same versions; every result lists both.

## What is measured

### The app

`bench/netjes/app` is a Netjes app on the app router with fifty items in memory, the same on every run. It has a root layout with two links, plain CSS and generated zod schemas, like a small real app, and no database, so the numbers are Netjes's and not a database's. It takes Netjes from the checkout and React, Vite and zod from the repository's own install, so a run measures the code it was built from.

| Route | Request | What it exercises |
|---|---|---|
| `document` | `GET /items` | A full page: the C# loader, the payload, the sidecar rendering React into a streamed HTML document. |
| `navigation` | `GET /items` with `X-Netjes-Data: 1` | What the client router asks for on a navigation: the same loader and its JSON payload, with no rendering. |
| `streamed` | `GET /items/7` | A document with a `Page.Defer` field that takes 50 ms (`Bench:SlowFieldMs`): the shell goes out first, the field streams in after it. |
| `cached` | `GET /cached` | The list page under `CachePage`, answered by ASP.NET Core's output cache after the first request. |

Before any load, each route is requested twice and the second answer is checked against the route's expectations (status, content type, a piece of the body, a header such as the output cache's `Age`). A route that fails its check stops the run: a benchmark of an error page measures nothing.

### The steps

Each route gets a warm-up (8 connections for 10 s, so the JIT and V8 are past their first passes), then two kinds of step of 15 s each:

- **Latency at fixed arrival rates (open loop)**, at 25, 100 and 400 requests per second. Request *i* is due at *i*/rate seconds whether or not the earlier ones have been answered, and its latency counts from when it was due. A server that stalls is charged for every request that waited, not only for the few in flight; a tool that sends only when an answer comes back misses those, which is called coordinated omission. TTFB is the time until the response headers arrive; total, until the last byte. Percentiles (p50, p95, p99, nearest rank) cover the good answers, those that meet all of the route's expectations; other answers count as errors, by kind (a wrong status by its number, any other mismatch as `<status> not as expected`, which is `200 not as expected` for the routes here). The load generator checks every answer as it arrives: the status, a prefix test on the content type, a header lookup and, for a route with `contains`, a byte search of the body, with no decoding or parsing, so the check costs far less than the request it belongs to.
- **Throughput at fixed concurrency (closed loop)**, with 1, 8 and 32 connections, each sending its next request as soon as the previous one is answered. Throughput is good answers per second. These latencies leave out queueing, so take latency from the open-loop table.

The load generator is `bench/netjes/bench.mjs`, plain Node with no dependencies, on 4 worker threads by default. It lives under `bench/netjes/`, apart from the packages and the examples.

### Sidecar restart under load

The restart is measured twice, once in each row of its table:

- **default**: the app as `run.sh` started it, in Production, where `ClientRenderFallback` is on.
- **off**: the app started again with `Netjes__ClientRenderFallback=false`. Before its restart, `document` is checked and warmed up again, as at the start of the run.

Each time, at 50 requests per second on `document`, the run kills the app's oldest sidecar with SIGKILL after 5 s and keeps the load on for 15 s more. Every answer is checked against the route's expectations, as the routes are before the load, and counts as one of three things:

- **Good answer**: the page, an answer that meets the expectations.
- **Fallback shell**: an answer with the expected status that is the document Netjes writes itself while no sidecar is ready, with the head, the assets and the payload but not the page, for the browser to render. Netjes puts a comment after the payload, which is what the run looks for, before it checks the expectations, as the payload can hold the text a route expects.
- **Failed**: anything else, such as a connection error or timeout, another status, or an answer that misses the expectations. A render in flight on the killed process fails with a 500, and counts as failed even when its error page went out as a shell.

The table gives, in milliseconds where it is a time:

- **Healthy again**: from when the kill command returned until `/healthz` answers `Healthy` again, after it was seen otherwise (`Unhealthy` with one sidecar, `Degraded` with more).
- **First good answer**: from the kill until a request sent after it gets the page.
- **Fallback shells**: how many answers were shells.
- **Failed requests**: how many failed, out of all the requests of the measurement, by kind (an error such as `timeout`, a status, or `200 not as expected`).
- **Total p99 before / after**, **Max after**: the latency of the pages answered before the kill, from when each request was due to its last byte, and of the pages answered after it, including the ones in flight at the kill that it held up. Shells are in neither; the JSON has their latencies.

Netjes waits one second before it restarts a crashed sidecar (the wait doubles on repeated crashes, up to 30 s). With `ClientRenderFallback` on, a document request that arrives while no sidecar is ready gets the shell at once: the outage shows as fallback shells, and First good answer is when the server renders pages again. With it off, such a request waits for the restart, up to `RenderTimeout` (15 s), and gets the error page if none comes: the outage shows in Total p99 after and Max after. With one sidecar, the restart takes that second plus Node's start-up. With `--sidecars 2`, the other process answers in the meantime, so neither mode falls back or waits.

### Memory

The resident memory (RSS) of the app's process tree, added up by executable: `dotnet` for the app and `node` for its sidecars. It is taken when the app has started and before any page is requested (idle), sampled every 250 ms during the steps (peak), and taken again after the last step (after the load). It reads `/proc`, so it is measured on Linux only.

### Machine and versions

Every result records the CPU, cores and threads, memory, OS and kernel, the 1-minute load average (before `run.sh` built the app, at the start of the run and after it), the .NET and ASP.NET Core runtimes the app loaded, the Node versions of the sidecar and of the load generator, the .NET SDK, the Netjes version and commit (marked `+changes` when the checkout had uncommitted changes), React, Vite, zod and TanStack Query, and the run's settings.

## Running it

It needs `npm ci` at the repository root, the .NET SDK from `global.json`, and Linux for the memory figures and CPU pinning.

```bash
bench/netjes/run.sh                                        # the full run, about 9 minutes
bench/netjes/run.sh --quick                                # a smoke run: 3 s steps, about 2 minutes
bench/netjes/run.sh --server-cpus 0-7 --client-cpus 8-11   # the app and the load generator on their own cores
bench/netjes/run.sh --sidecars 2                           # Netjes:SidecarProcesses for the app
```

`run.sh` generates the app's types, publishes it to `bench/netjes/.out/`, starts it on port 5480 (`--port` or `BENCH_PORT`), waits for `/healthz`, runs the load generator and stops the app. For the second restart the load generator has `run.sh` stop the app and start it again on the same port with `Netjes__ClientRenderFallback=false`; the two apps log to `bench/netjes/.out/app.log` and `app-without-fallback.log`. It writes `bench/netjes/results/<time>.json` and `<time>.md`, copies them to `latest.json` and `latest.md`, and prints the markdown. Its other options go to the load generator:

| Option | Default | |
|---|---|---|
| `--rates <list>` | `25,100,400` | Open-loop arrival rates, requests per second; empty to skip |
| `--concurrency <list>` | `1,8,32` | Closed-loop connection counts; empty to skip |
| `--warmup <s>`, `--duration <s>` | 10, 15 | Warm-up per route, length of each step |
| `--workers <n>` | 4 | Load generator threads (the restart always runs on one) |
| `--max-connections <n>` | 256 | Connection cap for the open loop; requests over it wait, and the wait counts |
| `--restart-rate`, `--restart-before`, `--restart-after` | 50, 5, 15 | Each restart's arrival rate and seconds of load before and after the kill |
| `--only <names>` | all | Only these routes, for example `--only document,cached` |
| `--provisional`, `--note <text>` | | Mark the results PROVISIONAL (machine under load), add a note |

`node bench/netjes/bench.mjs --help` lists the rest. The harness has its own tests and type check: `npm test` and `npm run typecheck` in `bench/netjes` (`BENCH_TEST_PORT_BASE` puts the tests' servers on fixed ports from there up). `dotnet test Netjes.slnx` compiles the app.

Unlike the examples, the app does not commit its `generated/netjes.ts`: `run.sh` writes it from the C# types on every run, so it always matches the checkout. To type-check the app by hand, run `npm run generate` and then `npm run typecheck` in `bench/netjes/app`.

### Against another app

The load generator measures any HTTP app from a route list. With `--target`, `run.sh` builds and starts nothing:

```bash
bench/netjes/run.sh --target http://127.0.0.1:8080 --routes my-routes.json
node bench/netjes/bench.mjs --target http://127.0.0.1:8080/base/ --routes my-routes.json --pid 4242 \
  --kill 'kill -KILL $(pgrep -o -P 4242 -f my-renderer)' --meta my-meta.json --out results
```

`--pid` names the process whose tree is measured for memory and runtimes, `--kill` the shell command that takes the renderer down for the restart (when it exits non-zero, the run keeps the routes it measured and warns that the restart was not measured, with the command's exit code and stderr), and `--meta` a JSON file with `versions` and `settings` objects to record. Without them, memory and the restart are left out. `--relaunch-without-fallback <command>` adds the second restart, as `run.sh` does: after the first, the run runs the command, which starts the app again with `ClientRenderFallback` off and returns once it is healthy, then checks and warms up the restart's route and measures the restart again. When the command exits non-zero, or the route does not answer as expected after it, the second restart is left out with a warning. Without it, the run measures one restart, of the app as it was started, labelled `default`. If you build the app just before the run, pass the 1-minute load average from before the build as `--load-before-build`, as `run.sh` does, so the idle check is not set off by the build. A route list (`bench/netjes/routes.json` is the app's own):

```json
{
  "routes": [
    {
      "name": "document",
      "path": "/items",
      "headers": { "Accept": "text/html" },
      "expect": { "contentType": "text/html", "contains": "data-bench=\"items\"" }
    }
  ],
  "restart": { "route": "document", "health": "/healthz", "healthy": "Healthy" }
}
```

A route has a `name`, a `path` below the target (with a query if needed), and optionally a `method` (GET), `headers`, a `body`, and `expect`: `status` (200), `contentType` (a prefix), `contains` (text in the body) and `header` (a header that must be present). `restart` names the route to load during the kill, whose `expect` every answer during the restart is checked against, the health path to poll, and the whole body of a healthy answer.

## Reading the results

- **Compare like with like.** Only runs on the same machine, with the same versions and settings, compare. Run on a quiet machine: the results warn when the 1-minute load average was 1 or more before `run.sh` built the app (with `--target`, at the start of the run). The build itself lifts the load average at the start, so that figure is only recorded. A run marked PROVISIONAL was taken on a machine doing other work, and its numbers only show that the harness works.
- **Open loop.** Achieved counts good answers over the time they arrived in, so it reads a little above or below Rate when the server keeps up. Below 90% of Rate, the server could not keep up: requests queued, latency grew through the step, and the results warn. For `document` and `cached` TTFB and total are close, as the page is small; for `streamed`, TTFB is the shell and total includes the 50 ms field. `document` against `navigation` is the cost of server rendering; `document` against `cached`, what the output cache saves.
- **Closed loop.** Throughput rises with connections until the server is saturated; with one sidecar, `document` levels off where its single Node process is busy rendering (`--sidecars` adds processes). `streamed` is bound by the slow field: at most connections / 50 ms.
- **The load generator.** It shares the machine with the app. When one of its threads was busy more than 80% of a step, or it sent requests more than 5 ms late (p99), it set the pace rather than the server, and the results warn. More `--workers`, or `--server-cpus` and `--client-cpus`, help.
- **Restart.** With the fallback on (`default`), Fallback shells counts the requests that got a page for the browser to render instead, and First good answer is how long until the server rendered pages again. The time a browser takes to render a shell is not measured. With it off, Total p99 after and Max after show how long the requests that arrived during the outage waited for the restarted sidecar. Healthy again is about the same in both: the fallback does not change how fast a sidecar restarts.
- **Memory.** Node's heap grows under load and is not given back at once, so compare peaks between runs rather than the after-load figure.
- **The JSON** holds more than the tables: for each step the count, min, mean and max next to the percentiles, the send lag, the most requests in flight, how busy the load generator was, and errors by kind; for each restart, the same figures for the pages before and after the kill and for the fallback shells.

## Results

Runs on the same machine, newest first. Each is the markdown `run.sh` printed. No run on a quiet machine has been recorded yet; the one below is provisional.

### Netjes benchmark, 2026-10-29 23:19 UTC

**PROVISIONAL (machine under load)**: a full run on a machine shared with other builds and tests; it shows the harness works, not how fast Netjes is

| | |
|---|---|
| Target | http://127.0.0.1:21470/ |
| Machine | 12th Gen Intel(R) Core(TM) i5-12600K, 10 cores, 16 threads, 31.1 GiB memory, x64 |
| OS | Bluefin (Version: 44.20261027), kernel 7.2.7-200.fc44.x86_64 |
| Load average (1 min) | 4.30 before the build, 4.28 at the start, 8.54 after |
| Runtimes | .NET 10.0.14, ASP.NET Core 10.0.14, Node v26.12.0 (app), Node v26.12.0 (load generator) |
| Versions | netjes 0.0.1, dotnet SDK 10.0.403, react 19.3.1, react-dom 19.3.1, vite 8.3.5, zod 4.6.7, @tanstack/react-query 5.105.0 |
| Settings | warmupSeconds 10, stepSeconds 15, rates 25/100/400, concurrency 1/8/32, maxConnections 256, workers 4, server GC true, slowFieldMs 50, sidecars 1 |

#### Latency at fixed arrival rates (open loop)

Milliseconds from when each request was due. TTFB: until the response headers; total: until the last byte.

| Route | Rate (req/s) | Achieved (req/s) | TTFB p50 | TTFB p95 | TTFB p99 | Total p50 | Total p95 | Total p99 | Errors |
|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| document | 25 | 25.0 | 5.10 | 6.85 | 12.9 | 5.48 | 7.31 | 15.8 | 0 |
| document | 100 | 100 | 2.50 | 5.22 | 6.37 | 2.62 | 5.62 | 6.80 | 0 |
| document | 400 | 400 | 1.63 | 4.53 | 12.4 | 1.73 | 4.74 | 12.6 | 0 |
| navigation | 25 | 25.0 | 1.84 | 2.88 | 9.17 | 2.11 | 3.30 | 9.78 | 0 |
| navigation | 100 | 100 | 0.86 | 2.36 | 6.78 | 0.95 | 2.67 | 6.86 | 0 |
| navigation | 400 | 400 | 0.80 | 1.49 | 2.19 | 0.91 | 1.69 | 2.46 | 0 |
| streamed | 25 | 25.0 | 3.43 | 5.01 | 16.4 | 52.5 | 54.2 | 67.3 | 0 |
| streamed | 100 | 100 | 1.45 | 7.76 | 14.9 | 51.2 | 58.3 | 65.3 | 0 |
| streamed | 400 | 400 | 6.78 | 38.9 | 63.7 | 58.2 | 100 | 127 | 0 |
| cached | 25 | 25.0 | 1.60 | 2.48 | 7.16 | 1.85 | 2.90 | 7.73 | 0 |
| cached | 100 | 100 | 1.04 | 1.56 | 2.04 | 1.21 | 1.80 | 2.37 | 0 |
| cached | 400 | 401 | 0.70 | 1.20 | 1.58 | 0.81 | 1.38 | 1.79 | 0 |

#### Throughput at fixed concurrency (closed loop)

Each connection sends its next request when the previous one is answered. Latencies here leave out queueing.

| Route | Concurrency | Throughput (req/s) | Total p50 | Total p95 | Total p99 | Errors |
|---|--:|--:|--:|--:|--:|--:|
| document | 1 | 1373 | 0.62 | 1.34 | 2.38 | 0 |
| document | 8 | 2619 | 2.43 | 5.16 | 7.27 | 0 |
| document | 32 | 2662 | 11.6 | 18.9 | 23.4 | 0 |
| navigation | 1 | 9598 | 0.08 | 0.18 | 0.39 | 0 |
| navigation | 8 | 23863 | 0.22 | 0.78 | 2.60 | 0 |
| navigation | 32 | 42380 | 0.64 | 1.59 | 3.28 | 0 |
| streamed | 1 | 19.1 | 52.4 | 53.3 | 53.7 | 0 |
| streamed | 8 | 154 | 51.9 | 53.8 | 58.0 | 0 |
| streamed | 32 | 610 | 51.7 | 57.2 | 62.5 | 0 |
| cached | 1 | 9079 | 0.05 | 0.22 | 0.79 | 0 |
| cached | 8 | 27317 | 0.17 | 0.77 | 2.88 | 0 |
| cached | 32 | 66847 | 0.36 | 1.08 | 1.63 | 0 |

#### Sidecar restart under load

A renderer process killed under open-loop load. Milliseconds from when the kill command returned. A good answer is the page; a fallback shell is the document without it that Netjes sends while no sidecar is ready. ClientRenderFallback: default is the app as it was started (on in production unless the app turns it off), off the app started again with it off.

| ClientRenderFallback | Route | Rate (req/s) | Healthy again | First good answer | Fallback shells | Failed requests | Total p99 before | Total p99 after | Max after |
|---|---|--:|--:|--:|--:|--:|--:|--:|--:|
| default | document | 50 | 1090 | 1103 | 53 | 1 of 1000 (500: 1) | 8.86 | 16.1 | 25.0 |
| off | document | 50 | 1095 | 1111 | 0 | 1 of 1000 (500: 1) | 7.67 | 960 | 1109 |

#### Memory (RSS, MiB)

| Process | Idle | Peak under load | After the load |
|---|--:|--:|--:|
| dotnet | 99.0 | 205.9 | 157.2 |
| node | 70.9 | 254.3 | 207.4 |

#### Warnings

- the machine was not idle: its load average was 4.30 before the build
- navigation with 32 connections: the load generator was busy 84% of the step, so the server may be faster than this shows
- streamed at 400/s: the load generator sent requests up to 5.1 ms late (p99); that delay counts as latency
- cached with 32 connections: the load generator was busy 95% of the step, so the server may be faster than this shows
