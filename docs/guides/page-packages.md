# Page packages

This guide is for app developers who mount a page package, and for the people who write one. A page package brings pages, layouts, boundaries, loaders, actions and endpoints for an app to mount at a prefix, as a Razor class library or a Rails engine does for its framework. It comes in two halves of one version: an npm package with the page files, components and styles, and a NuGet package with their C# side. An app mounts it with one C# call and one entry in `vite.config.ts`, and its pages then live under the prefix as if they were the app's own, inside the app's layouts, built by the app's Vite build and routed by ASP.NET Core.

The [help center](../../examples/HelpCenter/README.md) in this repository is one: the [Blog](../../examples/Blog/README.md) mounts it at `/help`, and [TaskBoard](../../examples/TaskBoard/README.md) in its marketing group, with one of its pages replaced by its own. The examples below use its names. Why page packages work this way is in [ADR 0049](../decisions/0049-page-packages-are-mounted-into-the-route-table.md) (the route table), [ADR 0050](../decisions/0050-a-page-package-ships-sources-that-the-app-builds.md) (the build), [ADR 0051](../decisions/0051-a-page-package-has-a-generated-file-of-its-own.md) (the generated files and links) and [ADR 0052](../decisions/0052-a-page-package-whose-versions-do-not-fit-stops-the-app.md) (versions).

## Mounting a package

Install both halves, at one version:

```bash
npm install @acme/help-center@1.2.0
dotnet add package Acme.HelpCenter --version 1.2.0
```

Mount the C# half with the extension the package provides, before `app.MapPages()`:

```csharp
// Program.cs
builder.Services.AddNetjes();
builder.Services.AddHelpCenter();     // the package's services, if it has any

var app = builder.Build();
app.UseNetjes();
app.UseAntiforgery();

app.MapHelpCenter("/help");           // the package's pages, loaders, actions and endpoints under /help
app.MapPostPages();                   // the app's own features
app.MapPages();                       // the app's page files, and every mounted package's
```

And list the npm half in the Vite plugin's options, so the app's build compiles its pages and its styles:

```ts
// vite.config.ts
import netjes from 'netjes/vite';
import { defineConfig } from 'vite';

export default defineConfig({ plugins: [netjes({ packages: ['@acme/help-center'] })] });
```

The package's `app/contact/page.tsx` now answers at `/help/contact`, inside the app's root layout. In development its pages come from `node_modules`, and a page added to a package that a workspace links in is routed without a restart; in production they come from the client build, which writes a record of each listed package next to Vite's manifest (`dist/client/.vite/netjes.json`). A package that is listed but not mounted is a route warning in production, `package-unmounted`. One that is mounted but not listed stops the app in production, and in development its pages fail to render, with a message that names the fix.

The two halves are checked against each other, and against Netjes, when the app starts. Each of these stops the app with one line:

```text
'@acme/help-center' is mounted but not installed: npm install @acme/help-center@1.2.0
'@acme/help-center' is mounted but not in the client build: add it to netjes({ packages }) in vite.config.ts and build again
@acme/help-center is 1.2.0 on npm and Acme.HelpCenter is 1.1.0 on NuGet: install one version of both
@acme/help-center 1.2.0 supports Netjes ^0.0.4; this app runs Netjes 0.0.5
```

The Vite plugin checks the last one too, against the installed `netjes`, and stops the config on it, as it does for a listed package that is not installed or is no page package. `npx netjes info` lists the page packages the app depends on and checks their ranges and the NuGet half's version in the project file ([the netjes command](../reference/cli.md#netjes-info)). `npm run routes` adds a Package column to the route table once something is mounted.

## The prefix

The prefix is written in folder syntax, as a folder of `app/` would be named, and may hold route groups and values:

| Prefix | The package's `contact/page.tsx` | Inside |
|---|---|---|
| `/help` | `/help/contact` | the app's root layout |
| `/(marketing)/help` | `/help/contact` | `app/(marketing)/layout.tsx` too |
| `/support/(desk)/help` | `/support/help/contact` | the app's layouts in `app/support/` and `app/support/(desk)/` |
| `/[tenant]/help` | `/acme/help/contact` | the app's layouts in `app/[tenant]/` |
| `/` | `/contact` | the app's root layout |

