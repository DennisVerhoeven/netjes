# Metadata

This guide is for app developers. It covers what goes in a page's `<head>`: titles, descriptions, Open Graph and Twitter tags, canonical and alternate links, from page and layout files or from a C# loader. It also covers `sitemap.xml` and `robots.txt`, and loading third-party scripts with `<Script>`. The API follows the Next.js Metadata API.

## Titles and tags from page files

A page or a layout exports `metadata`:

```tsx
// app/layout.tsx
import type { Metadata } from 'netjes';

export const metadata: Metadata = {
  title: { default: 'Netjes Blog', template: '%s · Netjes Blog' },
  openGraph: { siteName: 'Netjes Blog', type: 'website' },
};
```

```tsx
// app/guestbook/page.tsx
export const metadata: Metadata = { title: 'Guestbook' }; // <title>Guestbook · Netjes Blog</title>
```

Netjes merges the metadata of the layouts above a page (outermost first), then the page's own, then what the page's C# loader sent, and writes the result as tags that React puts in the head. Each later one replaces what the earlier ones set, field by field: `openGraph`, `twitter`, `alternates`, `icons` and `other` are merged key by key, everything else is replaced. On a client navigation the tags change with the page.

### Titles

`title` is a string, or an object:

| Title | Effect |
|---|---|
| `'Guestbook'` | Goes through the nearest `template` from a layout above. |
| `{ template: '%s · Docs' }` | In a layout: the first `%s` is replaced by the title of each page below it. |
| `{ default: 'Netjes Blog' }` | In a layout: the title of the pages below it that set none. In a layout or a page it goes through the nearest `template` from a layout above, like a string title, and never through a `template` in the same object. |
| `{ absolute: 'Home' }` | Used as it is, skipping every template. |

The Blog's docs layout nests a template inside the root one: `export const metadata: Metadata = { title: { template: '%s · Docs · Netjes Blog' } };` makes a docs page's title `Routing · Docs · Netjes Blog`.

A layout below the root can set a `default` as well. Say the Blog's docs layout set both a default and a template:

```tsx
// app/docs/layout.tsx
export const metadata: Metadata = { title: { default: 'Docs', template: '%s · Docs · Netjes Blog' } };
```

A docs page that sets no title is then `Docs · Netjes Blog`: the default goes through the root layout's template, `%s · Netjes Blog`, and not through the template beside it. A page's `title: 'Routing'` is still `Routing · Docs · Netjes Blog`, and so is `title: { default: 'Routing' }`, since a page's default goes through the nearest template above it like a string. To keep a title bare, set `absolute` instead of `default`: `{ absolute: 'Docs' }` is `Docs`, whatever templates are above it.

### Fields

| Field | Written as |
|---|---|
| `title` | `<title>` |
| `description` | `<meta name="description">` |
| `keywords` | `<meta name="keywords">`, joined with commas |
| `authors: [{ name, url? }]` | `<meta name="author">` per author (the name) |
| `robots` | `<meta name="robots">`: a string as it is, or `{ index, follow }` as `index, follow`, `noindex, nofollow` and so on |
| `alternates.canonical` | `<link rel="canonical">` |
| `alternates.languages: { 'nl': '/nl' }` | `<link rel="alternate" hreflang>` per language |
| `openGraph` | `og:title`, `og:description`, `og:url`, `og:site_name`, `og:type`, `og:locale`, and `og:image` per image, each followed by its `og:image:width`, `og:image:height` and `og:image:alt` when set |
| `twitter` | `twitter:card`, `twitter:site`, `twitter:creator`, `twitter:title`, `twitter:description`, and `twitter:image` per image, each followed by its `twitter:image:alt` when set |
| `icons: { icon, apple }` | `<link rel="icon">`, `<link rel="apple-touch-icon">` |
| `themeColor` | `<meta name="theme-color">` |
| `other: { name: content }` | `<meta name content>` per entry |

An image's `alt` is written only when it is not empty, and its `width` and `height` only when they are above zero. URLs are written as given, so give crawlers absolute image URLs: under a [base path](routing.md#base-path), an icon in `public/` needs the base path in front (`useBasePath()` in a layout that renders the `<link>` itself).

