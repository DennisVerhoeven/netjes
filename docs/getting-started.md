# Getting started

This tutorial takes you from an empty folder to a Netjes app running in a container. You build a small notes app: a page, a C# loader that feeds it, a page per note, a server action with validation, a component test, a production build and a Docker image. It assumes you know some React and some C#; it explains the Netjes parts as they come up.

## Before you start

You need:

- the .NET 10 SDK or newer (`dotnet --version` shows 10 or higher);
- Node.js `^22.22.2 || ^24.15.0 || >=26.0.0`, that is Node.js 22.22.2 or a later 22, 24.15.0 or a later 24, or 26 or newer (`node --version`): the tools in the app, Vitest and jsdom among them, need these releases, and the odd releases 23 and 25 are not supported;
- Docker, for the last step only.

Netjes is tested in CI on Linux, macOS and Windows: the .NET and JavaScript tests on all three (the ones that need Docker on Linux only), and the end-to-end suites and the package check on Linux.

## Create the app

```bash
npx netjes new my-app
```

`netjes new` copies the app template into `my-app`, runs `npm install` in it, and prints where things are:

```text
Created MyApp in my-app (using the Netjes 0.0.1 packages)

  app/                 the pages: page.tsx is /, about/page.tsx is /about, layout.tsx wraps them
  components/          React components, each with a Vitest test
  Features/            the C# side, one folder per feature: loaders and server actions
  Program.cs           the ASP.NET Core app (MyApp.csproj)
  generated/netjes.ts  types and zod schemas from C#, rewritten by netjes generate; never edit it
  Dockerfile           and the docker-compose files: Docker for development and production

Next:
  cd my-app
  npm run dev    then open http://localhost:5080
```