A value in the prefix is a route value like any other: the app's layout loader of `app/[tenant]/layout.tsx` binds `tenant` for the package's pages, the package's loaders can bind it, and `params` holds it. A catch-all, an `@slot`, a `_private` folder, an intercepting folder, `.`, `..`, `%`, `\` and an empty segment are refused, and so are a value whose name one of the package's pages uses again and a folder that differs from one of the app's only in case (`/Help` where the app has `app/help/`).

The app's generated file routes the package's pages at their URLs, with the prefix's values first, so the app links to them as to its own:

```tsx
import { Link } from 'netjes';
import { route } from '../generated/netjes.ts';

<Link href={route('/help/articles/[slug]', { slug: 'privacy' })}>Privacy</Link>
<Link href={route('/[tenant]/help', { tenant })}>Help</Link>   // a package mounted at /[tenant]/help
```

## How the folders merge

The package's folder of pages goes into the app's `app/` under the prefix's folders, and the two trees merge folder by folder, route groups included. So a package's `layout.tsx` at the mount wraps the app's own pages in that folder as well, and a route group of the package wraps the app's pages in a folder of that group:

```text
app/                                      the app
  layout.tsx                              wraps everything, the package's pages included
  help/(center)/shortcuts/page.tsx        /help/shortcuts, inside both of the package's layouts
  help/faq/page.tsx                       /help/faq, inside the package's layout.tsx only

@acme/help-center/app/                    the package, mounted at /help
  layout.tsx                              wraps every page in app/help/
  (center)/layout.tsx                     wraps every page in app/help/(center)/
  (center)/articles/[slug]/page.tsx       /help/articles/privacy
  contact/page.tsx                        /help/contact
```

TaskBoard's shortcuts page sits in the help center's `(center)` group in just this way. A package's group names and its convention files are therefore part of what it promises under semver: a release that renames a group moves the app's pages out of its layout.

Files that would clash stop the app, naming both:

- an app page of the same shape as a package's page, such as `app/help/contact/page.tsx` next to the package's `contact/page.tsx`, stops `MapPages()`, unless the app replaces the package's page ([below](#replacing-a-page)). The message says so: `or replace the package's page with .Override("/help/contact") on its mount`;
- two packages' pages of one shape stop the second mount: mount one of them at another prefix;
- two `layout`, `template`, `loading`, `not-found` or other segment files for one folder, one of the app's and one of the package's, stop the mount. Only pages can be replaced;
- a package's `global-error.tsx`, `route.ts`, and an intercepting folder that climbs above the mount are refused, in the package.

The `overlap` route warning names a literal page next to a parameter page when one is the app's and the other the package's: the app's `app/help/[section]/page.tsx` next to the package's `settings/page.tsx` at `/help/settings` is warned, both ways round, since a package update that adds a page would otherwise take a URL from the app without a word ([Route warnings](routing.md#two-pages-that-match-the-same-urls)).

## Replacing a page

`.Override(pattern)` on the mount renders the app's page in place of the package's, at the URL the app links to:

```csharp
app.MapHelpCenter("/(marketing)/help")
    .Override("/help/contact");     // renders app/(marketing)/help/contact/page.tsx
```

The app's page stays in the folder of the package's page, inside every folder whose files wrap that page, and names its parameters as the package's does. It renders with the package's loader, page action and conventions, so it reads the package's props schema from the package's generated file:

```tsx
// app/(marketing)/help/contact/page.tsx
import { pages } from '@acme/help-center/generated';
import { definePage } from 'netjes';

export default definePage(pages['/contact'], function Contact({ email, hours }) {
  return <p>Write to <a href={`mailto:${email}`}>{email}</a>, {hours}.</p>;
});
```

The pattern is matched by shape, literals in any case and one trailing slash left out, so `/HELP/Contact/` names the same page. It throws when the package has no page there, when the app has none, when the two name their parameters otherwise, and for a page of another package. The app maps no loader or page action of its own for a replaced page: `MapPageLoader` and `MapPageAction` throw for it, and `.Override` throws for a page the app mapped one for first. A layout, a boundary or any other file of the package cannot be replaced.

## Protecting a mounted package

Conventions on the mount, such as `RequireAuthorization()`, apply to the package's own endpoints: its page files, its page loaders, page actions and server actions, and the endpoints it maps through its mount. They do not reach the app's own pages under the prefix, nor the package's layout loaders, which are no endpoints and keep their own conventions:

```csharp
app.MapHelpCenter("/help").RequireAuthorization();   // the package's pages and endpoints; not app/help/faq/page.tsx
app.MapPages().RequireAuthorization();               // every file page without a loader: the app's and, after the mount's, the package's
```

The app's pages under the prefix keep the protection they had: their own loader's conventions, `MapPages()`'s, and those of the layout loaders above them, which cover every page below their layout, from either source. So an app layout loader above the mount protects the package's pages too:

```csharp
app.MapLayoutLoader("/members", (ClaimsPrincipal user) => Page.Props(new MembersProps(user.Identity!.Name!)))
    .RequireAuthorization();
