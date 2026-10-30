# __TITLE__

A React app on ASP.NET Core, built with Netjes: Vite, Tailwind CSS, TanStack Query, zod and Vitest are set up.

```bash
npm run dev        # dotnet watch + Vite HMR, http://localhost:5080
npm test           # Vitest component tests
npm run typecheck
npm run routes     # the route table, from C#
npx netjes build   # types, client and server bundles
npx netjes info    # check Node.js, the .NET SDK, the packages and this setup
dotnet publish -c Release
```

- `app/` holds the pages, as in the Next.js App Router: `page.tsx` is `/`, `about/page.tsx` is `/about`, `layout.tsx` wraps them all, `not-found.tsx` and `error.tsx` handle the rest.
- `public/` is served at the site root. `public/favicon.svg`, which the metadata in `app/layout.tsx` links, is the Netjes mark: replace it with the app's own.
- `Features/` holds the C# side, one folder per feature: `Features/Home/HomePage.cs` maps the home page's loader and the `home.greet` server action the form calls.
- `AGENTS.md` tells coding agents how the app is laid out and what the rules are; `CLAUDE.md` imports it for Claude Code.
- `Properties/launchSettings.json` holds the `http` launch profile that `dotnet run` and IDEs (F5) use: the app in Development on http://localhost:5080, as `npm run dev` runs it but without `dotnet watch`.
- `generated/netjes.ts` holds zod schemas for what the loaders and actions return, typed server action references and typed routes. The app rewrites it when it starts in Development; commit it, and run `npx netjes generate --check` in CI.

Outside Development, `Program.cs` sends HSTS on https responses and redirects http to https once the app knows its https port (`ASPNETCORE_HTTPS_PORT` or `ASPNETCORE_HTTPS_PORTS`). Behind a proxy that terminates TLS, add `UseForwardedHeaders()` (trusting only that proxy) so proxied requests count as https and get HSTS, and let the proxy redirect plain http. Netjes adds `X-Content-Type-Options`, `Referrer-Policy` and `X-Frame-Options: SAMEORIGIN` to every page; see `NetjesOptions.SecurityHeaders` to change that.

With Docker: `docker compose up` for development, `docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d` for production. In Development `appsettings.Development.json` limits `AllowedHosts` to loopback names (a DNS-rebinding guard); add a name there if you reach the dev app by another host. The Docker build restores Netjes from NuGet and npm, which is how any app made by `npx netjes new` references it. Only an app scaffolded from a Netjes checkout references the checkout instead; scaffold it with `netjes new --packages` to build it in Docker.
