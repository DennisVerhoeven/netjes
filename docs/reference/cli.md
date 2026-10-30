# The netjes command

`netjes` comes with the `netjes` npm package. It creates apps, runs them in development and production, builds them, and asks the app itself for its generated types and its route table ([ADR 0007](../decisions/0007-a-node-cli-that-asks-the-app.md)).

In an app, run it with `npx netjes <command>` or through the npm scripts the template sets up (`npm run dev`, `npm run generate`, `npm run routes`). It needs Node.js `^22.22.2 || ^24.15.0 || >=26.0.0` (the range the `netjes` package and the app template declare as `engines`) and the .NET 10 SDK or newer; `netjes info` checks both.

```text
netjes <command> [arguments] [options]
netjes help [<command>]
netjes --version
```

| Command | What it does |
|---|---|
| [`new <dir>`](#netjes-new) | Create an app: ASP.NET Core with React pages, Tailwind CSS, TanStack Query, zod and Vitest |
| [`dev`](#netjes-dev) | Run the app in Development: `dotnet watch` with C# hot reload, Vite HMR for pages |
| [`build`](#netjes-build) | Generate the types, then build the client and server bundles into `dist/` |
| [`start`](#netjes-start) | Run the app in Production, from a build or a published app |
| [`generate`](#netjes-generate) | Write `generated/netjes.ts` from the C# types |
| [`routes`](#netjes-routes) | Print the route table and its warnings, as the app sees them |
| [`export [dir]`](#netjes-export) | Build, then write every page as static HTML into a folder |
| [`info`](#netjes-info) | Check the tools and the project setup |
| [`agents [dir]`](#netjes-agents) | Add `AGENTS.md`, `CLAUDE.md` and the Netjes skill for coding agents to an existing app |

`netjes help <command>`, `netjes <command> --help` and `netjes <command> -h` print a command's usage, its options with their defaults, and examples.

## Options and arguments

Every command takes these:

| Option | What it does | Default |
|---|---|---|
| `--cwd <dir>` | Run in another directory, usually the app's folder. It must exist. | the current directory |
| `-h`, `--help` | Show help, for the command when one is given. | |
| `-v`, `--version` | Print the version of netjes and exit. | |

Options can come before or after the command and its arguments. `--name Shop` and `--name=Shop` are the same; a value that starts with `-` needs the `=` form (`--cwd=-site`). `--` ends the options: what follows is an argument even if it starts with `-`.

Each command accepts only its own options and the ones above. An unknown command or option, an option without its value, a value for a flag, or one argument too many is a usage error: netjes prints one line on stderr, with a suggestion when one is close, starts nothing, and exits 1.

```console
$ netjes nwe my-shop
netjes: unknown command 'nwe'; did you mean `netjes new`?
$ netjes generate --chek
netjes generate: unknown option '--chek'; did you mean '--check'?
```

## netjes new

```text
netjes new <dir> [--name <Name>] [--packages] [--skip-install]
```

Creates an app in `<dir>`, runs `npm install` in it, and prints where things are and what to run next.

| Argument or option | What it does | Default |
|---|---|---|
| `<dir>` | The folder to create the app in. It must not exist yet, or be empty. | asked for in a terminal; required otherwise |
| `--name <Name>` | The .NET project name and root namespace. | the folder name in PascalCase: `my-shop` gives `MyShop` |
| `--packages` | Use the Netjes NuGet and npm packages even when netjes runs from a Netjes checkout. | off; outside a checkout the packages are used anyway |
| `--skip-install` | Do not run `npm install` in the new app. | off |

```bash
npx netjes new my-shop
npx netjes new my-shop --name Shop --skip-install
```

Names. The project gets three names: a .NET name (the `.csproj`, the root namespace and the assembly), an npm package name and a title. From the folder name `my-shop` they are `MyShop`, `my-shop` and `My Shop`; a name that starts with a digit gets `App` in front (`1shop` gives `App1shop`), and accents are dropped (`Mijn Café` gives `MijnCafe`, `mijn-cafe` and `Mijn Cafe`). `--name` is used as given for the .NET name, and must be letters and digits starting with a letter; the npm name and the title are made from it (`--name MyShop` gives `my-shop` and `My Shop`). netjes refuses, before it writes anything:

- `--name` with anything other than letters and digits, or starting with a digit (`--name my-shop`; it suggests `--name MyShop`);
- a folder name with no ASCII letters or digits, unless `--name` is given;
- a C# keyword as `--name`, such as `class` or `event` (a keyword cannot name a namespace);
- the name of a type the app's code uses by its simple name: `Program`, `SiteProps`, `Page`, `ServerAction`, `WebApplication`, `ValueTask`, `DateTimeOffset`, `TimeProvider`, `IEndpointRouteBuilder` and `FromFormAttribute`. A root namespace of that name hides the type, and the build fails (CS0101, CS0118 or CS0234). A folder named `page` is refused for the same reason, as it gives `Page`;
- an npm name that is one of the app's own dependencies, such as `react`, `vite` or `netjes` (npm refuses to install a package into one of the same name).

How the app references Netjes. From the published package, the `.csproj` gets `<PackageReference Include="Netjes" Version="[x.y.z]" />` and `package.json` gets `"netjes": "x.y.z"`, both the exact version of the netjes that ran `new`: the C# and npm halves speak one protocol. Run from a Netjes checkout (contributors), it references that checkout instead (a `ProjectReference` and a `file:` dependency), unless `--packages` is given. Such an app imports the package's compiled modules from the checkout's `packages/netjes/dist/`, so build them first, with `npm ci && npm run build` at the checkout's root, and again after changing `packages/netjes`.

Without `<dir>`, netjes asks for the folder when stdin and stdout are a terminal, and fails as a usage error otherwise, so scripts and CI never hang on a prompt. An empty answer or Ctrl+D at the prompt is a usage error too; Ctrl+C stops netjes with exit code 130, before it writes anything.

When `npm install` fails, the app stays where it is: netjes says so on stderr and exits with npm's exit code. Fix what npm reported, then run `npm install` in the app.

## netjes dev

```text
netjes dev [--no-watch]
```

Runs `dotnet watch run` in Development. The app starts its Node sidecar, which runs the Vite dev server, so C# changes hot reload and pages update through Vite HMR. The app listens on `http://localhost:5080` unless `ASPNETCORE_URLS` is set; the Vite dev server uses port 5173 (`Netjes:ViteDevPort`).

| Option | What it does | Default |
|---|---|---|
| `--no-watch` | Run once with `dotnet run` instead of `dotnet watch`. | off |

```bash
netjes dev
ASPNETCORE_URLS=http://localhost:3000 netjes dev
```

There is no `--port` option (nor `-p`): the app's URL sets the port, so `netjes dev --port 3000` fails and says to set `ASPNETCORE_URLS=http://localhost:3000` when you run `netjes dev`. `netjes start` answers the same way. netjes runs the app without its launch profiles, so a profile's `applicationUrl` does not apply either.

Before it starts anything, netjes checks that the folder has a `.csproj`, that `netjes` and `vite` are installed (`npm install`), that the app's port is free when the app gets one localhost URL, and that the Vite dev server's port is free. It reads Vite's port from the same settings as the app: `Netjes__ViteDevPort` from the environment, else `Netjes:ViteDevPort` from `appsettings.Development.json` (or the file of the environment `ASPNETCORE_ENVIRONMENT` names) or `appsettings.json`, else 5173. A port the app sets in code is not seen: netjes checks the one it read instead, and the app itself reports when the port it set is taken. Setting `Netjes__ViteDevPort` to the same port makes netjes check that one. Each of the checks fails with one line saying what to do, and exit code 1:

```text
netjes dev: port 5173 is in use, and the Vite dev server needs it; stop what is using it, or move Vite to another port: Netjes__ViteDevPort=5174 netjes dev
```

When the Vite dev server or the Node.js processes cannot start, such as for an error in `vite.config.ts` or a package it imports that is not installed, the app logs why in one log entry and exits with code 1, and `dotnet watch` waits for a file to change before it starts the app again (Ctrl+R starts it at once).

Stop it with Ctrl+C. `dotnet watch` does not stop on SIGTERM, so a tool that runs `netjes dev` for you (an IDE task, a process manager) should stop it with SIGINT.

## netjes build

```text
netjes build
```

Writes `generated/netjes.ts` (as `netjes generate` does), then runs `vite build` and `vite build --ssr`, which write `dist/client/` and `dist/server/`. It stops at the first step that fails, with that step's exit code. It runs the vite installed in the app's `node_modules`; when there is none it says to run `npm install`, and never downloads one.

`dotnet publish` runs `npm run build` itself (through `build/Netjes.targets`) and ships `dist/` with the app.

## netjes start

```text
netjes start [--dll <path>]
```

Runs the app in Production: `dotnet run -c Release` in the project, or `dotnet <path>` for a published app. In Production the app in the project serves the build in `dist/`; when `dist/` has none, netjes says to run `netjes build` first and starts the app anyway (an app can keep its build elsewhere with `NetjesOptions.DistDirectory`), which then stops with one line saying there is no build, and exit code 1. A published app runs from the folder the `.dll` is in, as it does when you start it by hand there: it serves the published `dist/` and reads the published `appsettings*.json`, not the project's. A relative path is resolved against the folder `netjes` runs in, or `--cwd`. `netjes` stops with an error when the path is not a file.

| Option | What it does | Default |
|---|---|---|
| `--dll <path>` | Run a published app's `.dll` (from `dotnet publish`) instead of the project, and run it from the `.dll`'s folder. | `dotnet run -c Release` in the project |

```bash
netjes build && netjes start
dotnet publish -c Release -o bin/publish && netjes start --dll bin/publish/MyShop.dll
```

ASP.NET Core listens on `http://localhost:5000` unless `ASPNETCORE_URLS` says otherwise.

## netjes generate

```text
netjes generate [--check]
```

Writes `generated/netjes.ts`: zod schemas for what the loaders and actions return, typed server action references and typed routes. The app also rewrites it when it starts in Development; commit it.

| Option | What it does | Default |
|---|---|---|
| `--check` | Write nothing, and exit 1 when `generated/netjes.ts` is out of date. For CI. | off |

When `--check` finds the file out of date, the error shows where it differs: the first block of changed lines, as `- 12: old line` from the committed file and `+ 12: new line` from the generated one, with line numbers, up to 20 lines and then a count of the rest. When the file differs again further down, a last line says up to which line. A missing file is reported as missing.

Like `routes`, it runs the app once in command mode: the app starts as usual, so every `Map...` call and option applies, but it binds only a throwaway loopback port, starts no Node sidecar and needs no build; it writes its result and exits. Startup code that needs a database can skip that work with `if (app.GetNetjesCommand() is null)`. A `Map...` call that throws, such as a loader whose parameters minimal APIs cannot bind, stops the app before it writes anything: the app prints the exception's message, then its stack trace, and the command exits with the app's exit code, which is not 0. It exits 2 when `NetjesOptions.TypesFile` is null.

## netjes routes

```text
netjes routes [--json]
```

Prints the pages as the app maps them (pattern, file, loader, page action, props type, output caching, authorization), the other endpoints (the built-in ones such as `/_netjes/image`, your own minimal API endpoints and the [server actions](../guides/actions-and-forms.md) at `/_netjes/actions/<name>`), and the [route warnings](../guides/routing.md#route-warnings). Warnings do not change the exit code.

| Option | What it does | Default |
|---|---|---|
| `--json` | Print the report as JSON. | off: a table |

The report is the only thing on stdout; the build and app output go to stderr, so `netjes routes --json | jq` works. Its shape:

```json
{
  "routes": [{ "pattern": "/blog/[slug]", "template": "/blog/{slug}", "file": "app/blog/[slug]/page.tsx", "layouts": ["app/layout.tsx"],
               "loader": true, "action": false, "props": "PostProps", "cached": true, "authorized": false }],
  "endpoints": [{ "method": "*", "route": "/healthz", "name": "Health checks" }],
  "warnings": [{ "kind": "overlap", "message": "…", "files": ["app/[id]/page.tsx", "app/[...rest]/page.tsx"] }]
}
```

`method` is the HTTP method an endpoint answers, or `"*"` for one that answers every method, as `MapHealthChecks` does.

## netjes export

```text
netjes export [dir] [--no-build]
```

Builds the app (as `netjes build` does), then runs it once in Production and writes every page as static HTML into `dir`, with `404.html` and the client assets. [Static export](../guides/static-export.md) has the rules: which pages can be exported, `.StaticParams(...)` for dynamic ones, and where the output may go.

| Argument or option | What it does | Default |
|---|---|---|
| `[dir]` | The folder to write the site to, inside the app. | `out` |
| `--no-build` | Export the build already in `dist/`. | off: build first |

## netjes info

```text
netjes info
```

Checks the tools and the project, one line each: `✓` for what is in order, `!` for a warning, `✗` for a problem. Every `!` and `✗` line is followed by an indented line that says how to fix it. It exits 1 when there is a `✗`, and 0 otherwise.

```text
netjes 0.0.1 in /home/me/my-shop

✓ Node.js 24.15.0
✓ .NET SDK 10.0.403 (MyShop.csproj targets net10.0)

✓ npm packages: netjes 0.0.1, vite 8.3.5, react 19.3.1, react-dom 19.3.1
✓ MyShop.csproj references Netjes 0.0.1, the version of the npm package
✓ vite.config.ts uses netjes()
✓ package.json has a build script (dotnet publish runs it)
✓ app/ has 2 pages
! generated/netjes.ts is older than Features/Home/HomePage.cs
  Run `netjes generate` if the C# types changed; `netjes generate --check` tells for sure
```

| Check | Fails (`✗`) when | Warns (`!`) when |
|---|---|---|
| Node.js | outside `^22.22.2 \|\| ^24.15.0 \|\| >=26.0.0`: older than 22.22.2, or a 24 older than 24.15.0 | an odd release line (23, 25), which falls between the supported ones |
| .NET SDK | `dotnet` is missing, `dotnet --version` fails (often a `global.json` that asks for an SDK that is not installed), or the SDK is older than the app's `TargetFramework` (and 10.0): any .NET 10 SDK builds an app that targets `net10.0` | |
| npm packages | `netjes`, `vite`, `react` or `react-dom` is not installed, or a version is outside the installed netjes's peer range | the netjes running `info` is another version than the app's (run `npx netjes info`) |
| Netjes reference | the `.csproj` references an exact Netjes version (`x.y.z` or `[x.y.z]`) other than the npm package's, or a checkout that is not there. A range, a floating version or an MSBuild property such as `$(NetjesVersion)` is shown, not compared. | the `.csproj` references no Netjes package (fine when `Directory.Packages.props` does) |
| Vite config | there is no `vite.config.*`, or it does not use `netjes()` | |
| `package.json` | it is missing, is not valid JSON, or has no `build` script | |
| `app/` | it is missing; or `pages/` still has pages router files (named with their `app/` files) or API routes in `pages/api/` | it has no `page.tsx` |
| `generated/netjes.ts` | it is missing | a `.cs` file in the app is newer (a hint only: `netjes generate --check` is exact) |

## netjes agents

```text
netjes agents [dir] [--name <Name>] [--force]
```

Writes what `netjes new` gives coding agents into an existing app: `AGENTS.md` (how this app is built and tested, with links to the Netjes docs and `llms.txt`), a `CLAUDE.md` that imports it, and the Netjes skill in `.claude/skills/netjes/`. It prints each file as `wrote` or `kept`: a file that exists is kept unless `--force` is given. When it keeps a `CLAUDE.md` that does not import `AGENTS.md`, it says to add the `@AGENTS.md` line. When the folder has no `.csproj` (or more than one) and `--name` is not given, it warns on stderr that the app is named after the folder, and still writes the files. After upgrading Netjes, `rm -r .claude/skills/netjes && npx netjes agents` refreshes the skill and keeps the rest. The [coding agents guide](../guides/coding-agents.md) has more.

| Argument or option | What it does | Default |
|---|---|---|
| `[dir]` | The app folder. | the current folder |
| `--name <Name>` | The app's name in `AGENTS.md`. | the name of its `.csproj`, else the folder name |
| `--force` | Replace the files that exist. | off: keep them |

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success. |
| 1 | A usage error; a check that failed before anything ran (no `.csproj`, packages not installed, the port is in use, a name or folder that `netjes new` refuses); or a failed check (`netjes info` found a `✗`, `netjes generate --check` found the types out of date). |
| 127 | `dotnet` or `npm` is not installed, or not on `PATH`. netjes says what to install. |
| 130 | Ctrl+C at the `netjes new` prompt. |
| other | The exit code of the process netjes ran (`dotnet`, `npm` or `vite`), passed on unchanged; 128 + n when signal n ended it (130 for SIGINT, 143 for SIGTERM). |

## Output

- Results go to stdout: help, the version, the route table or its JSON, the `info` report, what `new` created. Errors and hints go to stderr.
- A reader that stops early, as in `netjes routes | head`, is not an error: netjes drops the rest of its output and exits as it would have.
- `dotnet`, `npm` and `vite` write to the terminal directly, with their own formatting. Under `routes`, the app's stdout goes to stderr, so that stdout holds only the report.
- In command mode (`generate`, `routes`, `export`) the host's own lifetime messages ("Now listening on" a throwaway port, "Press Ctrl+C to shut down") are left out; the app's other logs are not.
- Colour only where it helps: the marks of `netjes info` and the label of an error, when the stream is a terminal. See `NO_COLOR` and `FORCE_COLOR` below.
- netjes asks a question in one case only: `netjes new` without a folder, in a terminal. Everything else takes its input from arguments, so it runs the same in scripts and CI.

## Environment variables

| Variable | Effect |
|---|---|
| `NO_COLOR` | Any non-empty value turns colour off ([no-color.org](https://no-color.org)). It wins over `FORCE_COLOR`. |
| `FORCE_COLOR` | Colour even when the output is not a terminal; `0` or `false` turns it off. |
| `TERM=dumb` | No colour. |
| `ASPNETCORE_URLS` | Where the app listens. `netjes dev` sets `http://localhost:5080` unless it is set; `start` leaves it to ASP.NET Core; command mode uses `http://127.0.0.1:0`. |
| `ASPNETCORE_ENVIRONMENT` | `dev`, `generate`, `routes` and the type step of `build` run in `Development`, `start` and `export` in `Production`, unless it is set. |
| `Logging__LogLevel__Microsoft.Hosting.Lifetime` | In command mode netjes sets it to `Warning`, unless it is set. |
| `NETJES_COMMAND`, `NETJES_OUTPUT`, `NETJES_CHECK` | Set by netjes to put the app in command mode. netjes removes inherited ones (in any letter case) from every process it starts, so a leftover one cannot turn `netjes start` into an export. The app reads them only from its process environment. |
| `NETJES_VITE_HOST`, `NETJES_VITE_ORIGIN` | Passed on to the app's sidecar: the Vite dev server's host (localhost unless set; `0.0.0.0` exposes it, see [the dev server](vite-plugin.md#the-dev-server)) and origin. `netjes dev` checks the Vite port on that host. |
| `Netjes__*` | ASP.NET Core configuration for `NetjesOptions`, passed on, such as `Netjes__ViteDevPort=5174`. |

## In CI

```bash
npm ci
npx netjes generate --check                # the committed types match the C# code
npm test
dotnet publish -c Release -o bin/publish   # runs npm run build, and ships dist/ with the app
```

To build the client in a step of its own (`npx netjes build`), pass `-p:NetjesSkipClientBuild=true` to `dotnet publish`, as the template's `Dockerfile` does, so that it is not built twice.

`netjes info` makes a quick first step in a CI job, too: it fails, with a reason, on a missing or old SDK or Node.js and on packages that do not match.