app.MapHelpCenter("/members/help");   // the package's pages need a signed-in user, as the app's below app/members/ do
```

A layout loader that a package maps through its mount covers the app's pages in its folder in the same way: had the help center's loader of `app/(center)` carried `RequireAuthorization()`, TaskBoard's shortcuts page would need a signed-in user too.

So an app page in one of the package's folders renders inside the package's layouts, and runs their loaders, for every visitor that page lets in, also when the mount requires a signed-in user: TaskBoard's shortcuts page would show the help center's list of articles to anyone under `app.MapHelpCenter("/(marketing)/help").RequireAuthorization()`. A package whose layout loaders send what not every visitor may see puts `RequireAuthorization()` on them itself, which protects the app's pages in their folders as well.

Where a page refuses a visitor whom the mount's conventions refuse too, those conventions decide how much of the package the visitor sees: the page's 401 or 403 stops at the package's first folder on the way, whose boundary renders without the package's layout, as at a layout loader that refuses the visitor, so no layout loader of the package runs for them.

## 404 and error pages under a mount

A package brings its own `not-found.tsx` and `error.tsx`. An unknown URL below the mount, such as `/help/nope`, renders the package's `not-found.tsx` inside its layout and the app's layouts above the mount, with status 404, where an app without packages renders the root `not-found.tsx` for every unknown URL. So does `Page.NotFound()` from an app page whose folders do not reach the mount, such as a root `app/[[...slug]]/page.tsx`, unless that page's own nearest not-found is at the mount's URL or below it. A page inside the mount's folder renders its own nearest not-found, as every page does.

The URL is below the mount when its path starts with the prefix's URL, literals in any case: `/HELP/nope` is, and `/helpx/nope` and `/help%2Fnope` are not. With a mount inside another, the deepest one takes the URL. A mount at the root URL leaves unknown URLs to the app's own `not-found.tsx`, and renders its own only in an app that has none.

Before anything on the way loads, each layout loader there, and each folder of the package under its mount's conventions, is authorized. A visitor one of them refuses gets the nearest not-found above it, with status 404, and the loaders from there down never run for them.

## The app's root layout

A package that draws a whole page around its own pages, with a header, navigation and footer, as a back-office package does, should not render inside the app's site header. Keep the app's root layout to what every page needs, and put the site's chrome in a route group layout of its own:

```text
app/
  layout.tsx                  providers and the skip link only
  (site)/layout.tsx           the site's header and footer
  (site)/page.tsx             /
  (site)/blog/[slug]/page.tsx /blog/hello
