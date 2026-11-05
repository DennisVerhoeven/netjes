# Routing

This guide is for app developers. It covers how the files in `app/` become URLs, how pages link to each other and navigate in the browser, and how a component reads the URL. The conventions are the Next.js App Router's; [Coming from Next.js](../coming-from-nextjs.md) lists where Netjes differs. Data for pages and layouts comes from C# loaders, described in [Data loading](data-loading.md).

## Pages

A page is a `page.tsx` file whose default export is a React component. Its folder path under `app/` is its URL:

```text
app/
  layout.tsx                  wraps every page
  page.tsx                    /
  about/page.tsx              /about
  blog/page.tsx               /blog
  blog/[slug]/page.tsx        /blog/hello-world
  docs/[...path]/page.tsx     /docs/getting-started/routing
```

```tsx
// app/about/page.tsx
import type { Metadata } from 'netjes';

export const metadata: Metadata = { title: 'About' };

export default function About() {
  return <h1>About</h1>;
}
```

A page without a C# loader renders with no props. A page with one reads its props through `definePage` and the generated schema ([Data loading](data-loading.md#reading-props-in-a-page)).

Page files are `.tsx` or `.jsx`. Tests next to them (`page.test.tsx`, `page.spec.jsx`) are not pages, and any other file in a folder is an ordinary module you can import.

Each page file becomes an ASP.NET Core endpoint with a route template: `blog/[slug]` is `/blog/{slug}`, `docs/[...path]` is `/docs/{**path:minlength(1)}` and `shop/[[...filters]]` is `/shop/{**filters}`. `app.MapPages()` maps every page that has no loader of its own, plus a fallback that renders the 404 page; `app.MapPageLoader(pattern, ...)` maps a page with a loader. Because they are endpoints, the ASP.NET Core matcher decides precedence, and conventions such as `.RequireAuthorization()` work as they do on any minimal API. The browser never matches routes itself: it asks the server for every page ([ADR 0002](../decisions/0002-server-owns-routing.md)).

## Layouts, boundaries and other special files

A folder can hold these files next to its `page.tsx`:

