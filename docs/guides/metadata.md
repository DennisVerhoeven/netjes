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
| `{ template: '%s · Docs' }` | In a layout: `%s` is replaced by the title of each page below it. |
| `{ default: 'Netjes Blog' }` | In a layout: the title of pages below it that set none. A layout's own default does not go through its own template, only through templates from layouts above it. |
| `{ absolute: 'Home' }` | Used as it is, skipping every template. |

The Blog's docs layout nests a template inside the root one: `export const metadata: Metadata = { title: { template: '%s · Docs · Netjes Blog' } };` makes a docs page's title `Routing · Docs · Netjes Blog`.

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

`MapRobotsTxt(siteUrl, disallow)` serves `/robots.txt`: `Disallow` for each path given (or `Allow: /` when none is), and a `Sitemap:` line with the URL `MapSitemap` is served at: `/sitemap.xml` under `siteUrl` and the app's base path. Both endpoints allow anonymous requests.

Crawlers read `robots.txt` only at the root of the host. Under a base path, serve the root file from whatever answers the root, with the `Sitemap:` line from the app's own `robots.txt`. The `disallow` paths are written as given, so include the base path in them.

Leave `/_netjes/` out of `disallow`. `<Image>` serves every image through `/_netjes/image`, so a crawler that may not fetch it cannot index your images. The other endpoints there take posts (server actions and web vitals) or hand out an antiforgery token, which a crawler has no use for.

## Third-party scripts: `<Script>`

`<Script>` loads a script without blocking the page, like `next/script`. It renders nothing where it stands; each script is added to the document once, by its `id` or `src`, however many components render it.

```tsx
import { Script } from 'netjes';

<Script src="https://analytics.example/script.js" strategy="lazyOnload" onLoad={() => console.log('loaded')} />

<Script id="consent-defaults">{`window.dataLayer = window.dataLayer || [];`}</Script>
```

| `strategy` | When the script runs |
|---|---|
| `afterInteractive` (default) | Added once the page has hydrated. |
| `lazyOnload` | Added after the page's `load` event, when the browser is idle. |
| `beforeInteractive` | Written into the server-rendered head, before the page's own scripts. External scripts only; an inline one is added after hydration. |

- Inline code goes in as the children, with an `id`, which a script without `src` needs.
- `onLoad` and `onError` are called once the script has loaded or failed, also for a component that renders a script another one added before it.
- Other attributes, such as `integrity`, `crossOrigin` or `data-*`, are set on the element.
- Under a [Content-Security-Policy](security.md) with a nonce, `<Script>` puts the page's nonce on the script. For an inline script of your own, read it with `useNonce()`.
