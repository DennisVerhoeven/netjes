# 0009 – Public configuration is sent at runtime

Status: accepted

## Context

Browser code needs some settings that differ per environment: an API origin, a feature flag, an analytics key. Next.js inlines `NEXT_PUBLIC_*` variables into the bundle at build time, and Vite does the same with `VITE_*`. A Netjes app, though, is a .NET app: it is built once, shipped as one image, and configured per environment through ASP.NET Core configuration (appsettings, environment variables, a secret store).

## Decision

The `Netjes:PublicConfig` configuration section is sent to the browser with the first document, and components read it with `usePublicConfig(schema)`. Keys follow the app's JSON naming policy (camelCase by default). Values are strings, as configuration values are, so schemas coerce them (`z.coerce.number()`, `z.stringbool()`). Only that section is sent, and everything in it is public by definition.

## Alternatives considered

- **Vite's `VITE_*` build-time variables.** Apps can use them, but a rebuild per environment contradicts building one image, and a secret put there by mistake ends up in every bundle.
- **Sending it with every payload.** Configuration does not change between navigations, so the first document is enough.
- **An options object in C# (`options.PublicConfig = new { ... }`).** Less flexible than configuration, with no environment overrides without code. It could be added on top of the section.

## Consequences

- Changing a public setting is a configuration change and a restart, not a build.
- Values are strings; typed values come from the schema.
- The first document grows by the size of the section.
