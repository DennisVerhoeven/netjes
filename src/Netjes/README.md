# Netjes

The ASP.NET Core half of [Netjes](https://github.com/DennisVerhoeven/netjes), a React framework for building apps and sites on ASP.NET Core. Pages live in `app/` with the Next.js App Router conventions; this package turns them into endpoints, runs their C# loaders and server actions, renders them through a supervised pool of Node processes, and handles caching, images and the generated TypeScript types. The npm package [`netjes`](https://www.npmjs.com/package/netjes), released at the same version, is the other half: the Vite plugin, the client router and the renderer.

## Start an app

```bash
npx netjes new my-app
```

That writes an app with both halves set up. To add Netjes to a project you have, reference this package and install the npm half at the same version (0.0.1 here; the two are released together):

```bash
dotnet add package Netjes --version 0.0.1
npm install netjes@0.0.1 react react-dom zod @tanstack/react-query
```

```csharp
using Microsoft.AspNetCore.Mvc; // for [FromForm]

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNetjes();

var app = builder.Build();
app.UseNetjes();
app.UseAntiforgery(); // after authentication, if you have it

app.MapPageLoader("/", (TimeProvider time) => Page.Props(new HomeProps("Hello from C#", time.GetUtcNow())));
app.MapAction("home.greet", ([FromForm] GreetInput input) => ServerAction.Ok(new Greeting($"Hello, {input.Name}!")));
app.MapPages(); // every other page file, plus the 404 fallback

app.Run();

public sealed record HomeProps(string Message, DateTimeOffset RenderedAt);
public sealed record GreetInput { public string? Name { get; init; } }
public sealed record Greeting(string Text);
```

The package adds build targets to the project: `using Netjes` when implicit usings are on, `node_modules` and `dist` kept out of the build, and `dotnet publish` running `npm run build` and shipping `dist/`. The app needs the .NET 10 SDK and, at build time and in production, Node.js `^22.22.2 || ^24.15.0 || >=26.0.0` (Node.js 22.22.2 or a later 22, 24.15.0 or a later 24, or 26 or newer).

## Documentation

- [Getting started](https://github.com/DennisVerhoeven/netjes/blob/main/docs/getting-started.md)
- [All guides](https://github.com/DennisVerhoeven/netjes/blob/main/docs/README.md)
- [C# API](https://github.com/DennisVerhoeven/netjes/blob/main/docs/reference/csharp-api.md)
- [Options](https://github.com/DennisVerhoeven/netjes/blob/main/docs/reference/options.md)
- [Architecture](https://github.com/DennisVerhoeven/netjes/blob/main/docs/architecture.md)

Report bugs in the [issue tracker](https://github.com/DennisVerhoeven/netjes/issues), and security problems privately, as [SECURITY.md](https://github.com/DennisVerhoeven/netjes/blob/main/SECURITY.md) describes. MIT licensed.
