# HelpCenter: an example page package

A help center that an app mounts at a prefix: a page package, in two halves of one version that live in this folder. The [Blog](../Blog/README.md) mounts it at `/help` and develops it, and [TaskBoard](../TaskBoard/README.md) mounts it at `/(marketing)/help` and replaces one of its pages. Neither half is published; `scripts/check-packages.sh` packs both and installs them into a new app, as a package from a registry would be.

| Half | Name | What it holds |
|---|---|---|
| npm | `@netjes-examples/help-center` ([package.json](package.json)) | The pages, layouts and boundaries in `app/`, components in `src/`, the design tokens in `styles/help-center.css`, and the package's own generated file, `generated/netjes.ts` |
| NuGet | `Netjes.Examples.HelpCenter` ([HelpCenter.csproj](HelpCenter.csproj)) | The descriptor (`HelpCenterPackage.cs`) and `MapHelpCenter` (`HelpCenterEndpoints.cs`), which mounts the pages and maps their loaders, a page action, the server action `help.vote` and a JSON endpoint through the mount |

## Mounting it

```csharp
builder.Services.AddHelpCenter();
// ...
app.MapHelpCenter("/help");   // before app.MapPages()
app.MapPages();
```

```ts
// vite.config.ts
export default defineConfig({ plugins: [netjes({ packages: ['@netjes-examples/help-center'] })] });
```

The prefix is in folder syntax, so it can hold a route group or a value: `/help`, `/(marketing)/help`, `/[tenant]/help`. The package's files merge into the app's folders under it, so its `layout.tsx` wraps the app's own pages in that folder too, and its `(center)` group wraps an app page in `app/help/(center)/`.

## The pages

| File | URL below the mount | C# |
|---|---|---|
| `app/layout.tsx` | every page | none: a band with the package's navigation, inside the app's layouts. It draws no page shell of its own. |
| `app/page.tsx` | `/` | `MapPageLoader("/")`: the articles |
| `app/(center)/layout.tsx` | the `(center)` group | `MapLayoutLoader("app/(center)")`: the list of articles beside each page |
| `app/(center)/articles/[slug]/page.tsx` | `/articles/[slug]` | `MapPageLoader`, `Page.NotFound()` for an unknown article, and `MapPageAction` for a note, with and without JavaScript; the "Was this helpful?" buttons call `help.vote` |
| `app/contact/page.tsx` | `/contact` | `MapPageLoader("/contact")`; its form posts JSON to `MapPost("/messages")`, which takes a message of up to 2000 characters |
| `app/not-found.tsx`, `app/error.tsx` | an unknown URL below the mount, and an error in a page | none |

Readers post notes and messages without signing in, so the in-memory store (`HelpCenterStore.cs`, which stands in for a database) keeps the newest 200 of each, and counts every one.

The pages link with the package's own `Link`, `Form` and `useHref` from `generated/netjes.ts`, which put the mount, and the app's base path, in front of each href: `<Link href="/contact">` goes to `/help/contact` in the Blog. A redirect from C# goes through `PagePackageMount.Url`, which does the same on the server.

## Developing it

- `generated/netjes.ts` is written by the Blog, which mounts the package with `.WriteTypes("../HelpCenter/generated/netjes.ts")`: start the Blog in Development, or run `npx netjes generate` in `examples/Blog`. `npx netjes generate --check` there fails when it is out of date. It is keyed below the mount, so it is the same wherever an app mounts the package. Never edit it by hand.
- `npm run typecheck` checks the package under `packages/netjes/tsconfig.strict-app.json` and `noPropertyAccessFromIndexSignature`: an app's own `tsc` never sees a package's pages, so the package checks them under the strictest flags an app may set. The root `npm run typecheck` runs it.
- The two halves have one version, the repository's: `version` in `package.json`, the `<Version>` the project takes from `Directory.Build.props`, and the peer range `^<version>` on `netjes`. An app that runs two versions of the halves, or a Netjes outside the range, stops at startup. `node scripts/release.mjs check` and `VersionTests` check them, and a release changes all three ([RELEASING.md](../../RELEASING.md)).
- The pages use Tailwind classes made from the package's tokens (`bg-help-accent`, `text-help-muted`). The Vite plugin imports `styles/help-center.css` into the app's Tailwind build and adds `app` and `src` as sources; an app restyles the package by setting the `--help-*` variables in its own stylesheet.
