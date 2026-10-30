# 0006 – Types and zod schemas generated from the C# contracts

Status: accepted

## Context

Pages parse their props, shared props and API responses with zod. Written by hand, each C# record would exist twice: once in C#, and once as a zod schema that has to match how System.Text.Json writes it (camelCase names, nullable annotations, `[JsonIgnore]`, enum converters). Drift between the two shows up only at runtime, as an error page.

## Decision

The running app generates `generated/netjes.ts`.

- The generator reads the endpoints' metadata: the props type of each page loader's `PageResult<T>` and each layout loader's, the `Produces` and `Accepts` metadata of minimal APIs, the `[FromForm]` types of page actions, the inputs and results of server actions, and the type given to `UseSharedProps<T>`. It turns each type into zod through `JsonSerializerOptions.GetTypeInfo`, with the app's own `JsonOptions`, so the schema follows the JSON contract rather than the C# declaration.
- It also writes typed routes from the route table: each page's pattern with the parameters `route()` needs, and the `Href` type that `<Link>` and `router.push` accept.
- In Development, startup rewrites the file when its content has changed. `netjes generate` writes it, or with `--check` fails when it is stale, without starting Node ([ADR 0007](0007-a-node-cli-that-asks-the-app.md)).
- `Page.Props(x)` returns `PageResult<T>`, and the untyped results (`NotFound`, `Redirect`, `Forbidden` and the rest) convert to any `PageResult<T>`. A loader lambda's inferred return type therefore carries its props type without an annotation.

## Alternatives considered

- **A Roslyn source generator.** It runs at compile time, but sees neither the configured `JsonSerializerOptions` (naming policy, converters) nor the endpoint metadata, and cannot write files outside the compilation.
- **An OpenAPI document and an OpenAPI-to-zod tool.** It reuses `Microsoft.AspNetCore.OpenApi`, but adds dependencies on both sides. Page loaders are not API operations, and the OpenAPI schema drops details the contract keeps, such as getter nullability and discriminators.
- **Plain reflection over properties.** Simpler, but it diverges from the JSON wherever System.Text.Json applies a policy, an attribute or a converter.

## Consequences

- The generated schemas match the JSON by construction. Golden-file tests pin the output and parse real System.Text.Json output with it.
- Anonymous types lose their nullable annotations (their properties are generic type parameters), so their reference-typed properties are generated as nullable. Named records give exact schemas.
- Only endpoints mapped by the time the host starts are known. The file is as current as the last Development start or `netjes generate`, which CI checks with `--check`.
- A type that a custom converter writes becomes `z.unknown()`, since only the converter knows its JSON.