The folder name gives the .NET project name and root namespace (`my-app` becomes `MyApp`); `--name Shop` picks another. The app references the `Netjes` NuGet package and the `netjes` npm package at the exact version that created it, since the two halves speak one protocol. [The netjes command](reference/cli.md#netjes-new) lists the options.

## What is in it

```text
my-app/
  app/
    layout.tsx               the root layout: the header, the navigation, the title template and the favicon
    page.tsx                 /, with props from a C# loader
    about/page.tsx           /about, a page without a loader
    not-found.tsx            unknown URLs, and Page.NotFound() from a loader
    error.tsx                errors below the root layout
  components/                Greeting.tsx and HelloForm.tsx, each with a *.test.tsx next to it
  Features/Home/HomePage.cs  the home page's loader and the home.greet server action
  Program.cs                 services and the request pipeline
  SiteProps.cs               the shared props every page and layout gets
  generated/netjes.ts        zod schemas, typed actions and routes, generated from the C# types
  styles/global.css          Tailwind CSS, imported by app/layout.tsx
  public/favicon.svg         the favicon, the Netjes mark until you put your own there
  MyApp.csproj, package.json, vite.config.ts, tsconfig.json
  appsettings.json, appsettings.Development.json
  Properties/launchSettings.json   the launch profile for dotnet run and IDEs
  Dockerfile, docker-compose.yml, docker-compose.override.yml, docker-compose.prod.yml
  AGENTS.md, CLAUDE.md, .claude/skills/netjes/   guides for coding agents
```

`Program.cs` is short. Every request goes through ASP.NET Core:

```csharp
using MyApp;
using MyApp.Features.Home;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNetjes(options =>
    // Available in every page and layout through useSharedProps(sharedProps); typed in generated/netjes.ts.
    options.UseSharedProps(_ => ValueTask.FromResult(new SiteProps("My App"))));
builder.Services.AddHealthChecks().AddNetjes();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseNetjes();
app.UseAntiforgery(); // after authentication, if you add it

app.MapHealthChecks("/healthz");
app.MapHomePage();
app.MapPages(); // every other page file, plus the 404 fallback

app.Run();
```

`UseNetjes()` serves static files, routes requests to pages and renders errors. Each feature maps its loaders and actions (`MapHomePage()`), and `MapPages()` comes last: it maps every page file that has no loader of its own, and the not-found page for everything else.

## Run the dev server

```bash
cd my-app
npm run dev
```

`npm run dev` runs `netjes dev`, which starts `dotnet watch` in the Development environment. The app starts a Node process that runs the Vite dev server, and renders pages through it. Open http://localhost:5080.

Two edits to try:

- In `components/Greeting.tsx`, change the button's text. The browser updates in place, keeping the click count: Vite's hot module replacement.
- In `Features/Home/HomePage.cs`, change `"Hello from C#"`. `dotnet watch` applies the change to the running app; reload the page to see it.

Hot reload applies changes inside methods. Code that runs once at startup, such as `Program.cs` and the `Map...` calls, needs a restart: press Ctrl+R in the terminal that runs `npm run dev`. Ctrl+C stops it. The app listens on another port with `ASPNETCORE_URLS=http://localhost:3000 npm run dev`. The Vite dev server has a port of its own, 5173: when another app's Vite dev server has it, `npm run dev` says so, and `Netjes__ViteDevPort=5174 npm run dev` moves it.

When the Vite dev server cannot start, the app says why in one log entry and stops, and `dotnet watch` waits: an error in `vite.config.ts` comes with Vite's code frame, and a package it imports that is not installed with the advice to run `npm install`. Fix it, then press Ctrl+R.

In Development, `appsettings.Development.json` limits `AllowedHosts` to `localhost`, `127.0.0.1` and `[::1]`: a request for any other host name gets a 400, so a hostile web page cannot point its own domain at your machine and read the dev app (DNS rebinding). Add a name there if you reach the dev server by another one.

### Debugging

To step through the C# code, start the app from your IDE, such as Visual Studio or Rider: it runs the launch profile in `Properties/launchSettings.json`, as `dotnet run` does, which starts the app in Development on http://localhost:5080 with its Node process and the Vite dev server, but without `dotnet watch`. Stop `npm run dev` first, since both use the same ports.

To keep `npm run dev` running, attach the debugger to the app's own process instead. `dotnet watch` builds the app and starts it as the executable named after the project: `MyApp`, or `MyApp.exe` on Windows. That process runs your C# code; the `dotnet` processes above it belong to `dotnet watch`, and the `node` process below it renders the pages. While a debugger is attached, .NET does not apply hot reload updates to that process, so C# edits reach it only when you restart with Ctrl+R; edits to the pages still update the browser as usual. Each restart (Ctrl+R, or an edit hot reload cannot apply) starts a new process, which you attach to again. The pages' own code runs in the browser too, where the browser's developer tools debug it.

## Add a page

Pages are React components in `app/`, named after the Next.js App Router conventions: `app/notes/page.tsx` is `/notes`. Create it:

```tsx
// app/notes/page.tsx
import type { Metadata } from 'netjes';

export const metadata: Metadata = { title: 'Notes' };

export default function Notes() {
  return (
    <section className="space-y-6">
      <h1 className="text-3xl font-bold tracking-tight">Notes</h1>
      <p className="text-slate-600">Nothing here yet.</p>
    </section>
  );
}
```

Open http://localhost:5080/notes. ASP.NET Core routes a new page file at once; the tab title is "Notes · My App", from the title template in `app/layout.tsx`. A page without a C# loader renders with empty props.

Links between pages are typed: `<Link href>` accepts the URLs of the app's pages, listed in `generated/netjes.ts`. The dev server rewrites that file when it starts; while it runs, update it yourself after adding a page:

```bash
npx netjes generate
```

Then add the page to the navigation in `app/layout.tsx`, next to the other two links:

```tsx
<Link href="/notes" className={navLink}>
  Notes
</Link>
```

`<Link>` navigates in the browser: it fetches the next page's props from the server as JSON, swaps the page in and keeps the layout mounted. [Routing](guides/routing.md) covers the other file conventions: layouts, loading and error boundaries, route groups, catch-all segments, parallel and intercepting routes.

## Load data in C#

The page's data comes from a C# loader. Notes need somewhere to live; for this tutorial, memory will do. Create `Features/Notes/NoteStore.cs`:

```csharp
namespace MyApp.Features.Notes;

public sealed record Note(int Id, string Text, DateTimeOffset CreatedAt);

/// <summary>Notes kept in memory, until the app stops. A real app would use a database here.</summary>
public sealed class NoteStore(TimeProvider time)
{
    private readonly Lock _lock = new();
    private readonly List<Note> _notes = [];

    public IReadOnlyList<Note> All()
    {
        lock (_lock)
        {
            return [.. _notes.OrderByDescending(note => note.Id)];
        }
    }

    public Note? Find(int id)
    {
        lock (_lock)
        {
            return _notes.Find(note => note.Id == id);
        }
    }

    public Note Add(string text)
    {
        lock (_lock)
        {
            var note = new Note(_notes.Count + 1, text, time.GetUtcNow());
            _notes.Add(note);
            return note;
        }
    }
}
```

Then the loader, in `Features/Notes/NotesPages.cs`:

```csharp
namespace MyApp.Features.Notes;

public static class NotesPages
{
    public sealed record NotesProps(IReadOnlyList<Note> Notes);

    public static void MapNotesPages(this IEndpointRouteBuilder app)
    {
        app.MapPageLoader("/notes", (NoteStore notes) => Page.Props(new NotesProps(notes.All())));
    }
}
```

`MapPageLoader` maps the page at `/notes` with this delegate as its loader. It is a minimal API handler: its parameters bind from route values, the query string and services, and it can carry `.RequireAuthorization()` or any other endpoint convention. `Page.Props(...)` returns a `PageResult<NotesProps>`, which names the props type for the generated schema.

Register the store and map the feature in `Program.cs`:

```csharp
using MyApp;
using MyApp.Features.Home;
using MyApp.Features.Notes;

// ...
builder.Services.AddHealthChecks().AddNetjes();
builder.Services.AddSingleton<NoteStore>();

// ...
app.MapHomePage();
app.MapNotesPages();
app.MapPages(); // every other page file, plus the 404 fallback
```

That is startup code, so press Ctrl+R to restart. On start, the app rewrites `generated/netjes.ts` with schemas for the new records:

```ts
export const Note = z.object({
  id: z.number().int(),
  text: z.string(),
  createdAt: z.iso.datetime({ offset: true, local: true }),
});
export type Note = z.infer<typeof Note>;
export const NotesProps = z.object({
  notes: z.array(Note),
});
export type NotesProps = z.infer<typeof NotesProps>;

export const pages = {
  "/": HomeProps,
  "/about": z.object({}),
  "/notes": NotesProps,
} as const;
```

The schemas follow the JSON the app sends: camelCase names, `DateTimeOffset` as an ISO date string, nullability as declared. Now make the page read its props:

```tsx
// app/notes/page.tsx
import { Link, type Metadata, definePage } from 'netjes';
import { pages, route } from '../../generated/netjes.ts';

export const metadata: Metadata = { title: 'Notes' };

export default definePage(pages['/notes'], function Notes({ notes }) {
  return (
    <section className="space-y-6">
      <h1 className="text-3xl font-bold tracking-tight">Notes</h1>
      {notes.length === 0 ? (
        <p className="text-slate-600">Nothing here yet.</p>
      ) : (
        <ul className="space-y-2">
          {notes.map((note) => (
            <li key={note.id}>
              <Link href={route('/notes/[id]', { id: String(note.id) })} className="underline">
                {note.text}
              </Link>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
});
```

`definePage` parses the props with the schema before the component sees them, and types them: `notes` is a `Note[]`. The `route('/notes/[id]', ...)` call is for the next step; it is a type error until that page exists.

If you change a C# type that reaches the browser while the dev server runs, hot reload applies the change, and the generated file stays as it was until the next start. Run `npx netjes generate`, or restart. [Data loading](guides/data-loading.md) covers layout loaders, shared props, public configuration and seeding TanStack Query from a loader.

## A page per note

Square brackets make a dynamic segment. Add a loader for `/notes/[id]` to `MapNotesPages`, and a props record next to `NotesProps`:

```csharp
public sealed record NoteProps(Note Note);

// in MapNotesPages:
app.MapPageLoader("/notes/[id]", (int id, NoteStore notes) =>
    notes.Find(id) is { } note
        ? Page.Props(new NoteProps(note)).WithMetadata(new PageMetadata { Title = $"Note {note.Id}" })
        : Page.NotFound());
```

The `id` parameter binds the `[id]` route value by name, as in any minimal API. `Page.NotFound()` renders `app/not-found.tsx` with status 404, and `.WithMetadata(...)` sets the page's title from C#, which the layout's template completes. A value that is not a number, such as `/notes/abc`, does not bind, so it gets a 400 and the error page.

Create `app/notes/[id]/page.tsx`:

```tsx
import { Link, definePage } from 'netjes';
import { pages, route } from '../../../generated/netjes.ts';

export default definePage(pages['/notes/[id]'], function NotePage({ note }) {
  return (
    <article className="space-y-4">
      <Link href={route('/notes')} className="underline">
        All notes
      </Link>
      <p className="text-xl">{note.text}</p>
      <p className="text-slate-500">
        Written <time dateTime={note.createdAt}>{note.createdAt}</time>
      </p>
    </article>
  );
});
```

Restart with Ctrl+R, since `MapNotesPages` runs at startup. `route('/notes/[id]', { id })` builds the URL with the value encoded, and a missing or misspelt parameter is a type error.

## A server action with validation

Notes are added by a server action: a C# endpoint that React calls like a function, and that a plain form posts to when JavaScript is off. Add the input record and the action to `NotesPages.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace MyApp.Features.Notes;

public static class NotesPages
{
    public sealed record NotesProps(IReadOnlyList<Note> Notes);

    public sealed record NoteProps(Note Note);

    public sealed record NewNote
    {
        [Required(ErrorMessage = "Write something first."), StringLength(200, ErrorMessage = "Keep it under 200 characters.")]
        public string? Text { get; init; }
    }

    public static void MapNotesPages(this IEndpointRouteBuilder app)
    {
        app.MapPageLoader("/notes", (NoteStore notes) => Page.Props(new NotesProps(notes.All())));

        app.MapPageLoader("/notes/[id]", (int id, NoteStore notes) =>
            notes.Find(id) is { } note
                ? Page.Props(new NoteProps(note)).WithMetadata(new PageMetadata { Title = $"Note {note.Id}" })
                : Page.NotFound());

        // Called from components/NewNoteForm.tsx as serverActions['notes.add'].
        app.MapAction("notes.add", ([FromForm] NewNote input, NoteStore notes) =>
        {
            notes.Add(input.Text!.Trim());
            return ServerAction.Ok().Refresh();
        });
    }
}
```

`MapAction("notes.add", ...)` maps `POST /_netjes/actions/notes.add`. It requires an antiforgery token, which `<Form>` adds, and a request from the app's own origin. Before the delegate runs, Netjes checks the DataAnnotations on its input; when they fail, the action answers 422 with the errors per field, and the delegate never runs. The input has `init` properties rather than constructor parameters, so a field the post leaves out, such as an unchecked checkbox, keeps its default instead of making form binding refuse the post ([Validation](guides/actions-and-forms.md#validation)). `.Refresh()` tells the browser to load the page on screen again, so the new note shows up.

The form, in `components/NewNoteForm.tsx`:

```tsx
import { Form, useAction } from 'netjes';
import { serverActions } from '../generated/netjes.ts';

export function NewNoteForm() {
  const [state, formAction, pending] = useAction(serverActions['notes.add']);
  const error = state?.ok === false ? state.errors.text?.[0] : undefined;
  return (
    <Form action={formAction} className="space-y-2">
      <label className="block space-y-1">
        <span className="font-medium">New note</span>
        <input
          name="text"
          defaultValue={state?.ok === false ? String(state.values?.text ?? '') : ''}
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? 'text-error' : undefined}
          className="block w-full rounded-lg border border-slate-300 px-3 py-2 aria-invalid:border-red-600"
        />
      </label>
      {error && (
        <p id="text-error" className="text-sm text-red-700">
          {error}
        </p>
      )}
      <button type="submit" disabled={pending} className="rounded-full bg-slate-900 px-4 py-2 font-medium text-white">
        {pending ? 'Saving…' : 'Add note'}
      </button>
    </Form>
  );
}
```

Put `<NewNoteForm />` on the notes page, under the heading, with `import { NewNoteForm } from '../../components/NewNoteForm.tsx';`. Restart (the action is mapped at startup), and the generated file gains a typed reference:

```ts
export const serverActions = {
  "home.greet": serverAction<FormData, Greeting>("home.greet", { result: Greeting }),
  "notes.add": serverAction<FormData, void>("notes.add"),
} as const;
```

`useAction` wraps React's `useActionState`: `state` is `null` before the first post, `{ ok: true, result }` after a success, and `{ ok: false, errors, values }` after a failure. React resets a form after its action runs, so `values` hands back what was typed, for the `defaultValue`. The error keys are the JSON names of the input's properties (`text`).

Now turn JavaScript off in your browser's developer tools and add a note again. `<Form>` renders a real `<form method="post">` with the antiforgery token in a hidden field. A valid post redirects back to `/notes` (303); an invalid one renders the page again in the same request, with status 422, the error under the field and the text still in it. [Actions and forms](guides/actions-and-forms.md) has the rest: JSON actions, page actions, redirects, revalidation, uploads, and what a failed post hands back.

## Test it

The template sets up Vitest with jsdom and Testing Library, and `netjes/testing` renders components inside a fake app: a router that records navigations, and server actions answered by your test. Create `components/NewNoteForm.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createTestApp } from 'netjes/testing';
import { expect, it } from 'vitest';
import { NewNoteForm } from './NewNoteForm.tsx';