```

```csharp
app.MapBackOffice("/back-office");   // outside (site): inside the bare root layout only
```

The URLs do not change, since a route group adds no URL segment, and the client router moves between the two groups without loading a new document. A package that draws its own shell says so (`PagePackage.DrawsItsOwnShell`), and the `package-shell` route warning names an app whose root layout has no route group layout directly below it, such as `app/(site)/layout.tsx` ([C# API](../reference/csharp-api.md#pagepackage)). A package that draws no shell, as the help center does, renders inside the app's chrome and needs nothing of this.

Keep the "Skip to content" link first in the root layout. Its target, `<main id="content">`, moves into the layouts that draw the chrome: the site's group layout, and the shell of a package that draws one, which gives its main element that id.

## Export, sitemap and Content-Security-Policy

A package's pages are exported, and listed in the sitemap, by the rules for the app's own pages. A page action, a page that needs a signed-in user or a dynamic page without static parameters stops `netjes export`, so an app that exports its site and mounts such a package leaves the package's pages out on the mount, as the Blog does:

```csharp
app.MapHelpCenter("/help").ExcludeFromExport();   // .ExcludeFromSitemap() leaves them out of sitemap.xml
```

Both conventions cover the package's pages, not the app's pages under the prefix ([Static export](static-export.md#what-cannot-be-exported)). A package's pages render under the app's Content-Security-Policy and add no inline script of their own. A page that streams cannot work under a policy without a nonce ([Hash-based](security.md#hash-based)), so a package whose pages stream says so (`PagePackage.RequiresNonce`), and the `package-nonce` route warning names an app whose `NetjesOptions.ContentSecurityPolicy` refuses inline scripts without a `{nonce}`.

## Updating a package

Install both halves at the new version, then run `npx netjes generate`: the app's generated file routes the package's pages, so a release that adds or renames a page changes it, and `netjes generate --check` fails until it is written again. Apps that skip one half stop at startup with the version line above.

A release can also add a page of a shape the app has, or a segment file in a folder where the app has one. The app then stops at startup, naming both files: replace the package's page with `.Override`, or move the app's.

## Writing a page package

A page package is easiest to develop inside an app that mounts it, as the help center is developed in the Blog: an npm workspace links the npm half into the app's `node_modules`, a project reference brings the C# half, and the app writes the package's generated file.

### The npm half

```json
{
  "name": "@acme/help-center",
  "version": "1.2.0",
  "type": "module",
  "files": ["app", "src", "styles", "generated"],
  "exports": { ".": "./src/index.ts", "./generated": "./generated/netjes.ts" },
  "peerDependencies": { "netjes": "^0.0.4", "react": "^19.3.1", "zod": "^4.6.7", "@tanstack/react-query": "^5.105.0" },
  "netjes": { "pages": "app", "sources": ["app", "src"], "css": "styles/help-center.css", "nuget": "Acme.HelpCenter" }
}
```

The `netjes` field marks a page package, and `peerDependencies.netjes` names the Netjes versions it supports, which the app checks against the Netjes it runs. A package's code imports `netjes`, React, zod and TanStack Query as peers, so the app's one copy of each serves both.

| Field | Default | What it is |
|---|---|---|
| `pages` | `app` | The folder of the package's page, layout and boundary files, under the conventions of `app/`. |
| `sources` | `pages`, and `src` when the package has it | The folders that hold its Tailwind classes. |
| `css` | none | A stylesheet that the app's Tailwind build imports: the package's tokens. |
| `nuget` | none | The NuGet package of its C# half, which `netjes info` and the version check name. |

Every path is relative and stays inside the package. The package ships its pages and components as TypeScript, and its styles as Tailwind classes, which the app's build compiles ([Vite plugin](../reference/vite-plugin.md#page-packages)). It ships no `public/` folder: an image or a font is an import, which Vite bundles.

### Pages and links

A package's pages read their props through its own generated file, and link with the components it exports, which put the mount in front of every href:

```tsx
// app/(center)/layout.tsx
import { defineLayout } from 'netjes';
import { layouts, Link, route } from '../../generated/netjes.ts';

export default defineLayout(layouts['app/(center)'], function CenterLayout({ articles, children }) {
  return (
    <div>
      <nav aria-label="Articles">
        {articles.map((a) => <Link key={a.slug} href={route('/articles/[slug]', { slug: a.slug })}>{a.title}</Link>)}
      </nav>
      {children}
    </div>
  );
});
```

The hrefs are below the mount, as if the package were mounted at the root: `route('/articles/[slug]', ...)` is `/articles/privacy`, and the file's `Link` makes it `/help/articles/privacy` where the app mounted the package at `/help`, `/acme/help/articles/privacy` under `/[tenant]/help`, and puts the app's base path in front of that. Each page payload carries the mounts' URLs for the request (`mounts`), and the components read them from the page being rendered, so two renders at once, of two tenants, each link below their own. `Form`, `SubmitButton` and `useRouter` do the same, `usePathname` gives the path below the mount, and `useHref` gives a URL below it for a plain `<a>` or a `fetch`:

```tsx
import { endpointUrl, useHref } from '../generated/netjes.ts';

const url = useHref(endpointUrl('POST /messages'));   // /help/messages, under the base path
```

A mounted href is a path below the mount or a link within the page (`?page=2`, `#top`). Another host, a scheme and a `..` segment throw, since they would leave the mount. To link to the app's pages, or to another site, use the core `Link` from `netjes` with an `Href` the package's C# side sends: such a URL is the app's, the mount in it. On a page outside a mount whose prefix has a value, a component of the package that links below the mount throws, since there is no tenant to link below ([`mounted`](../reference/react-api.md#mounted)).

