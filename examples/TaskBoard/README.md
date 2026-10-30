# TaskBoard

A small multi-tenant project tracker (think Linear-lite) built on Netjes's app router: React pages, ASP.NET Core and Postgres behind them. It is the example to read when you want to see how a real app is put together: authentication, tenancy, forms that work without JavaScript, live data, streaming.

The [Blog](../Blog) is the smaller example; the [Netjes docs](../../docs/README.md) explain each feature it uses.

## Run it

```bash
cp .env.example .env
docker compose up                      # dev: dotnet watch + Vite HMR on http://localhost:8081, Postgres included
```

Or outside Docker. From the repository root, once, and again after changing `packages/netjes`:

```bash
npm ci && npm run build
```

Then, in `examples/TaskBoard` and with the `.env` copied as above: `docker compose up -d db` (Postgres on 127.0.0.1:5433, matching `appsettings.Development.json`), then `dotnet run` (http://localhost:5081). Development migrates and seeds a demo account: **demo@taskboard.test / taskboard-demo**. `appsettings.Development.json` also limits `AllowedHosts` to `localhost`, `127.0.0.1` and `[::1]`, and the dev compose image is named `taskboard-app-dev` so the production stack never reuses it. The production services use `pull_policy: build`, so `up -d` rebuilds after a `git pull`.

Production:

```bash
docker compose -f compose.yaml -f compose.prod.yaml up -d   # db, then migrate (one-off), then the app behind Caddy on :443
```

The app is only reachable through Caddy, which terminates TLS for `TASKBOARD_SITE` in `.env`: a domain whose DNS points at the server gets a Let's Encrypt certificate (ports 80 and 443 must be open), and the default `localhost` gets one from Caddy's own CA, which browsers warn about. Plain http is redirected to https. The app publishes no port of its own in production; [Behind the proxy](#behind-the-proxy) says why. Caddy binds ports 80 and 443 on the host, which needs rootful Docker; with rootless Docker or Podman, lower `net.ipv4.ip_unprivileged_port_start` to 80 first, and see the note on client addresses under [Rate limits](#rate-limits).

Migrations never run on app start in production. The `migrate` service runs `dotnet TaskBoard.dll migrate` once and the app waits for it to finish (`service_completed_successfully`). To migrate by hand: `docker compose run --rm migrate`. Set a real `POSTGRES_PASSWORD` in `.env` (gitignored).

## What is where

| Feature | Files |
|---|---|
| Sign up, sign in, sign out: server actions with DataAnnotations validation, hashed passwords (`PasswordHasher<T>`), local-only `returnUrl`, no-JS fallback, rate limits; a sign-in on the landing page that stays there (`stay`), which the router refreshes for the signed-in user | `Features/Auth/AuthEndpoints.cs`, `Features/Auth/AuthLimits.cs`, `app/(marketing)/login`, `register`, `app/(marketing)/page.tsx` |
| The signed-in shell: a layout loader for the sidebar's projects, `.RequireAuthorization()` for every page below it | `Features/Shell/AppShell.cs`, `app/(app)/layout.tsx` |
| Signed-out visitors: 401 renders `unauthorized.tsx`, which sends them to the login page and back | `Program.cs` (cookie events), `app/unauthorized.tsx` |
| Projects: list, create with `useOptimistic` and `.Revalidate()` | `Features/Projects/ProjectsEndpoints.cs`, `app/(app)/projects/page.tsx` |
| Tenancy: the project layout's loader returns `Page.Forbidden()` for non-members and `Page.NotFound()` for unknown projects; they render `projects/forbidden.tsx` and `projects/not-found.tsx`, a level up, outside the project layout | `Features/Board/ProjectLayout.cs`, `app/(app)/projects/[id]/layout.tsx`, `app/(app)/projects/forbidden.tsx` |
| Live stats: a JSON API polled by TanStack Query, seeded by the layout loader with `.WithQuery`, invalidated by task actions with `.Invalidate` | `ProjectLayout.cs`, `lib/project.ts` |
| The board: tasks by state, filters in search params (`useSearchParams(schema)`), the Done column paged, add and move as server actions with `.RevalidatePath` | `Features/Board/BoardEndpoints.cs`, `app/(app)/projects/[id]/page.tsx`, `components/board.tsx` |
| A task: one edit form (assignees must be members), comments with `useOptimistic`, the activity feed streamed with `Page.Defer` and read with `deferred()` from `netjes/zod` | `Features/Tasks/TaskEndpoints.cs`, `app/(app)/projects/[id]/tasks/[taskId]/page.tsx`, `components/task-page.tsx` |
| Members: owners add people by email | `Features/Members/MembersEndpoints.cs`, `app/(app)/projects/[id]/settings/page.tsx` |
| Titles: `export const metadata` with a template in the root layout, `.WithMetadata` from C# | `app/layout.tsx`, every loader |
| Loading and error states inside their layouts | `app/(app)/projects/[id]/loading.tsx`, `app/(app)/error.tsx`, `app/error.tsx` |
| Data: EF Core, snake_case names, `IEntityTypeConfiguration` per entity, the `InitialSchema` migration | `Data/` |
| Types from C#: page and layout props (`pages[...]`, `layouts[...]`), shared props, the stats API (`api[...]`), server actions (`serverActions[...]`) and typed routes (`route()`, `<Link href>`), all generated from the C# records | `generated/netjes.ts`, `Program.cs` (`UseSharedProps`, the enum converter) |

## Things worth knowing

- **Page loaders run before layout loaders.** The project layout refuses non-members, but each page loader under it checks access too (`db.AccessAsync`), so no data is even read for them. Server actions are separate endpoints: each one checks membership itself.
- **Form inputs are records with `init` properties.** ASP.NET Core's form binding refuses a positional record with 400 when any constructor field is missing from the post, while `init` properties keep their defaults and let `[Required]` answer with a field error.
- **`Page.Defer` work gets its own `DbContext`.** It runs while the request's context keeps serving the layout loaders, and a `DbContext` must not be used concurrently. `AddDbContextFactory` registers both the factory and the per-request context.
- **One set of types.** The pages use the zod schemas Netjes generates from the C# records; nothing is written twice. Enums are C# enums (`TaskState`, `TaskPriority`, `MemberRole`) written as camelCase strings by one `JsonStringEnumConverter`, so they arrive as `z.enum([...])`. Only client input, such as the board's search-param filters, has hand-written schemas. The app rewrites `generated/netjes.ts` when it starts in Development; CI runs `npx netjes generate --check`, which needs no database: `Program.cs` skips the development migration and seed when `app.GetNetjesCommand()` names a command.
- **Task numbers** come from `Project.NextTaskNumber`, bumped by one atomic `UPDATE ... RETURNING` in the same transaction as the task insert: people adding a task at once queue on the project row and get consecutive numbers.

## Security

### Behind the proxy

Outside Development the auth cookie is always `Secure`, so it never travels over plain http. That is why production runs behind Caddy: over plain http, browsers keep a `Secure` cookie only for `localhost`, so signing in would work nowhere else. The app also sends HSTS on https responses, and redirects http to https when it knows its https port (`ASPNETCORE_HTTPS_PORT`). Leave that unset in the compose stack: Caddy redirects first, and with it set the app would redirect its own healthcheck too (plain http from 127.0.0.1, which is not the proxy), so it would never report healthy and Caddy, which waits for it, would not start.

Caddy talks to the app over plain http on the `edge` network and passes the visitor's scheme and address in `X-Forwarded-Proto` and `X-Forwarded-For`. The app believes those headers only from the addresses in `ForwardedHeaders:KnownNetworks`: in compose, Caddy's fixed address on `edge` (`TASKBOARD_PROXY_ADDRESS`, in `TASKBOARD_EDGE_SUBNET`; change both if the subnet overlaps one of your networks). From anyone else they are ignored, loopback included, and with no networks configured the middleware is left out altogether. Do not set `ASPNETCORE_FORWARDEDHEADERS_ENABLED` instead: it makes ASP.NET Core believe every sender, so anyone who reaches the app directly could claim to be on https or to come from any address. Behind another proxy, put its address (or network, in CIDR) in `ForwardedHeaders__KnownNetworks__0`.

### Rate limits

Sign-in and sign-up answer 429, with `Retry-After`, past these limits (`AuthLimits` in configuration, e.g. `AuthLimits__SignInsPerAccount`; each must be at least 1, or the app does not start):

| Limit | Default |
|---|---|
| `SignInsPerAddress` | 20 sign-in attempts a minute per client address |
| `SignInsPerAccount` | 10 sign-in attempts in 15 minutes per email, right password or wrong |
| `SignUpsPerAddress` | 10 sign-ups an hour per client address |

The sign-in and sign-up forms show a 429 as an error under the password, with what was typed kept (`withTooManyAttempts` in `components/forms.tsx`); a post without JavaScript gets the error page.

The client address is the visitor's, from Caddy, and an IPv6 `/64` counts as one. The account limit counts every attempt before the password is checked, so parallel guesses cannot get past it, and counts unknown emails the same way, so a 429 does not tell anyone whether an account exists. The cost: anyone can use up an account's attempts, and its owner then waits up to 15 minutes. The limits live in memory, per process; with several instances, each counts on its own.

The per-address limits are only as good as the address the app sees. If it sees one address for every visitor, they become limits for everyone together, and one client can block all sign-ins with 20 posts a minute. That happens behind another proxy that is not in `ForwardedHeaders:KnownNetworks` (the app then sees the proxy's address), and under rootless Docker or Podman, whose port forwarding can hand Caddy the gateway's address instead of the visitor's. So put the proxy in front of the app in `ForwardedHeaders:KnownNetworks`, and make sure that proxy sees the visitors' real addresses.

### Sign-up says when an email is taken

Sign-up creates the account and signs in at once, without confirming the email, so it has to say when an email already has an account ("There is already an account with this email"). Anyone can find out whether an address is registered that way. A generic answer would need a confirmation email: accept every sign-up, send a link, and tell an existing account holder by email instead. TaskBoard sends no email, so it keeps the plain message and limits sign-ups per address, which slows harvesting down. Adding a member tells a project owner, who is signed in, whether an email has an account in the same way.

### Signing out does not revoke a copied cookie

The auth cookie is a self-contained, encrypted ticket, valid for 14 days and renewed while in use. Signing out deletes the browser's copy, but a copy taken earlier (a separate compromise; the cookie is `HttpOnly`, so page scripts cannot read it) keeps working until it expires. For a deployment where that matters:

- Add a security stamp: a random value per user in the database, put in a claim at sign-in and checked in `CookieAuthenticationEvents.OnValidatePrincipal` (rejecting the principal when it no longer matches). Change it on sign-out, password change or role change, and every older cookie stops working. ASP.NET Core Identity's `SecurityStampValidator` does this, checking every `ValidationInterval` (30 minutes by default).
- Or keep tickets on the server with `CookieAuthenticationOptions.SessionStore` (an `ITicketStore`, for example backed by Postgres or Redis): the cookie then only holds a key, and sign-out removes the ticket.
- Shorten `ExpireTimeSpan` either way.

## Tests

```bash
dotnet test --project ../../tests/TaskBoard.Tests   # the real app against Postgres in Testcontainers (needs Docker)
npm test                                            # Vitest component tests with createTestApp
npm run typecheck                                   # TypeScript against the generated types
npx netjes generate --check                         # fails when generated/netjes.ts is out of date (no database needed)
npm run test:e2e                                    # Playwright, dev and production, on ports 8090/8091/8190
```

The integration tests talk to the app over https (the auth cookie is `Secure` outside Development), set the client address with a test header, and raise the per-address limits for the shared app, since every in-process request has the same (missing) address. `AuthLimitTests` runs apps of its own with low limits; `ProxyTests` covers which senders the forwarded headers are believed from. The e2e servers lift the per-address limits too: every test signs up from 127.0.0.1.

Some suites check what production depends on rather than one feature:

- `TenantIsolationTests` lists every loader and action of the app, and tries each one that takes a project or a task with another user's project, task and members ids: documents, client navigations (also with the owner's layout keys), HEAD, action calls and forms without JavaScript all get 401, 403 or 404, none of the project's data, and no change in the database. Its header says which other classes cover the sign-in, sign-up and projects loaders and actions, which take no project.
- `UserChangeTests` (and `e2e/user-change.spec.ts` in a real tab) signs out and in as someone else while the layouts stay mounted: the first user's layout keys reuse nothing. The spec also signs in from the landing page's form, which stays on the page and shows the user at once.
- `OutputCacheInstancesTests` runs two instances on one output-cache store and key ring, with a cached page (TaskBoard caches none): entries are shared, revalidation evicts for both, signed-in visitors are never served or stored, and a payload that reuses a layout is shared only with the same `SegmentKeySecret`, so neither instance serves one under a key the visitor never sent.
- `SidecarCrashTests` runs the built app in a process of its own (`npm run build` first), kills its sidecar while a page streams, and expects the response cut and the next render whole. It listens on a free port, or on `TASKBOARD_TEST_PORT`.
- `e2e/navigation.spec.ts` overtakes a slow navigation with a newer click or the back button.
- `e2e/no-js.spec.ts` posts every form of the app without JavaScript, in development and production: signing up, signing in from a page that needs it and landing back on it, starting a project, signing out (and Back after it), the board (adding a task and moving it), editing a task and commenting, and adding a member. Each post is answered with a redirect, or with the page again showing the errors and what was typed. The project pages render inside a loading boundary and the board is larger than React's 12.8 kB streaming chunk, so these tests also show that a boundary that is ready with the shell is written in place.

The end-to-end suite starts a throwaway Postgres container on 127.0.0.1:8432 (`e2e/db.mjs`; `E2E_DB_PORT` moves it) with a database for each mode, and removes it afterwards (`e2e/global-teardown.mjs`). The container is `taskboard-e2e-db`, or `taskboard-e2e-db-<port>` on another port, so runs on different ports leave each other alone. `E2E_DEV_PORT`, `E2E_PROD_PORT` and `E2E_VITE_PORT` move the app servers. The production run migrates with the `migrate` command first, like the compose stack.