Another case of a page's literal segments, and with `TrailingSlash.Never` a trailing slash, is redirected to the file's spelling, so a crawler that follows such a link indexes the page once ([One URL per page](routing.md#one-url-per-page)). Each query string is still a URL of its own, though, so a page reached with query strings that show the same content, such as `?utm_source=newsletter` or a sort order, names the URL to index with `alternates.canonical`: `export const metadata: Metadata = { alternates: { canonical: '/blog' } }`, or `Alternates` in a loader's `PageMetadata` when the URL has route values.

### Open Graph and Twitter

`openGraph.images` and `twitter.images` take one image or an array of them, as in Next.js: each is a URL, as a string or a `URL`, or `{ url, width, height, alt }` with the details optional. An empty entry, or an image with an empty URL, is left out.

Once the layouts', the page's and the C# metadata are merged, the link preview tags that are not set fill in as they do in Next.js:

- With `openGraph` metadata, `og:title` and `og:description` take the page's title and description where `openGraph` sets none.
- With `openGraph` or `twitter` metadata, `twitter:title`, `twitter:description` and the `twitter:image` tags take the Open Graph title, description and images that `twitter` does not set, and the page's title and description where there is no Open Graph one either. A title taken this way does not go through a template again: an Open Graph title is used as set, the page's title as its layouts' templates made it.
- Where `twitter` sets no `card`, `twitter:card` is `summary_large_image` when `twitter.images` has an image and `summary` when it has none; with no `twitter` metadata at all, the Open Graph images decide. So `twitter: { site: '@acme' }` next to Open Graph images writes a `summary` card with those images: set `card: 'summary_large_image'` for the large one.

An empty title or description counts as not set, so it takes the fallback, and no `og:` or `twitter:` title or description tag is written empty. An empty `twitter.images` list does count as set: it keeps the Open Graph images out of the Twitter tags, and the card is `summary` unless `twitter` sets one.

### Special pages

`not-found.tsx`, `error.tsx`, `forbidden.tsx` and `unauthorized.tsx` export `metadata` too, and the layouts' title templates apply to them. An `error.tsx`'s metadata applies when the server renders it; an error caught in the browser keeps the page's title.

### `<title>` and `<meta>` elements

React 19 hoists `<title>`, `<meta>` and `<link>` elements rendered anywhere into the head, so a component can render them itself. Below a layout that sets a title, use `metadata` instead: the layout's title is still written, so a `<title>` element makes a second one.

## Metadata from C#

For a title or description that depends on data, the loader sends it with `.WithMetadata(...)`, the counterpart of Next.js's `generateMetadata`:

```csharp
app.MapPageLoader("/blog", (string? tag, PostStore store) =>
    Page.Props(new BlogIndexProps(tag, store.Tags(), store.Posts(tag)))
        .WithMetadata(new PageMetadata { Title = tag is null ? "Blog" : $"Posts tagged {tag}" }));
```

It is merged over the files' metadata, so the layouts' title templates still apply: `Posts tagged dotnet · Netjes Blog`. `PageMetadata` has `Title`, `Description`, `Keywords`, `Robots` (`new PageMetadataRobots(Index: false)`), `Alternates` (`Canonical`, `Languages`), `OpenGraph` (`Title`, `Description`, `Url`, `SiteName`, `Type`, `Locale`, `Images`), `Twitter` (`Card`, `Site`, `Creator`, `Images`; each image is a `PageMetadataImage`, and a plain URL string converts to one), `ThemeColor` and `Other`. Icons and authors are set in the files only.

```csharp
.WithMetadata(new PageMetadata
{
    Title = post.Title,
    Description = post.Summary,
    Alternates = new PageMetadataAlternates(Canonical: $"https://blog.example/blog/{post.Slug}"),
    OpenGraph = new PageMetadataOpenGraph { Type = "article", Images = [new PageMetadataImage($"https://blog.example/img/{post.Slug}.png", 1200, 630, post.Title)] },
})
```

A layout loader's result can carry metadata too, but only the page loader's reaches the head.

If no Node sidecar is ready and the browser renders the page itself, the document Netjes writes has the title, description and robots from C# only; the layouts' templates and the files' metadata apply once the page has rendered ([Rendering and streaming](rendering-and-streaming.md)).

## The `lang` attribute

`<html lang>` is `NetjesOptions.Lang`, `en` by default. With [locale routing](routing.md#locales) it follows the request culture.

## Sitemap and robots

```csharp
var siteUrl = new Uri(app.Configuration["SiteUrl"] ?? "http://localhost:5080");
app.MapSitemap(siteUrl);
app.MapRobotsTxt(siteUrl);
app.MapPages();
```

`MapSitemap(siteUrl, cacheFor)` serves `/sitemap.xml`, the counterpart of `app/sitemap.ts`, from the route table:

- every page without parameters, and each dynamic page's URLs from its `.StaticParams(...)` ([Static export](static-export.md));
- not the pages that require authorization, their own or through a fallback policy, unless they are `.AllowAnonymous()`;
- not the pages marked `.ExcludeFromSitemap()`.

URLs are absolute, under `siteUrl` and the app's base path, with the base path percent-encoded as a URL path. The list of pages is built at most once per `cacheFor` (an hour by default), so `StaticParams` does not run on every request. The XML is kept for the first eight base paths it is requested under in each `cacheFor`, which is one in a normal app. When the base path can come from a client (an `X-Forwarded-Prefix` the app accepts from untrusted addresses), any further one is built from the kept list on each request instead. Under a base path longer than 256 characters as the XML writes it (percent-encoded, with each `&` as `&amp;`), `/sitemap.xml` is not found (404, `Cache-Control: no-store`, logged at debug level) and nothing is built: every URL would repeat the base path, so one small request could ask for a large answer. The sitemap itself is sent with `Cache-Control: public, max-age` set to `cacheFor`.

`MapRobotsTxt(siteUrl, disallow)` serves `/robots.txt`: `Disallow` for each path given (or `Allow: /` when none is), and a `Sitemap:` line with the URL `MapSitemap` is served at: `/sitemap.xml` under `siteUrl` and the app's base path. Both endpoints allow anonymous requests. Both answer `HEAD` as well as `GET`, since crawlers and link checkers send `HEAD` too: a `HEAD` gets the status and headers of a `GET`, without the body ([HEAD requests](routing.md#head-requests)). Each answers one spelling, as a page does: `/ROBOTS.TXT` and `/Sitemap.xml` get a 308 to `/robots.txt` and `/sitemap.xml`, and so do `/robots.txt/` and `/sitemap.xml/` under `TrailingSlash.Never` ([One URL per page](routing.md#one-url-per-page)).

Crawlers read `robots.txt` only at the root of the host. Under a base path, serve the root file from whatever answers the root, with the `Sitemap:` line from the app's own `robots.txt`. The `disallow` paths are written as given, so include the base path in them.

Leave `/_netjes/` out of `disallow`. `<Image>` serves every image through `/_netjes/image`, so a crawler that may not fetch it cannot index your images. The other endpoints there take posts (server actions and web vitals) or hand out an antiforgery token, which a crawler has no use for.

## Third-party scripts: `<Script>`

`<Script>` loads a script without blocking the page, like `next/script`. Each script is added to the document once, by its `id` or `src`, however many components render it, except inline `beforeInteractive` code, which the server writes wherever a component renders it. An `afterInteractive` or `lazyOnload` script renders nothing where it stands; a `beforeInteractive` one is written into the server-rendered document ([below](#code-that-runs-before-the-first-paint)).

```tsx
import { Script } from 'netjes';

<Script src="https://analytics.example/script.js" strategy="lazyOnload" onLoad={() => console.log('loaded')} />

<Script id="consent-defaults">{`window.dataLayer = window.dataLayer || [];`}</Script>
```

| `strategy` | When the script runs |
|---|---|
| `afterInteractive` (default) | Added once the page has hydrated. |
| `lazyOnload` | Added after the page's `load` event, when the browser is idle. |
| `beforeInteractive` | In a server-rendered document: inline code runs where it stands, as the parser reaches it, before what follows it is painted; an external script runs once the shell is parsed, in order with the others, before the page's own code and hydration. A component that mounts in the browser adds it as `afterInteractive` does. |

- Inline code goes in as the children, with an `id`, which a script without `src` needs.
- `onLoad` and `onError` are called once the script has loaded or failed, also for a component that renders a script another one added before it.
- Other attributes, such as `integrity`, `crossOrigin` or `data-*`, are set on the element.
- Under a [Content-Security-Policy](security.md) with a nonce, `<Script>` puts the page's nonce on the script. For an inline script of your own, read it with `useNonce()`.

### Code that runs before the first paint

A `beforeInteractive` script with inline code is written into the server's HTML where the component stands, so the browser runs it as its parser reaches it, before it parses or paints anything below it. That is the place for code the first paint depends on, such as a theme read from storage, set on `<html>` before any of the page shows:

```tsx
// app/layout.tsx
import { type LayoutProps, Script } from 'netjes';

const theme = `
  let theme = 'light';
  try { theme = localStorage.getItem('theme') ?? (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'); } catch {}
  document.documentElement.dataset.theme = theme;
`;

export default function RootLayout({ children }: LayoutProps) {
  return (
    <>
      <Script id="theme" strategy="beforeInteractive">{theme}</Script>
      {children}
    </>
  );
}
```

React leaves an attribute that such a script sets on `<html>` or `<body>` as it is when it hydrates the page ([Layouts](routing.md#layouts)). After a hydration error, React renders the page in the browser instead and replaces the attributes of both elements with its own, and the script does not run again.

- A browser runs inline code only once the stylesheets before it in the document have loaded, so it runs after the head's stylesheets, and before the page's own code. Put it first in the root layout, as above; a script takes no focus, so a skip link after it is still the first element Tab reaches.
- An external script (`src`) is preloaded from the head, so the browser fetches it while it parses the document. Once the shell is parsed, Netjes runs the external `beforeInteractive` scripts in it one after the other, in the order they stand in the document, with the page's nonce. They do not hold up the parser or the page's rendering: the page shows while they load. Hydration waits for them: the page's own modules run, and the page responds to clicks, once each of them has loaded or failed, so a host that is slow to answer delays the page's interactivity. A script the browser does not run, one with `noModule` or a `type` that is neither JavaScript nor `module`, such as the `text/plain` a consent manager looks for, is not preloaded and not waited for. It is still added to the head, with its `id` and attributes, where the consent manager finds it.
- All the inline code in the shell runs before any external script, so inline code cannot use what an external `beforeInteractive` script defines.
- A page with a `beforeInteractive` script runs its own modules once the shell is parsed, instead of while the rest of the shell arrives ([ADR 0044](../decisions/0044-before-interactive-scripts-run-before-the-pages-code.md)).
- Keep these scripts in the shell, outside any Suspense boundary that may still wait when the shell goes out (a `loading.tsx` included). In such a boundary, inline code runs when the boundary's content arrives, and an external script runs when that part of the page hydrates, not before the page's code.
- Render each one in one place, such as the root layout: a server-rendered document carries the inline code of every `beforeInteractive` script a component renders, so the same code rendered by two components runs twice. An external script runs once per `id`, or `src` when it has none, as with the other strategies.
- On a page the browser renders itself (after a client navigation, when no sidecar was ready, or in a `createTestApp()` test) a component that mounts adds the script after it mounts, as `afterInteractive` does. A script the server's document already ran is not added again.
- `onLoad` and `onError` are called once an external script has loaded or failed, and `onLoad` once inline code has run, after the component mounts.
- `async`, `defer` and `type="module"` do not apply. In development Netjes warns about them: an external script runs in order whatever `async` and `defer` say, and inline code with `type="module"` would wait until the whole document is parsed.
- Under a nonce-based policy the script carries the page's nonce. Under a hash-based one, list the hash of the inline code as the document carries it: Netjes writes the `s` of `<script` and `</script` (in any case) as `\u0073` (`\u0053` for `S`), as React does in its own scripts, so the code cannot end its element. JavaScript reads that as the same letter in strings, template literals, regular expressions and names, and the hash is of the text with it. Code without `<script` or `</script` in it is written as it is. A component that adds the code in the browser writes the same text, so the one hash allows it on both paths. `inlineScriptHashes(await renderDocument(...))` from `netjes/testing` lists the hash of each such script on a page ([the document](testing.md#the-document)), and an external script's host goes in `script-src` ([security](security.md#hash-based)).