### Styles

The pages use Tailwind classes made from the package's own tokens, CSS variables with a prefix of its own, which its `css` file defines and maps into Tailwind's theme:

```css
/* styles/help-center.css */
:root {
  --help-accent: #0f6f6a;
  --help-ink: #13302e;
}

@theme inline {
  --color-help-accent: var(--help-accent);
  --color-help-ink: var(--help-ink);
}
```

The Vite plugin imports that file into the app's stylesheet, after its `@import "tailwindcss"`, and adds each of the `sources` folders as an `@source`, so the app's Tailwind build makes the package's classes (`bg-help-accent`). The app's Tailwind version and theme apply; an app restyles the package by setting the variables in its own stylesheet, as TaskBoard does. The app's build needs Tailwind on: `tailwind: false` with `packages` stops the config. CSS a package writes by hand shares the app's stylesheet, so scope it under the package's own root element.

### The C# half

A class library with `<FrameworkReference Include="Microsoft.AspNetCore.App" />` and a reference to the `Netjes` package holds a descriptor and the extension the app calls:

```csharp
public static class HelpCenterPackage
{
    public static PagePackage Package { get; } = new("@acme/help-center", typeof(HelpCenterPackage).Assembly);
}

public static class HelpCenterEndpoints
{
    public static PagePackageBuilder MapHelpCenter(this IEndpointRouteBuilder app, string prefix = "/help")
    {
        var help = app.MapPagePackage(HelpCenterPackage.Package, prefix);
        var mount = help.Mount;

        help.MapPageLoader("/", (HelpCenterStore store) => Page.Props(new HomeProps(store.Links())));
        help.MapLayoutLoader("app/(center)", (HelpCenterStore store) => Page.Props(new CenterProps(store.Links())));
        help.MapPageAction("/articles/[slug]", (string slug, [FromForm] FeedbackForm form, HelpCenterStore store, HttpContext http) =>
        {
            store.AddFeedback(slug, form.Comment);
            return Page.Redirect(mount.Url($"/articles/{Uri.EscapeDataString(slug)}?feedback=sent", http.Request.RouteValues));
        });
        help.MapAction("help.vote", (VoteInput input, HelpCenterStore store) => ServerAction.Ok(store.Vote(input.Slug, input.Helpful)));
        help.MapPost("/messages", (MessageRequest request, HelpCenterStore store) => TypedResults.Ok(store.AddMessage(request)));

        return help;
    }
}
```

`PagePackage`'s version is the assembly's informational version, without its `+` suffix, which must be the npm half's version. The builder that `MapPagePackage` returns maps below the mount:

