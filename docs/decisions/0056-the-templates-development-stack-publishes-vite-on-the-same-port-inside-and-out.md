# 0056 – The template's development stack publishes Vite on the same port inside and out

Status: accepted

## Context

The template's development Compose file published Vite's port as `127.0.0.1:5173:5173`, so a second app on the same machine, or any program that already held 5173, could not start its stack, and the guides said Vite's port does not move. The obvious fix, `${VITE_PORT}:5173`, serves nothing: the page's module URLs are `http://localhost:<ViteDevPort>/...`, with the port the app was told (`Netjes:ViteDevPort`, 5173 by default), so the browser asks the host for a port that Vite is not listening on there.

Two smaller gaps sat in the same files. The development image set `ASPNETCORE_URLS=http://+:8080` beside the .NET images' own `ASPNETCORE_HTTP_PORTS=8080`, which `ASPNETCORE_URLS` takes precedence over, so the app's port was defined in two places that had to be kept equal. And ASP.NET Core's Data Protection keys lived in the container's filesystem, so each rebuild of the development image made the antiforgery tokens of pages that were still open invalid.

## Decision

- `docker-compose.override.yml` publishes `127.0.0.1:${VITE_PORT:-5173}:${VITE_PORT:-5173}` and sets `Netjes__ViteDevPort` to `${VITE_PORT:-5173}` for the `app` service: Vite listens on the port the host publishes, so the module URLs are right. `VITE_PORT` is read as `APP_PORT` is, from the shell or a `.env` file next to the Compose files.
- The development stage of the `Dockerfile` no longer sets `ASPNETCORE_URLS`: the app listens on the image's `ASPNETCORE_HTTP_PORTS`, 8080, as the production stage already does.
- The development Compose file keeps the Data Protection keys of the container's root user (`/root/.aspnet/DataProtection-Keys`) on a named volume, as the production file does for its user. `docker compose down -v` removes it.

## Alternatives considered

- **Map the host port to 5173 in the container.** The app would still tell the browser the host's port for the modules, where nothing listens on it inside: the port has to be one number in all three places, so the stack sets it once.
- **Leave `ASPNETCORE_URLS` and document that it must match.** It has no use the image's setting does not cover, and a second place for the port is how the two go out of step.
- **Leave the keys in the container.** A rebuild or `docker compose down` followed by `up` is a normal part of development, and a form that fails with an antiforgery error after it looks like a bug in the app.

## Consequences

- `VITE_PORT=5190 docker compose up` serves the app's modules from http://localhost:5190, and two development stacks can run side by side with a different `APP_PORT` and `VITE_PORT` each.
- An app made before this change keeps its own Compose files and Dockerfile, which still work. To move Vite's port, or keep the keys across rebuilds, copy the three changes into them (the CHANGELOG lists them); `ASPNETCORE_URLS` can stay.
- The development stack now has a named volume, which `docker compose down -v` removes with the rest.