it('shows the error from C# next to the field, and keeps what was typed', async () => {
  const app = createTestApp({
    actions: {
      'notes.add': () => ({ ok: false, status: 422, errors: { text: ['Keep it under 200 characters.'] } }),
    },
  });
  render(<NewNoteForm />, { wrapper: app.Wrapper });

  await userEvent.type(screen.getByLabelText('New note'), 'Far too long');
  await userEvent.click(screen.getByRole('button', { name: 'Add note' }));

  expect(await screen.findByText('Keep it under 200 characters.')).toBeDefined();
  expect(screen.getByLabelText<HTMLInputElement>('New note').value).toBe('Far too long');
});
```

Run the checks the template's `AGENTS.md` lists before you call a change done:

```bash
npm test                      # Vitest
npm run typecheck             # strict TypeScript, against the generated types
dotnet build                  # C#, with warnings as errors
npx netjes generate --check   # fails when generated/netjes.ts is out of date
npm run routes                # the route table, and warnings about likely mistakes
```

`netjes generate --check` belongs in CI: it runs the app once without serving anything or starting Node, and fails when the committed file no longer matches the C# types. [Testing](guides/testing.md) covers page tests, C# tests of loaders and actions against the real app, and Playwright.

## Build and run in production

```bash
npx netjes build     # generated/netjes.ts, then dist/client and dist/server
npx netjes start     # the app in Production, on http://localhost:5000
```

`netjes build` regenerates the types and runs `vite build` and `vite build --ssr`: `dist/client/` holds the browser bundles, and `dist/server/` the server bundle and the script of the Node process that renders it. `netjes start` runs `dotnet run -c Release` in the Production environment. In production the app starts its Node process with the built bundle; there is no Vite dev server. The notes are in memory, so they are gone after a restart.

To deploy, publish the app:

```bash
dotnet publish -c Release -o bin/publish
cd bin/publish && dotnet MyApp.dll
```

The build leaves `bin/`, `obj/` and a `publish/` folder in the project out of the app's content, so publishing into any of them is safe.

`dotnet publish` runs `npm run build` and copies `dist/` into the output, through the build targets the `Netjes` package adds. It does not regenerate `generated/netjes.ts`, which is why CI runs `netjes generate --check`. The machine that runs the app needs Node.js as well as the ASP.NET Core runtime.

Outside Development the template sends HSTS on https responses, and redirects http to https once the app knows its https port (`ASPNETCORE_HTTPS_PORT`); until then it logs that it cannot determine the port. Behind a proxy that terminates TLS, add `UseForwardedHeaders()`, trusting only that proxy, before `UseNetjes()`.

## Run it in Docker

The template's `Dockerfile` has a development target and a production target, and three Compose files use them:

```bash
docker compose up                                                     # development: dotnet watch and Vite, http://localhost:8080
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d # production, on port 8080
```

Development bind-mounts the source, so edits reload as they do outside Docker, and binds ports 8080 and 5173 to localhost only. The production image builds the client in one stage and publishes the app in another, then runs on the ASP.NET Core runtime image with a Node binary next to it, as a non-root user. The production Compose file makes the filesystem read-only, drops capabilities, limits CPU and memory, restarts the container, checks `/healthz`, and keeps ASP.NET Core's Data Protection keys, which sign antiforgery tokens, in a named volume. ASP.NET Core logs a warning that those keys are not encrypted at rest; configure one of its `ProtectKeysWith...` options to encrypt them.

The image restores `Netjes` from NuGet and `netjes` from npm, so it needs the `package-lock.json` that `npm install` wrote. [Deployment](guides/deployment.md) covers configuration, health checks, telemetry, proxies and running several instances.

## Add Netjes to an existing app

To add Netjes to an ASP.NET Core project you already have:

1. Reference the NuGet package: `dotnet add package Netjes --version 0.0.1`. The package adds build targets to the project: `using Netjes` when `ImplicitUsings` is on, `node_modules` and `dist` kept out of the build, and `dotnet publish` running `npm run build` and shipping `dist/`.
2. Next to the project file, add a `package.json` with `"type": "module"` and the scripts `"dev": "netjes dev"`, `"build": "vite build && vite build --ssr"`, `"test": "vitest run"`, `"typecheck": "tsc -p tsconfig.json --noEmit"`, `"generate": "netjes generate"` and `"routes": "netjes routes"`. Install the npm half at the same version as the NuGet package (`netjes@0.0.1` with `Netjes` 0.0.1), and the tools the template builds, tests and type-checks with:

   ```bash
   npm install netjes@0.0.1 react react-dom zod @tanstack/react-query
   npm install -D vite typescript @types/react @types/react-dom tailwindcss @tailwindcss/vite \
     vitest jsdom @testing-library/react @testing-library/dom @testing-library/user-event
   ```
3. Add a `vite.config.ts`:

   ```ts
   import netjes from 'netjes/vite';
   import { defineConfig } from 'vite';

   export default defineConfig({ plugins: [netjes()] });
   ```
4. Add a `tsconfig.json`, the one `netjes new` writes. It type-checks `app`, `components`, `generated` and `vite.config.ts`:

   ```json
   {
     "compilerOptions": {
       "target": "ES2022",
       "module": "ESNext",
       "moduleResolution": "bundler",
       "lib": ["ES2023", "DOM", "DOM.Iterable"],
       "types": ["vite/client"],
       "jsx": "react-jsx",
       "strict": true,
       "noUncheckedIndexedAccess": true,
       "noImplicitOverride": true,
       "isolatedModules": true,
       "verbatimModuleSyntax": true,
       "allowImportingTsExtensions": true,
       "skipLibCheck": true,
       "noEmit": true
     },
     "include": ["app", "components", "generated", "vite.config.ts"]
   }
   ```

   `netjes` ships JavaScript with declaration files, so your `tsc` checks your own code alone, also under stricter options you add, such as `exactOptionalPropertyTypes` or `noPropertyAccessFromIndexSignature` (see [TypeScript setup](reference/vite-plugin.md#typescript-setup)).
5. Put pages in `app/`, with an `app/layout.tsx` that imports a stylesheet containing `@import "tailwindcss";`.
6. In `Program.cs`, call `builder.Services.AddNetjes()`, then `app.UseNetjes()`, `app.UseAntiforgery()` (after authentication) and `app.MapPages()`.
7. Run `npx netjes generate`, which writes `generated/netjes.ts` from the C# types; the app rewrites it each time it starts in Development. Commit it.
8. Start the dev server with `npm run dev` (the `netjes dev` script from step 2), or `npx netjes dev`. It runs the app with `dotnet watch` and Vite hot reloading, on `http://localhost:5080` unless `ASPNETCORE_URLS` says otherwise.

`npx netjes info` checks the result: Node.js and the SDK, the installed packages and their versions, the Vite config and the `app/` folder. [Vite plugin](reference/vite-plugin.md) lists the plugin's options, including how to turn Tailwind CSS, the query client or the Vitest defaults off.

## Next

- [Coming from Next.js](coming-from-nextjs.md), if you know the App Router.
- The [guides](README.md#guides), one per topic.
- The [Blog](../examples/Blog) and [TaskBoard](../examples/TaskBoard) examples, for a docs section, a guestbook, authentication, Postgres and streaming in working apps.