- `MapPageLoader` and `MapPageAction` take a pattern below the mount (`/` is the mount's own page) and accept only the package's own pages;
- `MapLayoutLoader` takes the layout's folder in the package (`app/(center)`), or its URL below the mount, and accepts only the package's own layouts;
- `MapAction` maps at `/_netjes/actions/{name}`, as on the app: server actions share one namespace with the app's and every other package's, so start their names with the package's (`help.vote`). Names that differ only in case count as one, and a second one stops the app;
- `MapGet`, `MapPost` and the other minimal API calls map below the mount's URL: `/help/messages`;
- the prefix's values bind like the page's own: a loader of a package mounted at `/[tenant]/help` can take `string tenant`.

The first four throw on a `MapGroup` made from the mount, and the app's own `MapPageLoader`, `MapPageAction` and `MapLayoutLoader` throw for the package's pages and layouts, naming the mount. Return the builder, so the app can add its conventions and overrides.

A URL the C# side writes, for a redirect, an email or a cookie's path, comes from `PagePackageMount.Url(path, values)`, with the request's route values for the prefix's own: `mount.Url("/articles")` is `/help/articles`. A service of the package that runs outside a request finds the mount in `PagePackageMounts`, a singleton ([C# API](../reference/csharp-api.md#pagepackagemount)). Set `DrawsItsOwnShell` on the descriptor when the package's layout draws a whole page, and `RequiresNonce` when its pages stream (`Page.Defer`, or a Suspense boundary still waiting when the shell goes out).

### Its generated file

The app the package is developed in writes the package's generated file, next to its own:

```csharp
app.MapHelpCenter("/help").WriteTypes("../HelpCenter/generated/netjes.ts");
```

It is written when that app starts in Development and by `netjes generate`, and `netjes generate --check` in that app fails when it is out of date, so run that in the package's CI. It holds the zod schemas of what the package maps through its mount, and its routes, keyed below the mount and without the prefix's values, so it is the same file wherever an app mounts the package, and the package ships it. It declares no `module 'netjes'`: it type-checks in one program with the app's file, and its `Href` checks only the shape of a URL, since a URL from the C# side is the app's ([A page package's file](../reference/react-api.md#a-page-packages-file)).

### Schemas the app reads too

The app's generated file holds none of a package's schemas. Schemas that the app's code reads as well, such as the props of each kind of block a package stores and the app's own pages render, go in an export of the package's, which its extension adds through the mount:

```csharp
var mount = app.MapPagePackage(Package, prefix);
mount.AddTypeScriptExport("blocks", "hero", new Dictionary<string, Type> { ["props"] = typeof(HeroBlock) });
mount.AddTypeScriptExport("blocks", "quote", new Dictionary<string, Type> { ["props"] = typeof(QuoteBlock) });
```

Both files, the app's and the package's, then hold the schemas of `HeroBlock` and `QuoteBlock`, and the export:

```ts
export const blocks = {
  "hero": { "props": HeroBlock },
  "quote": { "props": QuoteBlock },
} as const;
```

The app's code imports it from its own file (`blocks.hero.props.parse(value)`), and the package's code from the package's. The package owns the export, so another package cannot add to it; a second entry of one key throws at startup, and so does a name that is no identifier or that the file takes already. The export's name is taken in both files, so a C# type of the app's with that name gets another; a type of the export's named like one of the app's own gets another name in the app's file, so the app's schemas keep theirs. `netjes generate --check` fails on the app's file when an export changes, and on the package's in the app that writes it, so apps run `netjes generate` after updating the package ([C# API](../reference/csharp-api.md#pagepackagebuilder)).

### Checking it

- **Types.** The app's `npm run typecheck` does not see a package's pages, which only the page module map imports. Type-check them in the package, under the strictest flags an app may set: `strict`, `exactOptionalPropertyTypes`, `noUncheckedIndexedAccess` and `noPropertyAccessFromIndexSignature`, with the package's `app`, `src` and `generated` folders included, as the help center's `tsconfig.json` does.
- **Documents.** In the app the package is developed in, `renderDocument` from `netjes/testing` renders a package's page inside the app's layouts and the package's, with `mounts` naming the prefix: `renderDocument('@acme/help-center/app/contact/page.tsx', { mounts: { '@acme/help-center': '/help' }, props })` ([The document](testing.md#the-document)).
- **Components.** `createTestApp({ mounts: { '@acme/help-center': '/help' } })` gives the package's `Link` and `Form` the mount's URL ([Options](testing.md#options)).
- **The C# side.** Test the loaders and actions through the app that mounts the package, with `WebApplicationFactory`, as for the app's own ([C# tests](testing.md#c-tests-of-loaders-and-actions)).

### Versions

Release both halves together, at one version. The C# half takes it from the project's `<Version>`, the npm half from its `package.json`, and an app that runs two versions stops at startup. Write the range of Netjes versions once, in `peerDependencies.netjes`; the .NET app and the Vite plugin both read it as npm does. Before 1.0, `^0.0.4` holds 0.0.4 alone, so a package that is known to work with later 0.0.x releases states a wider range, such as `>=0.0.4 <0.1.0`.

## What is not there

- **Replacing a layout, a boundary or another segment file.** `.Override` replaces pages only.
- **Mounting inside a `MapGroup`.** `MapPagePackage` throws there: a group's prefix stays out of the route table, so the package's links and the app's `route()` would not know it. Put the group's prefix in the mount's, `/[tenant]/help`, and its conventions on the mount.
- **Mounting one package twice.** The second mount throws.
- **A package without Tailwind, files in `public/`, or Yarn Plug'n'Play.**
- **Removing a package's pages from the client bundle** when the app replaces them or does not mount the package: they are built and shipped all the same.
- **A way for the app to put its own components into a package's pages.** A package that needs one can read them from a React context of its own, which the app provides in its root layout.
