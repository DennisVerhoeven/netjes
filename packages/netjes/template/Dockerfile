# syntax=docker/dockerfile:1
#   docker compose up                                                    -> dev (dotnet watch + Vite HMR)
#   docker compose -f docker-compose.yml -f docker-compose.prod.yml up   -> production image
# Builds against the published Netjes NuGet and npm packages, as any app made by `npx netjes new` does. Only an app
# scaffolded from a Netjes checkout references the checkout, which this build cannot see: scaffold it with --packages.
ARG NODE_IMAGE=node:24.22.1-bookworm-slim
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.403-noble
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.14-noble

FROM ${NODE_IMAGE} AS node

# npm dependencies, cached until package.json or the lockfile changes.
FROM node AS deps
WORKDIR /src
COPY package.json package-lock.json ./
RUN npm ci

# Development: the source is bind-mounted by docker-compose.override.yml.
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
EXPOSE 8080 5173
CMD ["dotnet", "watch", "run", "--no-launch-profile"]

# Client bundle (dist/client) and server bundle + sidecar (dist/server).
FROM deps AS client
COPY . .
RUN npm run build

FROM ${SDK_IMAGE} AS build
WORKDIR /src
COPY __NAME__.csproj ./
RUN dotnet restore
COPY . .
COPY --from=client /src/dist dist
RUN dotnet publish -c Release -o /app --no-restore -p:NetjesSkipClientBuild=true

# Production: ASP.NET Core runtime plus the node binary for the SSR sidecar. Runs as the non-root app user.
FROM ${RUNTIME_IMAGE} AS production
COPY --from=node /usr/local/bin/node /usr/local/bin/node
# Where ASP.NET Core keeps its Data Protection keys; docker-compose.prod.yml mounts a volume here.
RUN mkdir -p /home/app/.aspnet/DataProtection-Keys && chown -R app:app /home/app/.aspnet
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=3 \
  CMD ["node", "-e", "fetch('http://127.0.0.1:8080/healthz').then(r => process.exit(r.ok ? 0 : 1), () => process.exit(1))"]
ENTRYPOINT ["dotnet", "__NAME__.dll"]
