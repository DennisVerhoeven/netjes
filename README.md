![Netjes](assets/netjes-lockup.svg#gh-light-mode-only)
![Netjes](assets/netjes-lockup-on-ink.svg#gh-dark-mode-only)

# Netjes

[![CI](https://github.com/DennisVerhoeven/netjes/actions/workflows/ci.yml/badge.svg)](https://github.com/DennisVerhoeven/netjes/actions/workflows/ci.yml)

**Build React apps and sites on ASP.NET Core.** *Netjes* is Dutch for neat and tidy, said "NET-yes".

Netjes is a React framework for .NET teams. You write pages in `app/` with the Next.js App Router conventions, and the server side in C#: ASP.NET Core receives every request and owns routing, data loading, server actions, authorization and caching. A supervised pool of Node processes renders the React tree to HTML, the browser hydrates it, and later navigations fetch the next page's props as JSON from the same C# endpoints.

## Why Netjes

- **The server is ASP.NET Core.** Loaders and actions are minimal API handlers, with dependency injection, model binding, ASP.NET Core authentication and `RequireAuthorization()`, and the data access you already use. There is no second server to deploy or keep in step.
- **The pages are React.** Layouts, loading and error boundaries, client navigation, Suspense and streaming, with Vite, Tailwind CSS, TanStack Query and Vitest set up.
- **Types come from C#.** The C# records are the source: `generated/netjes.ts` holds zod schemas, typed server action references and typed routes, so a renamed property or a mistyped link is a type error.
- **Forms work without JavaScript.** A form that calls a C# server action posts as a plain HTML form when scripts are off, and comes back with its errors and what was typed.

## A small example

A page, its C# loader and a server action. `Program.cs` registers Netjes and maps the feature:

```csharp
builder.Services.AddNetjes();
builder.Services.AddSingleton<PostStore>();

var app = builder.Build();
app.UseNetjes();
app.UseAntiforgery(); // after authentication, if you have it

app.MapPostPages();
app.MapPages(); // every other page file, plus the 404 fallback
app.Run();
```

```csharp
// Features/Posts/PostPages.cs
using Microsoft.AspNetCore.Mvc; // for [FromForm]

public static class PostPages
{
    public sealed record PostProps(string Slug, string Title, string Body, int Likes);

    public sealed record LikeInput
    {
        public string Slug { get; init; } = "";
    }

    public static void MapPostPages(this IEndpointRouteBuilder app)
    {
        // The page's loader, in place of a server component: it binds like a minimal API handler.
        app.MapPageLoader("/blog/[slug]", (string slug, PostStore posts) =>
            posts.Find(slug) is { } post
                ? Page.Props(new PostProps(post.Slug, post.Title, post.Body, posts.LikesOf(slug)))
                    .WithMetadata(new PageMetadata { Title = post.Title })
                : Page.NotFound());

        // A server action: POST /_netjes/actions/posts.like, behind antiforgery and an origin check.
        app.MapAction("posts.like", ([FromForm] LikeInput input, PostStore posts) =>
        {
            if (posts.Find(input.Slug) is null)
            {
                return ServerAction.NotFound();
            }

            posts.Like(input.Slug);
            return ServerAction.Ok().Refresh(); // the page loads again, with the new count
        });
    }
}
```

```tsx
// app/blog/[slug]/page.tsx
import { Form, Link, definePage, useAction } from 'netjes';
import { pages, route, serverActions } from '../../../generated/netjes.ts';

// pages['/blog/[slug]'] is the zod schema of the C# PostProps record.
export default definePage(pages['/blog/[slug]'], function Post({ slug, title, body, likes }) {
  const [, like, pending] = useAction(serverActions['posts.like']);
  return (
    <article>
      <Link href={route('/blog')}>All posts</Link>
      <h1>{title}</h1>
      <p>{body}</p>
      {/* Without JavaScript this is a plain form post, and the page comes back with the new count. */}
      <Form action={like}>
        <input type="hidden" name="slug" value={slug} />
        <button disabled={pending}>Like ({likes})</button>
      </Form>
    </article>
  );
});
```

`app/blog/[slug]/page.tsx` is the route `/blog/{slug}`. ASP.NET Core matches it, runs the loader, and renders the page on the server; `<Link>` then navigates in the browser. The app rewrites `generated/netjes.ts` when it starts in development, so `pages['/blog/[slug]']` and `serverActions['posts.like']` follow the C# records.

## Quick start

You need the .NET 10 SDK and Node.js 22.22.2 or a later 22, 24.15.0 or a later 24, or 26 or newer (the odd releases 23 and 25 are not supported).

```bash
npx netjes new my-app
cd my-app
npm run dev          # http://localhost:5080
```

`netjes new` writes an ASP.NET Core app with a root layout, a page with a C# loader, a form calling a validated server action, component tests, Tailwind CSS, the generated types, and Docker files for development and production. Edit a page and the browser updates through Vite; edit the C# and `dotnet watch` applies it.

[Getting started](docs/getting-started.md) takes that app from the first page to a deployed container. If you know the Next.js App Router, [Coming from Next.js](docs/coming-from-nextjs.md) maps each of its concepts to the Netjes counterpart.

## Features

- **Routing** with the App Router file conventions: layouts, templates, `loading`, `error`, `not-found`, `forbidden` and `unauthorized` boundaries, route groups, dynamic, catch-all and optional catch-all segments, parallel and intercepting routes, a client router with prefetching and back and forward from memory, base paths and locale routing. [Routing](docs/guides/routing.md)
- **Data loading** in C#: page loaders, layout loaders that are skipped on navigations within their section, shared props for every page, public configuration read at runtime, and TanStack Query seeded from the loader so queries render on the server. [Data loading](docs/guides/data-loading.md)
- **Server actions and forms**: C# endpoints with typed references, DataAnnotations validation, `<Form>` with a fallback without JavaScript, antiforgery and same-origin checks, revalidation, uploads and downloads. [Actions and forms](docs/guides/actions-and-forms.md)
- **Rendering and streaming**: streamed documents, `Page.Defer` for slow values, `loading.tsx` and Suspense, complete documents for crawlers, `dynamic()` and `clientOnly()`, and in production a page the browser renders itself while no Node process is ready. [Rendering and streaming](docs/guides/rendering-and-streaming.md)
- **Caching**: output-cached pages with tag and path revalidation, from actions or from anywhere, draft mode that bypasses the cache. [Caching](docs/guides/caching.md)
- **Metadata**: titles with templates, descriptions, Open Graph, Twitter and robots tags from page files or from C#, a sitemap and `robots.txt` from the route table, and `<Script>`. [Metadata](docs/guides/metadata.md)
- **Images**: `<Image>` with an optimiser endpoint, configured widths and allow-listed remote hosts, and optional resizing and WebP with SkiaSharp. [Images](docs/guides/images.md)
- **Static export**: `netjes export` writes every page as a complete HTML file. [Static export](docs/guides/static-export.md)
- **Security defaults**: security headers on every document, an opt-in Content-Security-Policy with nonces or hashes, body limits for actions, safe redirects and a guarded image endpoint. [Security](docs/guides/security.md)
- **Testing**: Vitest set up by the Vite plugin, a harness that renders pages and components with a fake router and fake actions, and C# tests against the real app. [Testing](docs/guides/testing.md)
- **Deployment**: `dotnet publish` runs the client build and ships it with the app; Docker, health checks, OpenTelemetry traces and metrics, several instances behind a load balancer. [Deployment](docs/guides/deployment.md)
- **Coding agents**: new apps come with an `AGENTS.md` and a Netjes skill, and the docs are published as `llms.txt` and `llms-full.txt`. [Coding agents](docs/guides/coding-agents.md)

## Documentation

| | |
|---|---|
| Start | [Getting started](docs/getting-started.md), [Coming from Next.js](docs/coming-from-nextjs.md) |
| Guides | [Routing](docs/guides/routing.md), [Data loading](docs/guides/data-loading.md), [Actions and forms](docs/guides/actions-and-forms.md), [Rendering and streaming](docs/guides/rendering-and-streaming.md), [Caching](docs/guides/caching.md), [Metadata](docs/guides/metadata.md), [Images](docs/guides/images.md), [Static export](docs/guides/static-export.md), [Security](docs/guides/security.md), [Deployment](docs/guides/deployment.md), [Testing](docs/guides/testing.md), [Redirects, rewrites and headers](docs/guides/redirects-rewrites-headers.md), [Coding agents](docs/guides/coding-agents.md) |
| Reference | [React API](docs/reference/react-api.md), [C# API](docs/reference/csharp-api.md), [Options](docs/reference/options.md), [The netjes command](docs/reference/cli.md), [Vite plugin](docs/reference/vite-plugin.md) |
| How it works | [Architecture](docs/architecture.md), [Design decisions](docs/decisions/) |
| Project | [Roadmap](docs/roadmap.md), [Changelog](CHANGELOG.md), [Contributing](CONTRIBUTING.md), [Security policy](SECURITY.md) |

[docs/README.md](docs/README.md) lists every page with a line on what it covers.

Two example apps show the pieces together. The [Blog](examples/Blog) has a catch-all docs section, a guestbook that works without JavaScript, a JSON API used through TanStack Query, streaming, images and a static export. [TaskBoard](examples/TaskBoard) is a multi-tenant project tracker with cookie authentication, Postgres through EF Core, protected sections, optimistic updates, Testcontainers tests and a production Docker Compose stack behind a TLS proxy.

## Packages

| Package | What it holds |
|---|---|
| [`netjes`](packages/netjes) on npm | The Vite plugin, the client router and components, the sidecar that renders pages, the `netjes` command and the app template |
| [`Netjes`](src/Netjes) on NuGet | The ASP.NET Core library: routing from `app/`, loaders, server actions, rendering, caching, the image endpoint, type generation |
| [`Netjes.Images.SkiaSharp`](src/Netjes.Images.SkiaSharp) on NuGet | Optional image processing for `<Image>`: resizing and WebP |

The three are released together under one version. An app uses the same version of `netjes` and `Netjes`, which `netjes new` pins and `netjes info` checks.

## Requirements

- The .NET 10 SDK. Apps target `net10.0`.
- Node.js 22.22.2 or a later 22, 24.15.0 or a later 24, or 26 or newer (`^22.22.2 || ^24.15.0 || >=26.0.0`), at build time and next to the app in production: the server renders pages in Node processes it starts and supervises.
- React 19, Vite 7 or newer, and zod 4 for the generated schemas. `netjes new` installs them.
- Docker, only for the template's container setup.
- Linux, macOS or Windows. CI runs the .NET and JavaScript tests on all three (the ones that need Docker on Linux only), and the end-to-end suites and the package check on Linux.

## Status

Netjes is at 0.0.1, its first release. Until 1.0, a new version can change the API; the [changelog](CHANGELOG.md) says what changed and what to do about it.

Not there yet:

- Showing the target's `loading.tsx` at once on a client navigation. The router waits for the next page's payload, then shows it.
- Metadata files in `app/` (`icon.png`, `opengraph-image.tsx`, `manifest.json`) and generated Open Graph images.
- Client navigation in static exports: exported pages navigate with full page loads.
- Intercepting folders outside a `@slot`.

The [roadmap](docs/roadmap.md) has what comes next.

Left out by design:

- React Server Components. C# loaders do their job: data comes from C# as props, and every component ships to the browser ([ADR 0010](docs/decisions/0010-app-router-with-csharp-layout-loaders.md)).
- Serverless and edge hosts. Pages render in long-lived Node processes next to the .NET app ([ADR 0001](docs/decisions/0001-react-renders-in-node-sidecars.md)).

## Community

Questions and ideas go to [GitHub Discussions](https://github.com/DennisVerhoeven/netjes/discussions), bugs to the [issue tracker](https://github.com/DennisVerhoeven/netjes/issues). Read [CONTRIBUTING.md](CONTRIBUTING.md) before a pull request, and follow the [Code of Conduct](CODE_OF_CONDUCT.md). Report security problems privately, as [SECURITY.md](SECURITY.md) describes, not in the issue tracker.

## Licence

Netjes is released under the [MIT licence](LICENSE).