| File | What it does |
|---|---|
| `layout.tsx` | Wraps every page in and below the folder. It stays mounted while the user navigates between those pages, so its state survives. |
| `template.tsx` | Like a layout, but mounted again on every navigation to another path. |
| `loading.tsx` | The Suspense fallback while a page below waits for deferred values or code. |
| `error.tsx` | The error boundary for the pages below the folder's layout. |
| `not-found.tsx` | Rendered when the loader of a page in or below the folder returns `Page.NotFound()`. The root one also renders for unknown URLs. |
| `forbidden.tsx` | Rendered for `Page.Forbidden()` and for an empty 403 from authorization. |
| `unauthorized.tsx` | Rendered for `Page.Unauthorized()` and for an empty 401 from authorization. |
| `global-error.tsx` | In `app/` only: rendered without any layout when even the error page fails. |
| `default.tsx` | In a `@slot` folder only: what a [parallel route](#parallel-routes) renders when no page in it matches. |

Per folder they nest as the App Router nests them: layout, then template, then error boundary, then the `loading.tsx` Suspense boundary, then the folder below or the page.

### Layouts

A layout gets `children` and the page's `params` (`LayoutProps`). The root layout, `app/layout.tsx`, wraps every page; it is where the global stylesheet is imported and the site's title template is set:

```tsx
// app/layout.tsx
import { Link, type LayoutProps, type Metadata, useSharedProps } from 'netjes';
import { sharedProps } from '../generated/netjes.ts';
import '../styles/global.css';

export const metadata: Metadata = { title: { default: 'My App', template: '%s · My App' } };

export default function RootLayout({ children }: LayoutProps) {
  const site = useSharedProps(sharedProps);
  return (
    <div>
      <header>
        <Link href="/">{site.siteName}</Link>
      </header>
      <main>{children}</main>
    </div>
  );
}
```

React renders the whole document ([ADR 0003](../decisions/0003-react-renders-the-whole-document.md)): Netjes writes `<html>`, `<head>` and `<body>` around the root layout, so a layout returns what goes inside the body. Tags such as `<title>` and `<link rel="icon">` that a layout or page renders end up in the head.

A layout cannot put attributes on `<html>` or `<body>`, but a script can before React hydrates the document: a theme script, a browser extension or a translator. In development React logs a hydration error for each attribute on an element that the element's props do not have, such as one a script added (it does not check attributes in production), so Netjes renders both elements with `suppressHydrationWarning`. React then logs nothing for the attributes of those two elements, `lang` included, and when it hydrates the page it leaves an attribute as the script set it. In production, a page that the browser renders itself because no sidecar was ready ([When no sidecar is ready](rendering-and-streaming.md#when-no-sidecar-is-ready)) loses such attributes: React then replaces the attributes of both elements with its own.

The prop also covers the text directly in `<body>`, with no element or `<Suspense>` boundary around it, such as text that a root layout returns next to `{children}`, or that a page returns outside any element when its layouts add none. React does not compare that text with the server's, in development or in production: when the two differ, nothing is reported and the page keeps showing the server's text until that text changes. Text inside an element is compared as usual, so put top-level text that can differ between the server and the browser, such as a time, in an element. A mismatch in the head, or in any element in the body, is reported as before ([ADR 0026](../decisions/0026-netjes-html-and-body-skip-reacts-attribute-check.md)), apart from the text of the `<style>` elements that hold a page's CSS in development, which Vite's client owns once it runs ([ADR 0035](../decisions/0035-development-styles-are-inlined-from-the-client-build.md)).

A layout that needs data gets it from a C# layout loader, `app.MapLayoutLoader(...)`, and declares it with `defineLayout` ([Layout loaders](data-loading.md#layout-loaders)). A layout loader's conventions, such as `.RequireAuthorization()`, cover every page below it.

### Error, not-found, forbidden and unauthorized pages

An `error.tsx` gets `{ error, reset }` (`ErrorPageProps`). `error.status` holds the HTTP status when the server rendered the page for an error, and `reset()` renders the segment again:

```tsx
// app/error.tsx
import type { ErrorPageProps } from 'netjes';

export default function ErrorPage({ error, reset }: ErrorPageProps) {
  return (
    <section role="alert">
      <h1>Something went wrong</h1>
      {/* In development the message can take several lines: a syntax error comes with a code frame. */}
      <p style={{ whiteSpace: 'pre-wrap' }}>{error.message}</p>
      <button type="button" onClick={reset}>Try again</button>
    </section>
  );
}
```

An error boundary sits inside its folder's layout, so it catches errors in the pages and folders below, not in that layout itself. To catch an error in a layout, put an `error.tsx` in the folder above. On the server, an exception in a loader or a render gets the nearest `error.tsx` above the failing segment, with status 500 (or the status of a `BadHttpRequestException`).

`Page.NotFound()`, `Page.Forbidden()` and `Page.Unauthorized()` from a page loader render the nearest `not-found.tsx`, `forbidden.tsx` or `unauthorized.tsx`, in the page's folder or one above it, inside the layouts above that file, with status 404, 403 or 401. Without a `forbidden.tsx` or `unauthorized.tsx` the error page renders instead. Authorization that answers an empty 401 or 403 gets the same pages; with cookie authentication, answer those statuses instead of redirecting to a login page, as the TaskBoard example does:

```csharp
options.Events.OnRedirectToLogin = context =>
{
    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    return Task.CompletedTask;
};
```

Netjes has built-in 404 and error pages for apps that have neither file. Special pages can export `metadata` like any page, and the layouts' title templates apply to them ([Metadata](metadata.md)).

## Dynamic segments

| Folder | Matches | `params` in React | What a loader binds |
|---|---|---|---|
| `[slug]` | one segment: `/blog/hello` | `{ slug: 'hello' }` | `string slug` = `"hello"` |
| `[...path]` | one or more segments: `/docs/a/b` | `{ path: ['a', 'b'] }` | `string path` = `"a/b"` |
| `[[...path]]` | none or more: `/docs`, `/docs/a/b` | `{ path: [] }`, `{ path: ['a', 'b'] }` | `string? path` = `null`, `"a/b"` |

A catch-all must be the last segment. Parameter names are letters, digits and underscores, not starting with a digit, and each name appears once in a route, in any case. Literal segments match in any case, as ASP.NET Core routes do, and a request in another case is redirected to the file's spelling ([One URL per page](#one-url-per-page)). Route values reach loaders and `params` as the URL spelled them.

Build URLs with `route()` from the generated file, which encodes each value and takes catch-all values as arrays. The third argument is the query, and `{ hash }` in the fourth is the fragment:

```tsx
import { route } from '../generated/netjes.ts';

route('/blog/[slug]', { slug: post.slug });          // '/blog/hello-world'
route('/docs/[...path]', { path: ['guides', 'a b'] }); // '/docs/guides/a%20b'
route('/projects/[id]', { id }, { done: 2 });          // '/projects/web?done=2'
route('/docs/[...path]', { path: ['guides', 'caching'] }, undefined, { hash: 'keys' }); // '/docs/guides/caching#keys'
```

## One URL per page

ASP.NET Core matches literal segments in any case and ignores one trailing slash, so a page answers many URLs: `/about`, `/About`, `/ABOUT/`. Served as they are, each would be a page of its own to a crawler and to the output cache, and a `<Link>` is marked current only on the spelling it names. Two settings give a page one spelling, the one its file names:

| Option | Default | What it does |
|---|---|---|
| `RedirectLiteralCase` (`Netjes:RedirectLiteralCase`) | `true` | `/About` and `/ABOUT` answer 308 with `Location: /about`, the spelling of `app/about/page.tsx`. `false` answers every case, as ASP.NET Core does. |
| `TrailingSlash` (`Netjes:TrailingSlash`) | `Ignore` | `Ignore` answers `/about` and `/about/` alike. `Never` answers `/about/` with a 308 to `/about`. |

```csharp
builder.Services.AddNetjes(options => options.TrailingSlash = TrailingSlash.Never);
```

What a redirect changes, and what it keeps as it was sent:

- Only the literal segments change case, the ones the file names: `/Blog/Hello-World` goes to `/blog/Hello-World`, and the loader of `app/blog/[slug]` gets `Hello-World` either way. The query string goes along as sent (`/About?Tab=Team` goes to `/about?Tab=Team`), and so does the base path, in its own case (`/Docs/About` goes to `/Docs/about` under `UsePathBase("/docs")`).
- Values go out encoded as they came: `/Blog/caf%C3%A9` goes to `/blog/caf%C3%A9`, and `%2F` stays `%2F`.
- Under `Ignore`, a trailing slash stays: `/About/` goes to `/about/`. Under `Never` it goes, a catch-all's included: `/docs/a/b/` goes to `/docs/a/b`, so `[...path]` gets `a/b` and not `a/b/`. `/` stays `/`, at the root and under a base path.
- A client navigation to another spelling (a `<Link>` or `router.push`) gets a redirect payload, and the router shows the file's spelling. A `HEAD` gets the 308 as a `GET` does.
- `/robots.txt` and `/sitemap.xml`, from `MapRobotsTxt` and `MapSitemap`, answer one spelling too, with a plain 308.

What it leaves alone:

- Requests with another method: a form that posts to `/About/` gets its answer at that URL.
- The app's own endpoints, a minimal API that renders a page (`Page.Render`) and the not-found page.
- A request that a rewrite or a re-execution changed (`UseRewriter`, `UseStatusCodePagesWithReExecute`, `UseExceptionHandler`, the page Netjes renders for a failed form post). The redirect reads the URL the client sent, the request target, and acts only when it spells the path routing matched ([rewrites](redirects-rewrites-headers.md#rewrites)).
- A request target that browsers do not send: one with `.` or `..` segments (`/./About`), which the server resolves before routing, or one in absolute form (`GET http://example.com/About`), which clients send only to a proxy. It is answered as before, and under output caching its page can be stored under `/About` ([caching](caching.md#how-entries-are-keyed)).
- TestServer, which `WebApplicationFactory` tests run on: it passes on no request target, so nothing is redirected there ([testing](testing.md#the-app-over-http)).
- The case of the base path and of route values: in a `[lang]` folder, `/EN/about` is still a URL of its own next to `/en/about`.
- A URL whose redirect could lead off the site or to another page, such as `//other.example/` under a root catch-all with `Never`: it is answered as it was sent. See [security](security.md#redirects).

A loader that compared the URL's spelling of its literal segments, to redirect to its own, can drop that check; a check of a route value's case (a slug) stays the loader's.

Browsers keep a 308 and follow it again without asking the server. After a folder is renamed to another case (`about` to `About`), a visitor whose browser followed the old redirect from `/About` to `/about` is sent round in a loop until the browser forgets it. Behind a proxy or CDN that adds a trailing slash to URLs, `Never` loops at once: turn off one of the two.

Next.js matches a route in its file's case only, so `/About` is a 404 there, and by default (`trailingSlash: false`) it answers `/about/` with a 308 to `/about`, as `TrailingSlash.Never` does. Netjes keeps a link in another case working, and leaves a trailing slash as ASP.NET Core has it unless you choose `Never` ([ADR 0039](../decisions/0039-a-page-url-has-one-spelling.md)).

## Route groups and private folders

A folder in parentheses, `(marketing)`, adds nothing to the URL. Use it to give some pages a layout of their own: `app/(marketing)/layout.tsx` and `app/(app)/layout.tsx` can both sit at `/`, each wrapping its own pages. If two groups put layouts at one URL, a layout loader names the folder rather than the URL: `app.MapLayoutLoader("app/(app)", ...)`.

A folder whose name starts with `_`, such as `_components`, is not routed, so components can live next to the pages that use them.

## Parallel routes

A folder named `@slot` is a slot of the layout in the folder above it. The layout gets each slot as a prop named after the folder, without the `@`:

```text
app/dashboard/
  layout.tsx              gets { children, team, activity }
  page.tsx                /dashboard
  settings/page.tsx       /dashboard/settings
  @team/page.tsx          the team slot at /dashboard
  @team/default.tsx       the team slot at /dashboard/settings
  @activity/page.tsx      the activity slot at /dashboard; at /dashboard/settings it renders nothing
```

```tsx
// app/dashboard/layout.tsx
import type { LayoutProps } from 'netjes';
import type { ReactNode } from 'react';

export default function DashboardLayout({ children, team, activity }: LayoutProps & { team: ReactNode; activity: ReactNode }) {
  return (
    <div className="grid grid-cols-3">
      <main className="col-span-2">{children}</main>
      <aside>{team}{activity}</aside>
    </div>
  );
}
```

On every request and every client navigation, the server picks what each slot renders: the slot's page whose path below the layout matches the current page (parameters match by position, `[...x]` covers any depth, and the most specific page wins), else the slot's `default.tsx`, else nothing (the prop is `null`). A slot page gets its own route values as `params`, under its own parameter names. Inside a slot folder only `page` and `default` files count.

## Intercepting routes

An intercepting route shows another page inside a slot, the way a photo opens in a modal over a feed. It is a folder inside a slot whose name starts with `(.)` (the slot's own level), `(..)` (one level up; `(..)(..)` two), or `(...)` (the root), followed by the path it intercepts:

```text
app/
  layout.tsx                        renders the modal slot next to children
  feed/page.tsx                     /feed
  photo/[id]/page.tsx               /photo/7: the full page
  @modal/default.tsx                renders nothing
  @modal/(.)photo/[id]/page.tsx     /photo/7 in the modal slot
```

A client navigation to `/photo/7` from a page inside the slot's layout keeps that page on screen, with its state, and renders the intercepting page in the slot. The intercepting page gets the props of the real page's loader and its own `params`. Back closes it, and `router.refresh()` keeps it open over the page it opened on. A document request, such as a reload or a shared link, always shows the real page. Intercepting folders work only inside a slot.

## Linking and navigating

### Links

[`<Link>`](../reference/react-api.md#link) renders an `<a>` and navigates in the browser: it asks the server for the next page's props as JSON, loads the page's code, and swaps it in while the layouts above stay mounted.

```tsx
import { Link } from 'netjes';
import { route } from '../generated/netjes.ts';

<Link href={route('/blog/[slug]', { slug: post.slug })}>{post.title}</Link>
<Link href={route('/docs/[...path]', { path: ['guides', 'caching'] }, undefined, { hash: 'keys' })}>Cache keys</Link>
<Link href="/about" className="aria-[current=page]:font-bold">About</Link>
```

- `href` is typed by the app's routes once `netjes generate` has written `generated/netjes.ts`: a path no page serves is a type error. `Href.parse(value)` from the generated file checks a URL that comes from data, such as a CMS ([Typed links](data-loading.md#typed-links)). A link that a C# loader or the shared props send as a `Netjes.Href` has been checked against the pages when the props were parsed, so it goes to `href` as it is ([Links from C#](data-loading.md#links-from-c)).
- The link gets `aria-current="page"` when it goes to the page on screen, which screen readers announce and styles can target: its path is the page's, with `/docs` and `/docs/` counted as one, and the page has the same values for every query parameter the link names, in any order. Parameters the link leaves out do not count: on `/blog?tag=react&utm_source=nl`, the links `/blog?tag=react` and `/blog` are marked and `/blog?tag=vue` is not. Names and values are compared with their case, as `useSearchParams()` reads them: a loader's `tag` parameter gets `react` on `/blog?Tag=react` too, since ASP.NET Core binds query names in any case, but `/blog?tag=react` is not marked there. A link with a `#`, such as `/#features`, points to a place in a page and is never marked, nor is a URL with its own scheme or host (`https://…`, `mailto:`).
- An `aria-current` prop of your own wins. `aria-current={undefined}` leaves the attribute off, as an "All" filter (`/blog`) needs on `/blog?tag=react`, and `aria-current="true"` marks a link to a section ([The current section](#the-current-section)).
- It prefetches the next page on hover, focus and touch. `prefetch="none"` turns that off. A prefetched page is kept for 30 seconds, and every server action or form post drops the ones kept so far.
- To link to a place in a page, pass the `id` of an element there as `hash` in the fourth argument of `route()`, as the second link above does. The third argument is the query: pass `undefined` when the link has none. One leading `#` in `hash` is dropped and the rest is percent-encoded; with nothing left, no fragment is added. Browsers and the router decode a fragment when they look for the element, so `hash: 'über-uns'` gives `#%C3%BCber-uns` and finds `<h2 id="über-uns">`. Like any link with a `#`, such a link is never marked as the current page.
- After navigating, the page scrolls to the top, or to the element the `#fragment` names, which also takes the focus. A link to another page starts the focus over at the top of the document, as a page load does ([Focus and announcements](#focus-and-announcements)). `scroll={false}` keeps the scroll position, and keeps the focus on the link while the link has it; `replace` replaces the history entry instead of adding one.
- Ctrl-, Cmd-, Shift- and Alt-clicks, other mouse buttons, a `target` other than `_self`, a `download` attribute and URLs on another origin are left to the browser, as for a plain `<a>`. `reloadDocument` makes the browser load the URL with a full page load, for a file download or a page outside Netjes.
- A `ref` gets the `<a>` element, as on a plain `<a>`, to focus the link or scroll it into view. `<Form>`, `<SubmitButton>` and `<Image>` take a `ref` in the same way, for the `<form>`, `<button>` and `<img>` they render.

### The current section

`<Link>` marks only a link to the page on screen. A link that stands for a whole section, such as Docs in a header shown on every page under `/docs`, sets `aria-current` itself: `"page"` on the section's own page and `"true"` on the pages below it.

```tsx
import { Link, useSelectedLayoutSegments } from 'netjes';

// In the root layout the segments are the whole path: ['docs'] on /docs, ['docs', 'intro'] on /docs/intro.
export function DocsLink() {
  const [section, ...below] = useSelectedLayoutSegments();
  const current = section !== 'docs' ? undefined : below.length > 0 ? 'true' : 'page';
  return (
    <Link href="/docs" aria-current={current}>
      Docs
    </Link>
  );
}
```

A selector on `[aria-current]` styles both values. `usePathname()` works as well (`pathname.startsWith('/docs/')`), but the segments count `/docs/` as `/docs`, as `<Link>` does. In the section's own layout, `useSelectedLayoutSegment()` is `null` on the section's page and names the segment below it on the others ([Reading the URL](#reading-the-url)).

### Back and forward

The router remembers the last 50 pages it showed, one per history entry, and shows them again on back and forward without asking the server, restoring the scroll position. An action or form post that sets or deletes a cookie, such as signing in or out, makes it forget them, so those pages are loaded from the server again ([Signing in and out](actions-and-forms.md#signing-in-and-out)).

A fragment link to the same page (`#comments`) changes the history entry, scrolls to the element and focuses it, without a request. When it adds an entry, Back scrolls to where the page was, also for a link followed while the page was still hydrating.

### Focus and announcements

A page load tells a screen reader which page has opened, and starts the keyboard focus over at the top of the document. A client navigation swaps the page without doing either, so the router does both ([ADR 0033](../decisions/0033-client-navigations-announce-the-page-and-start-the-focus-over.md)).

After a navigation to another path or query, a live region that the router renders after the app's content (`aria-live="polite"`, visually hidden) gets the new page's name: its `document.title`, or else the text of its first `h1`, or else its path. The region speaks only when that name differs from the previous page's, so give each page a title of its own, with `metadata` or with `PageMetadata` from C# ([Titles](metadata.md#titles)): two pages with one title sound like one page. A refresh, a form post that stays on its page and a link to a fragment of the page on screen are not announced. Nor is a page that an intercepting route shows in a modal: the page under it keeps its title, so the modal names itself, as a dialog with a label does. The region renders in the browser only, once the page has hydrated, so the server's HTML does not contain it. It has no role, so a test that looks for a `status` or an `alert` does not find it; an end-to-end test reads it as `page.locator('[data-netjes-route-announcer]')`.

Once it has scrolled, the router moves the focus by the first of these rules that applies:

1. When the router scrolls to a `#fragment`, as after a link, `router.push` or `router.replace` to a URL with one, the element it names takes the focus, on the page on screen or on the page the navigation opens. An element that takes no focus of its own, such as a heading or `main`, gets `tabindex="-1"` until it loses the focus again.
2. On the same page, the focus stays where it is: after a navigation that changes only the query, `router.refresh()`, a form post that stays on its page, and a modal that an intercepting route opens over the page, or that Back closes.
3. When the focus moved while the navigation ran, it stays where it went: on the new page's `autoFocus` element, or wherever the user or the app's code put it, such as a menu that gives the focus back to its button as it closes. The `onClick` of the `<Link>` that was followed and the `onSubmit` of the `<Form>` that was submitted count as code that runs during the navigation.
4. When the element that had the focus as the navigation started still has it, it keeps it if the navigation keeps the scroll position, or if the element is a field the user types into (a text field, a `select`, editable content), such as a search box in a layout that navigates as the user types. A link with `scroll={false}` keeps the scroll position, and so does `router.refresh()`, also when the server redirects it to another page.
5. Otherwise the focus starts over at the top of the document, as after a page load: the next Tab goes to the first element that takes the focus. The router focuses `body` for this, giving it `tabindex="-1"` for that moment only, and leaves a `tabindex` of the app's own on `body` as it was.

No focus call scrolls, so the scroll position the router set stays, the one Back and Forward restore included. Back and Forward restore the scroll position the router saved for the entry instead of scrolling to a fragment, so rule 1 does not apply to them: the element a `#fragment` names does not take the focus. Rules 2 to 5 apply as after a link: after Back to another page, the next Tab starts from the top of that page, not from the link the user followed from it. A page that wants the focus somewhere else, on its heading say, moves it there in an effect: the page's `useEffect` runs after the router has moved the focus, and the router leaves the focus where the page's layout effects put it (rule 3).

A few things follow from these rules:

- **Skip links.** After each navigation to another page, Tab goes through everything above the page again, such as the header and its navigation, unless the root layout starts with a skip link, as the layout of a new app does:

  ```tsx
  <Link href="#content" prefetch="none" className="sr-only focus:not-sr-only">
    Skip to content
  </Link>
  {/* the header and its navigation */}
  <main id="content">{children}</main>
  ```

  It links a place on the page on screen, which needs no prefetch. Following it focuses `main` (rule 1), so the next Tab goes into the page's content, as it does without JavaScript.
- **Menus.** A menu or a drawer that closes as one of its links is followed can give the focus back to its button, in the link's `onClick` or as the menu closes, and the router leaves it there (rule 3). A handler that navigates with `router.push` gives the focus back after that call: the router notes where the focus is as the call starts the navigation. If the menu closes later, in an effect that runs after the navigation, give the focus back to its button only while the focus is still inside the menu: by then the router has moved the focus, and the button would take it from the top of the new page.
- **Modals.** When a modal that an intercepting route shows closes, give the focus back to the link that opened it, as a dialog does. The router leaves the focus alone when the modal opens and when it closes, since the page under it stays (rule 2).
- **Tabs and pagination.** A link that changes only the query, such as `?page=2`, keeps the focus, since the page stays. A link that changes the path replaces the page, and a tab or pagination link inside the page goes with it, so the focus starts over at the top, even with `scroll={false}`. Tabs in a layout stay mounted, and with `scroll={false}` they keep the focus (rule 4).
- **End-to-end tests.** After a navigation to another page the focus is on `body`, so a test that types right after one focuses its field first, as `locator.fill()` does. A test that reads the text of a whole container, such as `body`, also gets the region's text: read the element you mean instead.

### `useRouter`

```tsx
import { useRouter } from 'netjes';

const router = useRouter();
await router.push('/projects');               // navigate, adding a history entry
await router.replace(route('/login', {}, { returnUrl }), { scroll: false });
router.prefetch('/projects/web');             // load ahead, as a link does on hover
await router.refresh();                       // run this page's loaders again
router.back();
router.forward();
```

`refresh()` loads the page on screen from the server in place: every loader runs again, layout loaders included, in the same history entry, with the URL's fragment, without scrolling. `push`, `replace` and `refresh` return promises that resolve once the router has handed the new page to React, or once the navigation has ended another way: another navigation took its place, the browser loads the URL with a full page load, or the router refused the URL. When a promise resolves, the old page can still be on screen: React shows the new one when it commits it, and inside a transition of the app's own, such as a form action, only once that transition has ended ([ADR 0034](../decisions/0034-navigation-promises-resolve-when-react-has-the-page.md)). Code that needs the new page runs in an effect of that page, or in an effect that depends on `usePathname()` and `useSearchParams()`; a link to a fragment moves the focus to its element without any code.

### Navigation state

`useNavigation()` reports what the router is doing, for a progress bar or a disabled button:

```tsx
import { useNavigation } from 'netjes';

export function ProgressBar() {
  const navigation = useNavigation();
  return <div aria-hidden="true" data-state={navigation.state} className="progress" />;
}
```

`state` is `'idle'`, `'loading'` (a navigation) or `'submitting'` (a form post), and while it is not idle, `location` is the path and query on their way. After a form post that did not produce a page (an error response, or no response at all), `state` is `'idle'` with `error: { status, action }` until the next navigation; `status` is `null` when nothing answered.

### When the router hands over to the browser

The router falls back to a full page load when a navigation fails, when the server's answer is not a Netjes page, and when the page comes from another build of the app (after a deploy) or names a page this build does not have. A redirect to another site, from a loader or an action, is a full page load too. The router only ever loads pages from the document's own origin, and never navigates to a URL that is not http or https.

A navigation also fails when a module of the next page does not load: the request for its chunk was dropped, or a deploy removed the chunk. The browser then loads that page in full. On the first page of a document, a module that fails to load is tried once more, half a second later. If it fails again, the page stays as the server rendered it, without the client router, and the browser console names the module. Its links and forms work as plain HTML, so the next page the visitor opens is a full page load, and that document starts the router again. The [client-rendered fallback](rendering-and-streaming.md#when-no-sidecar-is-ready) has no page HTML to fall back on, so it stays blank.

### Forms that navigate

A `<Form method="get">` builds a query string from its fields and navigates to it, so filters live in the URL, work without JavaScript and can be shared. TaskBoard's board filters its tasks this way:

```tsx
<Form method="get" aria-label="Filter tasks">
  <input name="q" type="search" defaultValue={search.q ?? ''} />
  <button type="submit">Apply</button>
</Form>
```

Forms that post are covered in [Actions and forms](actions-and-forms.md).

## Reading the URL

| Hook | Returns |
|---|---|
| `usePathname()` | The path, without the base path: `/blog/hello`. |
| `useSearchParams()` | The query string as `URLSearchParams`. |
| `useSearchParams(schema)` | The query string parsed by a schema. |
| `useParams()` | The route values: `{ slug: 'hello' }`, catch-all values as arrays. |
| `useParams(schema)` | The route values parsed by a schema. |
| `useSelectedLayoutSegment()` | In a layout: the segment one level below it, or `null` at the layout's own page. |
| `useSelectedLayoutSegments()` | In a layout: all the segments below it. |

The [React API](../reference/react-api.md#navigation) has their signatures. Visitors type URLs, so parse what you read with a schema. `useSearchParams(schema)` gets each value as a string, or an array of strings when a key repeats, and drops the values the schema rejects so its defaults and optional fields apply, rather than failing the page. `useParams(schema)` throws to the error page when a value is rejected, so constrain route values in C# (a loader returning `Page.NotFound()`) instead. Define schemas at module level: a new schema object on every render parses again. On a page that `netjes export` wrote, `useSearchParams()` sees no query string, whatever the address bar shows; [static export](static-export.md#in-the-browser) says what to read there instead.

```tsx
import { definePage, useParams, useSearchParams } from 'netjes';
import { z } from 'zod';
import { TaskPriority, pages } from '../../../../generated/netjes.ts';

// ?priority=whatever is dropped, so the filter falls back to "any".
const filters = z.object({
  q: z.string().optional(),
  priority: TaskPriority.optional(),
  done: z.coerce.number().int().positive().optional(),
});
const params = z.object({ id: z.string() });

export default definePage(pages['/projects/[id]'], function Board({ columns }) {
  const { id } = useParams(params);
  const search = useSearchParams(filters);
  // ...
});
```

`useSelectedLayoutSegment(s)` count from the layout's own folder, leave route groups out (they are not in the URL) and decode each segment. A layout uses them for tabs:

```tsx
// In app/projects/[id]/layout.tsx: 'settings' at /projects/web/settings, null at /projects/web.
const tab = useSelectedLayoutSegment();
```

## Base path

To serve the app under a path, such as `/docs`, set the base path on both sides:

```csharp
app.UsePathBase("/docs"); // before UseNetjes()
app.UseNetjes();
```

```ts
// vite.config.ts
export default defineConfig({ plugins: [netjes({ basePath: '/docs' })] });
```

Write links, form actions, redirects and router calls as if the app were at the root (`route()` builds paths that way): `<Link>`, `<Form>`, `useRouter()`, `Page.Redirect` and `ServerAction.Redirect` add the base path, except to paths that already start with it. `usePathname()` and `useNavigation()` locations leave it out. For a URL in your own markup, such as a plain `<a href>` or a `<link rel="icon">`, use `useHref(path)` or prefix `useBasePath()`; for a submit button's `formAction`, use `<SubmitButton formAction>`, which works without JavaScript too. `<Image>` puts its `public/` paths under the base path itself.

## Locales

Locale routing puts the pages under a `[lang]` folder (`app/[lang]/page.tsx`, `app/[lang]/about/page.tsx`) and lets the URL decide the request culture:

```csharp
builder.Services.AddNetjesLocalization(o => o.SupportedCultures = ["en", "nl"]); // the first is the default
// ...
app.UseNetjes();
app.UseNetjesLocalization(); // after UseNetjes: it reads the [lang] route value
app.MapLocaleRedirect();     // "/" redirects to the browser's best locale, /nl for nl-BE
```

```tsx
import { useLocale } from 'netjes';

const locale = useLocale(); // 'nl' at /nl/about; null without AddNetjesLocalization
```

- Loaders run with `CultureInfo.CurrentCulture` and `CurrentUICulture` set from the URL, so formatting and `IStringLocalizer<T>` follow it. The document's `<html lang>` follows the locale too.
- An unsupported locale renders the not-found page in the default culture. Any other endpoint whose route has the locale parameter answers a bare 404 for one, so give a parameter that is not a locale another name.
- A supported locale in another case (`/EN/about`) reaches loaders and `params.lang` as configured (`en`).
- Only the URL decides. Pages outside `[lang]` get the default culture, not one from a cookie or `Accept-Language`, so an output-cached page cannot serve one visitor's language to everyone. Parent-culture fallback is off: list `nl-BE` as well as `nl` if both should match a request culture provider you add yourself. `MapLocaleRedirect` still sends an `nl-BE` browser to `/nl`.
- `NetjesLocalizationOptions.RouteParameter` names another parameter than `lang`.

[Redirects, rewrites and headers](redirects-rewrites-headers.md) shows how to send translated strings to the client through shared props.

## Endpoints next to pages

The app's own endpoints share URLs with its pages: an API under `/api`, a file such as `/robots.txt`, or a Markdown copy of each docs page at `/docs/routing.md` next to `app/docs/[...path]/page.tsx`. For a request, ASP.NET Core takes the endpoints whose routes match the URL and ranks them:

1. By order, lowest first. Pages, page actions and the endpoints an app maps with `MapGet`, `MapPost` and the like have order 0; `.WithOrder(-1)` puts an endpoint ahead of them.
2. By route precedence, segment by segment from the first: a literal (`search.json`) ranks first, then a parameter with a constraint (`{id:int}`), a parameter without one, a catch-all with a constraint and, last, a catch-all without one. A page's `[slug]` is a parameter without a constraint, `[...slug]` a catch-all with one (`minlength(1)`), and `[[...slug]]` a catch-all without one.
3. By HTTP methods, then hosts, then accepted content types: an endpoint that names its HTTP methods ranks ahead of one that names none, then one that requires a host (`RequireHost`) ahead of one that does not, and then one that lists the content types it accepts (because it binds a form or a JSON body) ahead of one that takes every content type. Pages answer GET and HEAD, and a page action POST.

The first endpoint in that order whose route constraints, host, method and content type fit the request answers it. When two endpoints rank first together and both fit, ASP.NET Core cannot choose: it throws an `AmbiguousMatchException`, and the request answers 500. Netjes warns at startup about an endpoint that ties with a page or a page action ([An endpoint that ties with a page](#an-endpoint-that-ties-with-a-page)).

`MapPages()` also adds a fallback, last in that order, that answers `GET` and `HEAD` with the not-found page for every URL without a file extension that no other endpoint takes. An `app.MapFallback(handler)` of the app's own takes the same URLs but names no HTTP method, so it ranks after Netjes's fallback: it gets the requests with other methods, such as a `POST`, and never a `GET` or a `HEAD`. To change what an unknown URL shows, write `app/not-found.tsx`.

A file next to a catch-all page is the common case. Its route, a literal and then a catch-all with a constraint, ranks the same as the page's `[...path]`, so give it `.WithOrder(-1)`:

```csharp
// app/docs/[...path]/page.tsx renders /docs/routing; this endpoint answers /docs/routing.md.
app.MapMethods("/docs/{**path:regex(\\.md$)}", [HttpMethods.Get, HttpMethods.Head],
        (string path, DocsStore docs) => docs.Markdown(path[..^".md".Length]) is { } markdown
            ? Results.Text(markdown, "text/markdown; charset=utf-8")
            : Results.NotFound())
    .WithOrder(-1);
```

The endpoint then answers every URL its constraint allows, and the page every other URL below `/docs/`. `MapMethods` with GET and HEAD answers both, as a page does; with `MapGet` alone, the page would get the HEAD requests for the file.

A `<Link>` to such a URL still works, but the router first asks the server for a page, gets the file, and then has the browser load the URL in full: one request more. Link it with `<Link reloadDocument href="/docs/routing.md">`, which the browser follows with a full page load and which gets the base path, or with `<a href={useHref('/docs/routing.md')}>`. A plain `<a href="/docs/routing.md">` misses the base path when the app has one ([Base path](#base-path)).

In Next.js, a route handler cannot share a folder with a page, so a Markdown URL below `app/docs/[...path]` takes a rewrite in `next.config.js` to a route handler at another path. Here the endpoint's order does it.

Such an endpoint is not a page to the output cache either: it gets no `Cache-Control` from Netjes, and `RevalidatePath` does not evict it ([Endpoints that are not pages](caching.md#endpoints-that-are-not-pages)).

### HEAD requests

Pages answer a `HEAD` as they answer a `GET`, but without the body, and so do the `GET` endpoints Netjes maps: `/sitemap.xml` and `/robots.txt` (`MapSitemap` and `MapRobotsTxt`), the image endpoint `/_netjes/image`, and the locale redirect at `/` (`MapLocaleRedirect`). Files in `public/` (the client build in production) answer `HEAD` too. The exception is `/_netjes/antiforgery`, which answers `GET` only: it is there to send the client router a token in its body, which a `HEAD` would not get, and the router asks for it with `GET`. A `HEAD` there gets the not-found page.

An endpoint you map with `MapGet` answers `GET` only, as in any ASP.NET Core app. A `HEAD` to its URL goes to whatever else matches the URL and takes `HEAD`:

- a dynamic page whose pattern matches it, such as `app/[slug]/page.tsx` for `/feed`. The page answers as it answers a `GET` of that URL: 200 with its HTML, or its not-found page when its loader returns `Page.NotFound()`, or when the page is in a `[lang]` folder and that segment of the URL is not a supported locale;
- otherwise, for a path without a file extension, the fallback that `MapPages()` adds, which answers with the not-found page (404, HTML);
- otherwise nothing, and the answer is a 404 without a body.

A `POST` to that URL gets 405 with `Allow: GET, HEAD`, although the endpoint does not answer `HEAD`: `Allow` also lists the methods of the not-found fallback, which takes `GET` and `HEAD`. For the same reason, a request with a method that no endpoint for its URL takes gets 405 rather than 404, with `GET` and `HEAD` in `Allow`, whatever the URL: a `POST` to `/nothing/here` does, and so does one to a path with a file extension, such as `/other/z.txt`, which the fallback never answers.

To answer `HEAD` from an endpoint of your own, such as a feed that crawlers and link checkers ask for, map it for both methods:

```csharp
app.MapMethods("/feed.xml", [HttpMethods.Get, HttpMethods.Head], (FeedStore feed) =>
    Results.Text(feed.ToXml(), "application/rss+xml"));
```

The handler runs for both, and the server sends the answer to a `HEAD` without its body. A [JSON endpoint](data-loading.md#json-endpoints) mapped this way keeps one key in the generated `api` map, `GET` and its route (`api['GET /api/status']`), and gets none for `HEAD`. Next.js answers `HEAD` for every route handler that exports `GET`; Netjes leaves the app's own endpoints to ASP.NET Core, as it leaves their routing ([ADR 0002](../decisions/0002-server-owns-routing.md)).

Because Netjes's own endpoints answer `HEAD` themselves, a `HEAD` for `/sitemap.xml` or `/robots.txt` in a localized app reaches the sitemap or `robots.txt`. If they answered `GET` only, `app/[lang]/page.tsx` would take `sitemap.xml` for a locale it does not support and answer with the not-found page.

## Route warnings

Some mistakes in routes and actions fail without an error, or only when a request meets them, so Netjes checks for them once the app has started, in every mode, and logs a warning for each one it finds under the category `Netjes.Routing.RouteDiagnostics`. `netjes routes` prints the same warnings after the route table. They never stop the app or fail a command. The check names only what is likely wrong, not every near name, overlap or positional record ([ADR 0015](../decisions/0015-route-warnings-name-what-is-likely-wrong.md) for the loader and overlap checks, [ADR 0029](../decisions/0029-an-endpoint-that-ties-with-a-page-is-a-route-warning.md) for endpoints that tie with pages, [ADR 0017](../decisions/0017-a-form-input-without-fields-is-empty-not-null.md) for form inputs).

### A loader parameter that reads the query string instead of a route value

Loaders bind like minimal APIs: a string or `TryParse` parameter with no binding attribute takes the route value of the same name, in any case, and otherwise the query string. That means `(string id)` on `/blog/[slug]` gets `?id=`, never the slug, and `(string[] slug)` on `/docs/[...slug]` gets `?slug=`, because a route value binds only to a single value (a catch-all's value is its segments joined by `/`).

```csharp
// Warns: 'id' binds from the query string and route parameter 'slug' is bound by nothing.
app.MapPageLoader("/blog/[slug]", (string id, PostStore posts) => ...);

// Fine: the parameter is named after the route value.
app.MapPageLoader("/blog/[slug]", (string slug, PostStore posts) => ...);
```

The warning names the page or layout file, the query parameters and the route parameters nothing binds, once per loader. If a query parameter has a near name of an unbound route parameter (two edits or fewer, like `slg` for `slug`, or for an array the route parameter's own name in any case), only that one is named, in a warning of its own; the others are taken for ordinary query parameters. There is no warning when every route parameter is bound, by the loader itself or by a layout loader above it: `(string id, string? q)` on `/projects/[id]` is fine, and so is `(int? page)` on `/[locale]/blog` when the loader of `app/[locale]/layout.tsx` takes `locale`. A page loader mapped in a route group whose prefix has a parameter (`app.MapGroup("/{tenant}").MapPageLoader("/projects", ...)`) has the group's route parameters as well: `(string tenant)` or `[FromRoute] string tenant` binds that route value, and a warning names the URL as mapped, `/{tenant}/projects`. The page then answers at `/acme/projects`, while the generated `Href` type, `route()` and `isHref` know it by its file's URL, `/projects`, so a URL segment such as a tenant belongs in the file tree instead (`app/[tenant]/projects/page.tsx`). Parameters with a binding attribute (`[FromQuery]`, `[FromRoute]`, `[FromHeader]`, `[FromServices]`, `[AsParameters]`), services, `HttpContext`, `ClaimsPrincipal`, `CancellationToken` and types that bind themselves (`BindAsync`) are never named. A parameter that reads the request body, such as a complex type with no binding attribute or a `[FromBody]` one, is not a warning either: it stops the app at startup ([Binding](data-loading.md#binding)).

A layout loader binds only the route values of its own URL. A query parameter of a layout loader that has the name of a route parameter of a page below it gets a warning of its own: `MapLayoutLoader("/projects", (string id) => ...)` above `app/projects/[id]/page.tsx` gets `?id=`, never the project's id. Read that value in the page's loader ([Layout loaders](data-loading.md#layout-loaders)).

To fix it, name the parameter after the route value (or bind it with `[FromRoute(Name = "slug")]`), or mark it `[FromQuery]` when the query string is what you mean.

### Two pages that match the same URLs

`app/[id]/page.tsx` next to `app/[...rest]/page.tsx`: `/x` goes to `[id]`, and the catch-all only gets URLs of two or more segments. The same holds for `app/docs/page.tsx` next to `app/docs/[[...slug]]/page.tsx` (`/docs` goes to the page) and for `app/en/[slug]/page.tsx` next to `app/[lang]/about/page.tsx` (`/en/about` goes to `en/[slug]`). The warning names both files, the URLs they share and the one ASP.NET Core's route precedence picks.

Pages that cannot match one URL get no warning: literals that differ, or a catch-all next to a page without that segment (`app/docs/page.tsx` and `app/docs/[...slug]/page.tsx`, since `[...slug]` needs at least one segment). Neither does a page that is another with literals in place of some parameters, such as `app/blog/new/page.tsx` next to `app/blog/[slug]/page.tsx`: the literal wins, as in Next.js.

To fix it, rename or move one of the pages so the patterns do not overlap. For a page next to an optional catch-all, drop the page and handle the empty case in the catch-all, or make the catch-all a required `[...slug]`. Two pages of one shape, such as `app/[lang]/about/page.tsx` and `app/[slug]/about/page.tsx`, are not a warning but an error that stops the app at startup, and so are two files for one convention in one folder (`page.tsx` and `page.jsx`).

### An endpoint that ties with a page

An endpoint of the app that ranks the same as a page or a page action for some URLs makes ASP.NET Core answer those requests with 500 ([Endpoints next to pages](#endpoints-next-to-pages)). The warning names the endpoint's HTTP methods and route, the page file, or the page's action, and the URLs they share:

```text
GET /docs/{**path:regex(\.md$)} and the page app/docs/[...path]/page.tsx both match URLs like /docs/[...path] with the same order and route precedence, so ASP.NET Core cannot choose between them and answers those requests with 500 (AmbiguousMatchException). Give the endpoint .WithOrder(-1) if it should answer them, or change its route.
```

An endpoint is warned when all of these hold for it and the page or its action: an HTTP method in common; the same order and the same route precedence; the same literals, in any case; a host in common, as written, or no host required by either; and accepted content types listed by both, or by neither. So `app.MapGet("/About")` next to `app/about/page.tsx` is warned, as are `app.MapGet("/blog/{id}")` next to `app/blog/[slug]/page.tsx`, `app.MapPost("/contact", () => ...)` next to a page action of `app/contact/page.tsx` that binds no form input, and `app.MapLocaleRedirect()` next to `app/page.tsx`. Two that both list content types tie for a request without a body, which has no content type, even when they accept different ones: next to a page action that binds a form, a `MapPost` that binds JSON gets the JSON posts and the page action the form posts, but a POST without a body answers 500.

An endpoint that ranks ahead of the page or after it, or that no request reaches together with it, is not warned: a literal such as `/docs/search.json` next to `[...path]`, an endpoint with `.WithOrder(-1)`, one with other HTTP methods than the page, one that requires a host where the pages require none (or the reverse), one whose host differs from the pages' only in case, which ASP.NET Core keeps apart, and one that answers every method (`app.Map`), which ranks after the page for GET and HEAD, so the page answers those. Hosts are compared as written, so a host wildcard next to one it covers (`*.example.com` and `*.shop.example.com`), which can tie, is not warned. Some warnings name endpoints that never fail: route constraints are not compared, so `/{org:alpha}/projects` is warned next to a page loader mapped in `app.MapGroup("/{tenant:int}")`, though no URL fits both; a matcher policy of the app's own can tell an endpoint apart from a page, as API versioning can; and two endpoints that list different content types fail no request when a third endpoint for the URL takes every content type, such as the app's `MapFallback`, which then gets the posts without a body.

To fix it, give the endpoint `.WithOrder(-1)` if it should answer those URLs, or change its route. If the page should answer them, give the endpoint an order above 0, such as `.WithOrder(1)`.

### A form input that refuses a post without a checkbox

A browser leaves an unchecked checkbox out of the post it sends, and a list with nothing checked or chosen (checkboxes of one name, a `<select multiple>`). ASP.NET Core's form binding builds a positional record only from every one of its constructor's fields, so it refuses such a post with 400 before the action runs. A server action or page action that binds a `[FromForm]` positional record whose constructor takes a `bool`, a `bool?` or a list gets a warning naming the action (the page file, for a page action), the type and those fields:

```csharp
// Warns: a post without 'Pinned', as for an unchecked box, is refused.
public sealed record PinInput([Required] string? Text, bool Pinned);

// Fine: a missing field keeps its default.
public sealed record PinInput
{
    [Required] public string? Text { get; init; }
    public bool Pinned { get; init; }
}
```

A positional record of text fields only gets no warning, since a browser posts a text field even when it is empty, and neither does a JSON input. To fix it, give the type `init` properties, or bind the checkbox as a parameter of its own (`[FromForm] bool? stay`) ([Validation](actions-and-forms.md#validation)).

### Hiding the warnings

There is no option to turn the check off. For an overlap you keep on purpose, a route value only middleware reads, or an endpoint that a matcher policy of your own tells apart from a page, raise the category's log level in `appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Netjes.Routing.RouteDiagnostics": "Error"
    }
  }
}
```

That hides every kind, and the warning logged when the check itself cannot run. The check sees the routes as they are at startup, so a page added while the development server runs is checked on its next start.

## The route table

`netjes routes` (or `npm run routes` in an app made with `netjes new`) prints the pages as ASP.NET Core maps them: pattern, file, loader, page action, props type, output caching and authorization, then the other endpoints and the route warnings. `--json` prints the same as JSON. See [the netjes command](../reference/cli.md#netjes-routes).

App code and tests can read the same from the endpoints, without running the command. Every endpoint that serves a page carries `PageRouteMetadata`: its `Pattern` is the page's pattern in the report, and the report's loader is the `IsLoader` of the page's `GET` endpoint, true when `MapPageLoader` maps it. The report's output caching is true when that endpoint carries an output-cache policy, from `CachePage`, `.CacheOutput(...)` or an `[OutputCache]` attribute, while `CachedPageMetadata`, with the duration and tags, is only on the endpoints `CachePage` is put on. A base policy (`options.AddBasePolicy(...)`) applies to requests and is on no endpoint, so a page that only a base policy caches is listed as not cached. See [endpoint metadata](../reference/csharp-api.md#endpoint-metadata).

## In development and production

In development Netjes reads the page files from `app/` and reads them again when they change, so a new page answers on the next request and the browser picks up edits through Vite's hot module replacement. In production the routes come from the Vite build's manifest, so a page file that was not built does not exist.

## What is not there

- **Route handlers.** A `route.ts` file in `app/` stops the app in development and fails `vite build` (and so `netjes build` and `dotnet publish`), with a message saying what to do instead: an API is a C# minimal API endpoint, mapped next to the pages (`app.MapGet`, `app.MapPost`; [Endpoints next to pages](#endpoints-next-to-pages)). Its JSON shape reaches the client through the generated `api` schemas ([JSON endpoints](data-loading.md#json-endpoints)).
- **The pages router.** Only `app/` is routed ([ADR 0010](../decisions/0010-app-router-with-csharp-layout-loaders.md)). An app with Next.js pages router files in a `pages/` folder stops at startup, and the message says what to do with each:
  - A page moves to its `app/` name: `pages/about.tsx` to `app/about/page.tsx`, `pages/index.tsx` to `app/page.tsx`. A `.js` page moves to a `.jsx` file, since `app/` routes `.tsx` and `.jsx` only: `pages/index.js` to `app/page.jsx`.
  - `pages/404.tsx` moves to `app/not-found.tsx`, and `pages/500.tsx` and `pages/_error.tsx` to `app/error.tsx`.
  - `pages/_app.tsx` and `pages/_document.tsx` move to `app/layout.tsx`, the root layout, which wraps every page. Netjes renders `<html>`, `<head>` and `<body>` around it, so the head tags of `_document.tsx` go in [metadata](metadata.md), or in `<title>`, `<meta>` and `<link>` tags the layout renders, and `<html lang>` comes from [`NetjesOptions.Lang`](metadata.md#the-lang-attribute).
  - An API route in `pages/api/` becomes a C# minimal API endpoint (`app.MapGet`, `app.MapPost`), like a route handler.

  `404`, `500`, `_error`, `_app` and `_document` count at the root of `pages/` only, as in Next.js: `pages/blog/404.tsx` is a page, `app/blog/404/page.tsx`. The check looks at `.tsx`, `.jsx` and `.js` pages and at `.ts`, `.tsx`, `.js` and `.jsx` files in `pages/api/`. It skips tests, declaration files, anything in a `_` folder, other files whose names start with `_`, and `.ts` files outside `pages/api/`, which cannot hold JSX. The message names every special file and up to five other files, and counts the rest. [`netjes info`](../reference/cli.md#netjes-info) checks the `pages/` folder in the app's root by the same rule, flagging the same files but listing up to three pages and three API routes.
- **Intercepting folders outside a slot.**
- **Redirects, rewrites and headers in configuration.** These are ASP.NET Core middleware: see [Redirects, rewrites and headers](redirects-rewrites-headers.md).
