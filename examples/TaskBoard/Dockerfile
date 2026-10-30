# syntax=docker/dockerfile:1
# Build context is the repository root: the example builds against packages/netjes and src/Netjes.
#   cd examples/TaskBoard && docker compose up                                   -> dev (dotnet watch + Vite HMR)
#   cd examples/TaskBoard && docker compose -f compose.yaml -f compose.prod.yaml up -d -> production
ARG NODE_IMAGE=node:24.22.1-bookworm-slim
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.403-noble
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.14-noble

FROM ${NODE_IMAGE} AS node

# npm dependencies for the whole workspace, cached until a package manifest changes.
FROM node AS deps
ENV PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1
WORKDIR /src
COPY package.json package-lock.json ./
COPY packages/netjes/package.json packages/netjes/
COPY examples/Blog/package.json examples/Blog/
COPY examples/TaskBoard/package.json examples/TaskBoard/
RUN npm ci

# Development: the source is bind-mounted by compose.override.yaml.
FROM ${SDK_IMAGE} AS dev
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s /usr/local/lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
 && ln -s /usr/local/lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx
WORKDIR /src
COPY --from=deps /src ./
ENV ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_USE_POLLING_FILE_WATCHER=1 \
    DOTNET_WATCH_SUPPRESS_BROWSER_REFRESH=1 \
    NETJES_VITE_HOST=0.0.0.0
EXPOSE 8080 5174
# The examples import netjes from its build, packages/netjes/dist, which lives in a volume of its own. It is built from
# the mounted sources as the container starts, then again on every change to them (tsc --watch, in the background).
# Its output is in the container's log: a type error there holds the rebuild back until it is fixed.
CMD ["sh", "-c", "npm run build --workspace=packages/netjes && { npm run build --workspace=packages/netjes -- --watch --preserveWatchOutput & exec dotnet watch --project examples/TaskBoard run --no-launch-profile; }"]

# Client bundle (dist/client) and server bundle + sidecar (dist/server).
FROM deps AS client
COPY packages/netjes packages/netjes
COPY examples/TaskBoard examples/TaskBoard
RUN npm run build --workspace=packages/netjes && npm run build --workspace=examples/TaskBoard

FROM ${SDK_IMAGE} AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/Netjes/Netjes.csproj src/Netjes/
COPY src/Netjes/build src/Netjes/build
COPY examples/TaskBoard/TaskBoard.csproj examples/TaskBoard/
RUN dotnet restore examples/TaskBoard/TaskBoard.csproj
COPY src/Netjes src/Netjes
COPY examples/TaskBoard examples/TaskBoard
COPY --from=client /src/examples/TaskBoard/dist examples/TaskBoard/dist
RUN dotnet publish examples/TaskBoard/TaskBoard.csproj -c Release -o /app --no-restore -p:NetjesSkipClientBuild=true

# Production: ASP.NET Core runtime plus the node binary for the SSR sidecar, as the non-root app user.
# `docker compose run migrate` (or the migrate service) runs `dotnet TaskBoard.dll migrate` from this image.
FROM ${RUNTIME_IMAGE} AS production
COPY --from=node /usr/local/bin/node /usr/local/bin/node
RUN mkdir -p /home/app/.aspnet/DataProtection-Keys && chown -R app:app /home/app/.aspnet
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=3 \
  CMD ["node", "-e", "fetch('http://127.0.0.1:8080/healthz').then(r => process.exit(r.ok ? 0 : 1), () => process.exit(1))"]
ENTRYPOINT ["dotnet", "TaskBoard.dll"]
