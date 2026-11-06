# React API

This is the reference for the npm package `netjes`: every export of every entry point, with its signature and what it does, for app developers looking up an API. The guides explain how the pieces fit: [Routing](../guides/routing.md), [Data loading](../guides/data-loading.md), [Actions and forms](../guides/actions-and-forms.md), [Images](../guides/images.md), [Metadata](../guides/metadata.md) and [Testing](../guides/testing.md). The C# side is in the [C# API](csharp-api.md).

## Entry points

| Import | What it holds |
|---|---|
| [`netjes`](#netjes) | Components, hooks and functions for pages: links, forms, server actions, images, scripts, metadata types, and the hashes a Content-Security-Policy lists for Netjes's inline scripts |
| [`netjes/zod`](#netjeszod) | `deferred()`, the zod schema for `Page.Defer` values |
| [`netjes/testing`](#netjestesting) | `createTestApp()`, the harness for component and page tests, and `renderDocument()` and `inlineScriptHashes()` for a page's whole document |
| [`netjes/testing/setup`](#netjestestingsetup) | A Vitest setup file that unmounts what each test rendered |
| [`netjes/vitals`](#netjesvitals) | Core Web Vitals: `useReportWebVitals()` and `<WebVitals />` |
| [`netjes/vite`](#netjesvite) | The `netjes()` Vite plugin |
| [`netjes/client-entry`, `netjes/server-entry`, `netjes/sidecar`](#internal-entry-points) | Used by the plugin and the .NET app, not by app code |

The package also has the `netjes` command ([the netjes command](cli.md)). The app's own `generated/netjes.ts` is described under [The generated module](#the-generated-module).

## `netjes`

### Pages and layouts

#### `definePage`

```ts
function definePage<Schema extends StandardSchemaV1>(
  schema: Schema,
  Page: ComponentType<StandardSchemaV1.InferOutput<Schema>>,
): FunctionComponent<Record<string, unknown>>;
```

Declares a page whose props, sent by its C# loader, are validated before it renders. The component gets the parsed, typed props. Props the schema rejects throw `Invalid props: <issues>` to the nearest error boundary. Use the generated schema: `export default definePage(pages['/blog/[slug]'], function Post({ post }) { ... })`. Any [Standard Schema](https://standardschema.dev) works (zod, valibot, arktype), as long as it validates synchronously.

#### `defineLayout`

```ts
function defineLayout<Schema extends StandardSchemaV1>(
  schema: Schema,
  Layout: ComponentType<StandardSchemaV1.InferOutput<Schema> & LayoutProps>,
): FunctionComponent<LayoutProps & Record<string, unknown>>;
```

The same for a layout with a C# layout loader (`MapLayoutLoader`): its props are validated (`Invalid layout props: ...`) and the component gets them next to `children` and `params`. Use the generated schema: `defineLayout(layouts['app/docs'], function DocsLayout({ toc, children }) { ... })`.

#### `LayoutProps`

```ts
type LayoutProps = { children: ReactNode; params: Params };
```

The props of every layout component. A layout with parallel routes also gets each slot as a prop named after its folder ([Parallel routes](../guides/routing.md#parallel-routes)).

#### `ErrorPageProps`

```ts
type ErrorPageProps = { error: Error & { status?: number; withheld?: boolean }; reset: () => void };
```

The props of an `error.tsx`: what was thrown, and `reset()` to try again. `error.status` is the HTTP status of a server answer behind the error. `error.withheld` is true when `error.message` is a stand-in that Netjes wrote in place of a message it does not show, so an error page renders the message only when `!error.withheld`. `reset()` renders the segment again after an error a segment's boundary caught; on an error page the server rendered it loads the page from the server again (`router.refresh()`); on the page the router's own boundary shows it does nothing, so offer a link or a full reload there. What the error holds depends on where it came from:

- **The server rendered the error page** for an exception in a loader, an action without JavaScript or the render: `status` is the response's status, 500 or a `BadHttpRequestException`'s. With error details on ([`IncludeErrorDetails`](options.md#rendering-and-the-sidecar), on in the Development environment), `message` and `stack` are the exception's. With them off, `message` is the status's reason phrase, such as `Internal Server Error`, `withheld` is true and `stack` is undefined.
- **The server rendered the error page for a status alone**, such as `Page.Forbidden()` in a folder without a `forbidden.tsx`, or an empty 400 from binding: `message` is the status's reason phrase, such as `Forbidden`, in every environment, and it is not withheld. `stack` is undefined.
- **An error thrown in the browser**, caught by a segment's `error.tsx`, or by the router's own boundary, which hands it to the root `app/error.tsx`: the error as it was thrown, with its own class, message and stack, and no `status` unless it is a `ServerActionError`. A thrown string becomes an `Error` with that text. Any other thrown value becomes an `Error` with the message `Something went wrong`, and `withheld` true.
- **A server action that failed**: a [`ServerActionError`](#serveractionerror), with the status of the server's answer, withheld when the server sent no message.
- **A `Page.Defer` value that failed**, read with `use()`: with error details on, the exception's message; with them off, `Something went wrong`, and `withheld` true. An exception whose message is empty reads as withheld in any environment. A value that did not arrive within `StreamTimeout` (`The value did not arrive in time`), one whose task was cancelled (`The value was cancelled`) and one whose navigation stream ended first carry Netjes's own message, which is not withheld.

`withheld` is not a development switch. It says whether this message stands in for another, so in production an error thrown in the browser keeps its own message, and the reason phrase for a status alone is not withheld; in development, with error details on, the server withholds nothing. Netjes sets it only on the errors it builds, as it does `status`, but an error class of the app's own with a `withheld` property of its own reads the same way.

### Navigation

#### `Link`

```ts
function Link(props: LinkProps): JSX.Element;

type LinkProps = Omit<ComponentPropsWithRef<'a'>, 'href'> & {
  href: Href;
  replace?: boolean;            // replace the history entry instead of adding one
  scroll?: boolean;             // default true: scroll to the top, or to the #fragment and focus it
  prefetch?: 'intent' | 'none'; // default 'intent': fetch the page on hover, focus and touch
  reloadDocument?: boolean;     // let the browser load the URL with a full page load
};
```

An `<a>` that navigates on the client; a `ref` gets the `<a>` element. It puts its `href` under the base path, and sets `aria-current="page"` when it goes to the page on screen: the same path, with one trailing slash ignored, and the page's values for every query parameter the link names, in any order. Parameters the link leaves out do not count, and a link with a `#` or with a scheme or host of its own is never marked. An `aria-current` prop wins, `undefined` included. Modified clicks, other mouse buttons, a `target` other than `_self`, `download` and URLs on another origin behave like a plain link. After a navigation to another page, the router announces the page in a live region and starts the focus over at the top of the document; the element a `#fragment` names takes the focus instead, and `scroll={false}` keeps it on the link while the link has it. See [Links](../guides/routing.md#links) and [Focus and announcements](../guides/routing.md#focus-and-announcements).

#### `useRouter`

```ts
function useRouter(): Router;

type Router = {
  push(href: Href, options?: { scroll?: boolean }): Promise<void>;
  replace(href: Href, options?: { scroll?: boolean }): Promise<void>;
  prefetch(href: Href): void;
  refresh(): Promise<void>;
  back(): void;
  forward(): void;
};
```

The client router. `push` and `replace` navigate, adding or replacing a history entry. `prefetch` loads a page ahead, as a link does on hover. `refresh` loads the page on screen from the server again in place: every loader runs, in the same history entry, with the URL's fragment, without scrolling. `back` and `forward` go through history, showing remembered pages without a request. The promises of `push`, `replace` and `refresh` resolve once the router has handed the new page to React, or once the navigation has ended another way (another navigation took its place, a full page load, a refused URL), so the old page can still be on screen then; inside a transition of the app's own, such as a form action, React shows the new page once that transition has ended. Code that needs the new page runs in an effect ([`useRouter`](../guides/routing.md#userouter)). Throws outside a Netjes page.

#### `useNavigation`

```ts
function useNavigation(): NavigationState;

type NavigationState =
  | { state: 'idle'; error?: SubmitError }
  | { state: 'loading' | 'submitting'; location: string };

type SubmitError = { status: number | null; action: string };
```

The router's state: `'loading'` during a navigation and `'submitting'` during a form post, with the path and query on their way (without the base path). After a form post that did not produce a page, `'idle'` carries `error` until the next navigation: the status of the error response, or `null` when nothing answered, and the URL posted to.

#### `usePathname`

```ts
function usePathname(): string;
```

The current path, without the base path: `/blog/hello`.

#### `useSearchParams`

```ts
function useSearchParams(): URLSearchParams;
function useSearchParams<Schema extends StandardSchemaV1>(schema: Schema): StandardSchemaV1.InferOutput<Schema>;
```

The query string. With a schema, it parses an object of the values (a string each, or an array of strings for a key that repeats) and drops the values the schema rejects, so its defaults and optional fields apply; only a value that stays invalid without them throws. Define the schema at module level. On a page that `netjes export` wrote, it sees no query string, whatever the address bar shows; [static export](../guides/static-export.md#in-the-browser) says what to read there instead.

#### `useParams`

```ts
function useParams(): Params;
function useParams<Schema extends StandardSchemaV1>(schema: Schema): StandardSchemaV1.InferOutput<Schema>;
```

The page's route values, catch-all values as arrays (empty for an optional catch-all). A single parameter the request has no value for is `null`, as when a page is rendered with `Page.Render` from an endpoint whose route lacks it. With a schema, parsed by it; a rejected value throws `Invalid route params: ...` to the error boundary, so constrain route values in C#. Define the schema at module level.

#### `useSelectedLayoutSegment`

```ts
function useSelectedLayoutSegment(): string | null;
```

In a layout: the URL segment one level below it, decoded, such as the active tab; `null` at the layout's own page.

#### `useSelectedLayoutSegments`

```ts
function useSelectedLayoutSegments(): string[];
```

In a layout: every URL segment below it, decoded. Route groups do not appear, since they are not in the URL. Outside a layout, the whole path.

#### `useBasePath`

```ts
function useBasePath(): string;
```

The base path the app is mounted under with `UsePathBase`, or `''` at the root. For URLs in your own markup that Netjes does not prefix, such as `<link rel="icon" href={`${basePath}/favicon.svg`}>`.

#### `useHref`

```ts
function useHref(href: string): string;
```

`href` under the base path, for a plain `<a href>` or another attribute: what `<Link>` does for its own. Root-relative paths get the base path unless they already start with it; other URLs are returned as they are.

#### `useLocale`

```ts
function useLocale(): string | null;
```

The request culture the page was rendered in with `AddNetjesLocalization`, such as `'nl'`; `null` without localization. See [Locales](../guides/routing.md#locales).

### Forms and server actions

#### `Form`

```ts
function Form(props: FormProps): JSX.Element;

type FormProps = Omit<ComponentPropsWithRef<'form'>, 'method' | 'action'> & {
  method?: 'get' | 'post';        // default 'get'
  action?: string | FormAction;   // default: the current URL
  replace?: boolean;              // for a GET form: replace the history entry
  reloadDocument?: boolean;       // let the browser submit it, with a full page load
};

type FormAction = (formData: FormData) => void | Promise<void>;
```

A `<form>` that submits without a full page load and treats the answer like a navigation. A GET form navigates to its action with the fields as the query string. A POST form posts with `fetch`, adds the antiforgery token, follows a redirect or shows the page the server rendered, and leaves a response that is not a page as it is (an HTML document is shown, an attachment downloaded, a failure reported through `useNavigation()`). With a function `action`, React runs it, so `useFormStatus` works; for `useAction`'s `formAction` the server renders a real post to the action, so the form works without JavaScript too. A `ref` gets the `<form>` element. See [`<Form>` in detail](../guides/actions-and-forms.md#form-in-detail).

#### `SubmitButton`

```ts
function SubmitButton(props: SubmitButtonProps): JSX.Element;

type SubmitButtonProps = Omit<ComponentPropsWithRef<'button'>, 'type'>;
```

A submit button whose `formAction` is rendered under the base path, so it posts to the right place with and without JavaScript. A `ref` gets the `<button>` element.

#### `serverAction`

```ts
function serverAction<TInput = FormData, TResult = unknown>(
  name: string,
  options?: { result?: StandardSchemaV1<unknown, TResult> },
): ServerAction<TInput, TResult>;

interface ServerAction<TInput, TResult> {
  (input: TInput | FormData): Promise<ActionState<TResult>>;
  readonly actionName: string;
  readonly url: string; // /_netjes/actions/{name}
}
```

A reference to a C# server action (`app.MapAction(name, ...)`). Call it with a plain object, sent as JSON, or with `FormData`, sent as a form post; pass it to `useAction` for a form. With `options.result`, the result is parsed by that schema. A 422 resolves to `{ ok: false, ... }`; any other failure throws a `ServerActionError`. A call made as the page starts waits up to 10 seconds for the router; with no router on its way, as on the server or in a test outside `createTestApp`'s `Wrapper`, it throws at once ([calls as the page starts](../guides/actions-and-forms.md#calls-as-the-page-starts)). The generated `serverActions` map holds a typed reference, made with this function, for every action.

#### `useAction`

```ts
function useAction<TInput, TResult>(
  action: ServerAction<TInput, TResult>,
  options?: UseActionOptions<TResult>,
): [state: ActionState<TResult> | null, formAction: (formData: FormData) => void, isPending: boolean];

type UseActionOptions<TResult> = {
  initialState?: ActionState<TResult> | null; // default null
  onSubmit?: (formData: FormData) => void;    // runs inside the action's transition, before the call
};
```

React's `useActionState` for a server action. Pass `formAction` to `<Form action>`. After a failed post without JavaScript, `state` starts as that failure, with the submitted values. `onSubmit` is where `useOptimistic` updates go: React keeps them until the action and the page refresh after it have finished. See [`useAction` and `<Form>`](../guides/actions-and-forms.md#useaction-and-form).

#### `ActionState`

```ts
type ActionState<TResult> =
  | { ok: true; result: TResult }
  | { ok: false; status: number; errors: Record<string, string[]>; values?: Record<string, string | string[]> };
```

The value a server action call resolves to: its result, or the validation errors per field (status 422) and, for a form, the submitted values of the fields the action declares. An action that answers `ServerAction.Redirect(...)` or `Ok()` resolves with `result` undefined, whatever its type says.

#### `usePageActionState`

```ts
function usePageActionState(): PageActionFailure | null;

type PageActionFailure = { ok: false; status: number; errors: Record<string, string[]>; values: Record<string, string | string[]> };
```

After the input of the page's action (`MapPageAction`) failed its DataAnnotations or `IValidatableObject` checks: the errors per field (status 422) and the submitted values of the fields the action declares, in the shape of `ActionState`'s failure. The page has been rendered again from its loader, with or without JavaScript, so a field takes its value with `defaultValue={String(failed?.values.name ?? '')}`. Null on every other render, also after a server action's failure, which `useAction` reads. See [validation in page actions](../guides/actions-and-forms.md#validation-in-page-actions).

#### `ServerActionError`

```ts
class ServerActionError extends Error {
  name: 'ServerActionError';
  readonly status: number;
  readonly withheld: boolean;
}
```

Thrown by a server action call when the server could not run the action or refused it (403, 404, 413, 429, 500, a refused token), as opposed to validation errors. Uncaught in a transition or an action, it reaches the nearest error boundary. Catch it to handle a status on the form, as TaskBoard does for 429.

`status` is the status of the server's answer. `message` is the message the server sent: the exception's (for a 4xx always, for a 5xx only with error details on), or the reason phrase for a call the server refused, such as `Forbidden`. `withheld` is true when the server sent no message, as for a 5xx with error details off; `message` is then `Server action <name> failed with status <status>`. An answer that is not JSON, such as a proxy's 502 page, gets that message too, but is not withheld.

### Server data

#### `useSharedProps`

```ts
function useSharedProps(): unknown;
function useSharedProps<Schema extends StandardSchemaV1>(schema: Schema): StandardSchemaV1.InferOutput<Schema>;
```

The props every page and layout gets from `options.UseSharedProps(...)` in C#. Pass the generated `sharedProps` schema to get them parsed and typed (`Invalid shared props: ...` when they do not match); without a schema they are `unknown`.

#### `usePublicConfig`

```ts
function usePublicConfig(): unknown;
function usePublicConfig<Schema extends StandardSchemaV1>(schema: Schema): StandardSchemaV1.InferOutput<Schema>;
```

The `Netjes:PublicConfig` configuration section, sent with the first document: keys camelCased, values strings. Parse it with a coercing schema (`z.coerce.number()`, `z.stringbool()`); a mismatch throws `Invalid public config: ...`. See [Public configuration](../guides/data-loading.md#public-configuration).

#### `useDraftMode`

```ts
function useDraftMode(): { isEnabled: boolean };
```

Whether the page was rendered in draft mode (`IDraftMode.Enable()` in C#), like Next.js's `draftMode()`. See [Caching](../guides/caching.md).

#### `useNonce`

```ts
function useNonce(): string | undefined;
```

The page's Content-Security-Policy nonce, for inline scripts and styles of your own; `undefined` without a policy, or under one without `{nonce}`. `<Script>` uses it by itself. See [Security](../guides/security.md).

### Content-Security-Policy

#### `CSP_SCRIPT_HASHES`

```ts
const CSP_SCRIPT_HASHES: string;
```

The `script-src` sources that allow Netjes's fixed inline scripts by their hashes, for a policy without a nonce: one `'sha256-…'` per script, in its quotes and separated by spaces, as in `` `script-src 'self' ${CSP_SCRIPT_HASHES}` ``. It is for a policy that JavaScript writes, such as a static host's headers for [exported pages](../guides/static-export.md#deferred-values-and-suspense); in C#, the same value is [`NetjesContentSecurityPolicy.ScriptHashes`](csharp-api.md#netjescontentsecuritypolicy). A fixed script has the same text in every document. In this version there is one, the bootstrap that collects streamed `Page.Defer` values; a Netjes version that changes it or adds such a script changes the value. The scripts that deliver `Page.Defer` values, and React's scripts that reveal Suspense boundaries, differ from render to render, so no hash covers them. React's script for form actions that are functions is left out too, since its text follows the React version. See [Security](../guides/security.md#hash-based).

### Components

#### `Image`

```ts
function Image(props: ImageProps): JSX.Element;

type ImageProps = Omit<ComponentPropsWithRef<'img'>, 'src' | 'srcSet' | 'width' | 'height' | 'loading' | 'alt'> & {
  src: string;           // a path in public/ or a URL on an allowed remote host
  alt: string;           // '' for a decorative image
  width: number;         // the intrinsic size in CSS pixels
  height: number;
  sizes?: string;        // for a responsive image: every configured width becomes a candidate
  quality?: number;      // 1-100, default 75, rounded to the nearest accepted quality
  priority?: boolean;    // load eagerly with high priority, and preload from the head
  unoptimized?: boolean; // serve src as it is
};
```

An `<img>` that reserves its space, loads lazily and offers resized copies from `/_netjes/image`: a 1x and a 2x copy for a fixed size, every configured width with `sizes`. SVGs, `data:` and `blob:` URLs are used as they are. A `ref` gets the `<img>` element. See [Images](../guides/images.md).

#### `Script`

```ts
function Script(props: ScriptProps): JSX.Element | null;

type ScriptProps = Omit<ScriptHTMLAttributes<HTMLScriptElement>, 'onLoad' | 'onError' | 'children' | 'dangerouslySetInnerHTML'> & {
  id?: string;                       // required for an inline script
  strategy?: ScriptStrategy;         // default 'afterInteractive'
  onLoad?: (event: Event) => void;
  onError?: (event: Event) => void;
  children?: string;                 // inline code, instead of src
};

type ScriptStrategy = 'beforeInteractive' | 'afterInteractive' | 'lazyOnload';
```

A script loaded by strategy, like `next/script`: `afterInteractive` and `lazyOnload` do not block the page. Each is added to the document once, by `id` or `src`, except inline `beforeInteractive` code, which the server writes wherever a component renders it. `afterInteractive` adds it after hydration, and `lazyOnload` once the page has loaded and the browser is idle; neither renders anything where it stands. `beforeInteractive` writes it into a server-rendered document, with a `<meta name="netjes-before-interactive">` in the head: inline code where the component stands, which the parser runs as it reaches it, and for a `src` a preload in the head and an inert entry where the component stands, which runs once the shell is parsed, in order with the others, before the page's modules run and the page hydrates. A script the browser will not run (`noModule`, a `text/plain` type) is neither preloaded nor waited for. Both the inline script and the entry carry `data-netjes-script`, and the entry is a `type="application/json"` script; the attribute and the meta's name are reserved. A `beforeInteractive` component that mounts in the browser adds its script as `afterInteractive` does. `onLoad` and `onError` are called after the component mounts, once an external script has loaded or failed, and `onLoad` once inline code has run. The page's CSP nonce is added when there is one. See [Third-party scripts](../guides/metadata.md#third-party-scripts-script) and [Code that runs before the first paint](../guides/metadata.md#code-that-runs-before-the-first-paint).

#### `dynamic`

```ts
function dynamic<P extends object>(
  load: () => Promise<{ default: ComponentType<P> } | ComponentType<P>>,
  options?: DynamicOptions,
): ComponentType<P>;

type DynamicOptions = {
  ssr?: boolean;           // default true; false renders `loading` on the server and while hydrating
  loading?: ComponentType; // shown while the module loads
};
```

Like `next/dynamic`: a component whose module loads on demand, in its own chunk, inside a Suspense boundary. `dynamic(() => import('./Chart.tsx'), { loading: Spinner })`. With `ssr: false` it renders only in the browser, for code that needs `window`. See [Rendering and streaming](../guides/rendering-and-streaming.md).

#### `clientOnly`

```ts
function clientOnly<P extends object>(Component: ComponentType<P>, options?: { fallback?: ReactNode }): ComponentType<P>;
```

A component that renders only in the browser, after hydration; `fallback` (default nothing) on the server and until then. The switch is hydration-safe.

### Metadata types

#### `Metadata`

```ts
type Metadata = {
  title?: MetadataTitle;
  description?: string;
  keywords?: string[];
  authors?: { name: string; url?: string }[];
  robots?: string | { index?: boolean; follow?: boolean };
  alternates?: { canonical?: string; languages?: Record<string, string> };
  openGraph?: {
    title?: string; description?: string; url?: string; siteName?: string; type?: string; locale?: string;
    images?: string | URL | MetadataImage | (string | URL | MetadataImage)[];
  };
  twitter?: {
    card?: 'summary' | 'summary_large_image' | 'app' | 'player';
    site?: string; creator?: string; title?: string; description?: string;
    images?: string | URL | MetadataImage | (string | URL | MetadataImage)[];
  };
  icons?: { icon?: string; apple?: string };
  themeColor?: string;
  other?: Record<string, string>;
};
```

The type of `export const metadata` in pages, layouts and special pages. See [Metadata](../guides/metadata.md) for how it is merged and which tags it writes.

#### `MetadataTitle`

```ts
type MetadataTitle = string | { default?: string; template?: string; absolute?: string };
```

A title, a layout's `template` (`'%s · Site'`) and `default`, or an `absolute` title that skips templates. A `default`, on a layout or a page, goes through the nearest `template` of the layouts above it like a string title, never through the one beside it; a layout's `default` is also the title of the pages below it that set none. See [Titles](../guides/metadata.md#titles).

#### `MetadataImage`

```ts
type MetadataImage = { url: string | URL; width?: number; height?: number; alt?: string };
```

An image for `openGraph.images` or `twitter.images`, which also take a plain URL (a string or a `URL`), and one image or an array of them. `url` is written as `og:image` or `twitter:image`; `width`, `height` and `alt` follow it as `og:image:width`, `og:image:height` and `og:image:alt`, when given; an empty `alt` or a zero `width` or `height` writes no tag. A Twitter image writes only `twitter:image:alt` besides its URL.

### Routes and hrefs

#### `Href`

```ts
type Href = Register extends { href: infer H extends string } ? H : string;
```

The type of the URLs `<Link href>` and the router accept: the app's routes once the generated file has registered them, any string before that. The generated file has an `Href` type and schema of its own for the same set.

#### `Register`

```ts
interface Register {}
```

Filled in by the generated file through declaration merging, which types `Href`: `declare module 'netjes' { interface Register { href: Href } }`.

#### `buildHref`

```ts
function buildHref(
  pattern: string,
  params: Record<string, unknown>,
  search?: SearchParamsInput,
  options?: { hash?: string | undefined },
): string;
```

The URL of a route pattern written in file syntax (`/blog/[slug]`, `/docs/[...path]`, `/shop/[[...filters]]`), with every segment value encoded and catch-all values taken as arrays, followed by the query from `search` and the fragment from `options.hash`. A missing or empty required value throws `netjes: route '<pattern>' needs the parameter '<name>'`. `hash` is the id of an element on the page: one leading `#` is dropped and the rest is percent-encoded (`'über-uns'` gives `#%C3%BCber-uns`); with nothing left, no fragment is added. For example, `buildHref('/docs/[...path]', { path: ['guides', 'caching'] }, undefined, { hash: 'keys' })` is `/docs/guides/caching#keys`. The generated `route()` calls it with typed parameters.

#### `buildEndpointUrl`

```ts
function buildEndpointUrl(
  template: string,
  values: Readonly<Record<string, unknown>>,
  search?: SearchParamsInput,
  endpoint?: string,
): string;
```

The URL of one of the app's endpoints from its route template as the generated file writes it: literals as they are, with `%`, `{`, `}`, `#` and `\` encoded, and `{name}`, `{name?}`, `{name=default}` (the default encoded), `{*name}` and `{**name}`, without constraints. Each route value is encoded with `encodeURIComponent`; a `{**name}` value keeps its slashes. A missing, `null` or `''` optional value drops its segment, or its part with the `.` before it; a missing value with a default is filled in, unless it is a whole segment at the end, which is left out (`/openapi/{doc=v1}.json` keeps `v1`); a missing, `null` or `''` required value, or an optional one whose segment is followed by one that stays (`x` in `/a/{x?}/b`, or in `/a/{x?}/{y?}` with `y` given), throws `netjes: endpoint '<endpoint>' needs the route value '<name>'` (the template when `endpoint` is not given). The values the template does not name are the query string, followed by `search`. The result is root-relative, without the base path: `buildEndpointUrl('/api/items/{id}', { id: 'a b', v: 2 })` is `/api/items/a%20b?v=2`. The generated `endpointUrl()` calls it with typed values ([Endpoint URLs](../guides/data-loading.md#endpoint-urls)).

#### `SearchParamsInput`

```ts
type SearchParamsInput = Record<string, SearchValue | readonly SearchValue[]>;
```

The query of `buildHref`, `route()`, `buildEndpointUrl` and `endpointUrl()`. An array repeats the key; `null` and `undefined` are left out.

#### `SearchValue`

```ts
type SearchValue = string | number | boolean | null | undefined;
```

One query value.

#### `isRouteHref`

```ts
function isRouteHref(value: string, patterns: readonly string[]): boolean;
```

Whether a URL is one of the app's pages (by route pattern), a link within the page (`?…`, `#…`), or an `http(s):`, `mailto:` or `tel:` URL. `//host` and `/\host` are not, and neither is any URL that holds an ASCII control character or starts or ends with a space: browsers drop those characters, so `/\t/evil.example` goes to `evil.example`. The generated `isHref()` and `Href` schema call it. A root-relative URL has to match a pattern, which a C# `Netjes.Href`, checked for its shape only, does not ask.

### Payload types

#### `Params`

```ts
type Params = Record<string, string | string[] | null>;
```

Route values: a string per single segment, an array per catch-all.

#### `PageKind`

```ts
type PageKind = 'notFound' | 'error' | 'forbidden' | 'unauthorized';
```

The reason the server rendered a boundary file instead of the route's page.

#### `SegmentRef`

```ts
type SegmentRef = {
  id: string;                  // the folder, e.g. app/blog
  layout?: string; template?: string; loading?: string; error?: string;
  props?: Record<string, unknown>;      // from the layout loader
  key?: string;                // identifies what the layout loader saw
  reused?: boolean;            // the loader was skipped; the client keeps its props
  slots?: Record<string, string | null>;
  depth?: number;
  slotParams?: Record<string, Params>;
};
```

A folder's segment files in the page payload, outermost first. Useful when inspecting payloads in tests.

#### `SeededQuery`

```ts
type SeededQuery = { key: readonly unknown[]; data: unknown; updatedAt: number };
```

A TanStack Query entry seeded by `.WithQuery(key, data)` in C#; `updatedAt` is in Unix milliseconds, on the server's clock.

#### `StandardSchemaV1`

```ts
interface StandardSchemaV1<Input = unknown, Output = Input> {
  readonly '~standard': {
    readonly version: 1;
    readonly vendor: string;
    readonly validate: (value: unknown) => StandardSchemaV1.Result<Output> | Promise<StandardSchemaV1.Result<Output>>;
    readonly types?: { readonly input: Input; readonly output: Output };
  };
}
```

The [Standard Schema](https://standardschema.dev) interface that zod, valibot, arktype and others implement, which every schema-taking API here accepts. Netjes validates synchronously: an async schema throws.

## `netjes/zod`

The only module that imports zod, so an app on another schema library does not need it.

#### `deferred`

```ts
function deferred<Schema extends z.ZodType>(schema: Schema): z.ZodType<PromiseLike<z.output<Schema>>, Promise<unknown>>;
```

The schema of a `Page.Defer` prop (`Deferred<T>` in C#): a thenable for React's `use()` whose value is validated by `schema` when it arrives. A value that does not match rejects with `Invalid deferred value: ...`, which reaches the error boundary. The generated schemas use it for every `Deferred<T>` property. It returns the same thenable for the same value and schema on every render, so `use()` does not suspend again.

#### `DeferredValue`

```ts
class DeferredValue<T> implements PromiseLike<T> {
  status: 'pending' | 'fulfilled' | 'rejected';
  value?: T;
  reason?: unknown;
  constructor(source: PromiseLike<unknown>, parse: (value: unknown) => T);
  then<R1 = T, R2 = never>(onFulfilled?: ..., onRejected?: ...): PromiseLike<R1 | R2>;
}
```

The value `deferred()` parses to: a thenable that records `status`, `value` and `reason` the way React's `use()` reads them. It is not a `Promise`, which would make zod treat the whole schema as async. A value nobody reads does not surface as an unhandled rejection.

## `netjes/testing`

The harness for testing components and pages with Vitest and Testing Library ([Testing](../guides/testing.md)). The router in it is a fake: it records what the component asked for and moves to the new URL, but loads nothing. `renderDocument` renders a page's whole document instead, as the server sends it to a crawler.

#### `createTestApp`

```ts
function createTestApp(options?: TestAppOptions): TestApp;

type TestAppOptions = {
  url?: string;                     // the current URL, default '/'
  route?: string;                   // the page's pattern, e.g. '/blog/[slug]'
  params?: Params;                  // what useParams() returns
  shared?: unknown;                 // what useSharedProps() returns
  publicConfig?: Record<string, unknown>; // what usePublicConfig() returns
  locale?: string;                  // what useLocale() returns
  draftMode?: boolean;              // what useDraftMode() reports
  queries?: ReadonlyArray<{ key: readonly unknown[]; data: unknown }>; // seeded as .WithQuery seeds them
  defaultOptions?: DefaultOptions;  // like netjes({ query: { defaultOptions } }), over the app's and the harness's
  queryClient?: QueryClient;        // use this client, as it is, instead of a new one
  navigation?: NavigationState;     // what useNavigation() returns, default idle
  antiforgery?: AntiforgeryToken | false; // the token <Form method="post"> adds; false for none
  actions?: Record<string, (input: unknown) => TestActionAnswer | Promise<TestActionAnswer>>;
};

type TestApp = {
  Wrapper: (props: { children: ReactNode }) => ReactNode; // pass as Testing Library's `wrapper`
  navigations: TestNavigation[];                           // every navigation, router call and post, in order
  queryClient: QueryClient;
};

type TestNavigation =
  | { type: 'push' | 'replace'; href: string }
  | { type: 'refresh' | 'back' | 'forward' }
  | { type: 'submit'; action: string; data: URLSearchParams | FormData }
  | { type: 'action'; name: string; input: unknown };

type TestActionAnswer = ActionState<unknown> & {
  queries?: ReadonlyArray<{ key: readonly unknown[]; data: unknown }>; // set, as .WithQuery does
  invalidate?: ReadonlyArray<readonly unknown[]>;                      // invalidated, as .Invalidate does
  fields?: readonly string[]; // after a failure: the fields `values` keeps, as the server names them (default all)
  message?: string;           // after a failure other than a 422: the message the server would send (default none: withheld)
};
```

```tsx
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createTestApp } from 'netjes/testing';

const app = createTestApp({
  actions: { 'home.greet': () => ({ ok: false, status: 422, errors: { name: ['Tell us your name.'] } }) },
});
render(<HelloForm />, { wrapper: app.Wrapper });
await userEvent.click(screen.getByRole('button', { name: 'Say hello' }));
expect(await screen.findByText('Tell us your name.')).toBeDefined();
```

Links and `router.push` move to the new URL, so hooks see it, and are recorded. Form posts are recorded with their fields. Server action calls are recorded and answered by `actions[name]`; an action without an answer succeeds with no result, and a failure other than a 422 throws a `ServerActionError` with the answer's `message`, withheld when it has none. Components inside the `Wrapper` can call actions from the effects they run as they mount, layout effects included; a call outside any `Wrapper` throws at once ([answering server actions](../guides/testing.md#answering-server-actions)). The default antiforgery token is a test token. The `QueryClient` starts from the app's `netjes({ query })` defaults, which the plugin hands to the tests, with the harness's over them: failed queries and mutations are tried as often as in the app, with no wait between the tries (`retryDelay: 0`), and no query's data is garbage-collected (`gcTime: Infinity`). The test's `defaultOptions` go over both ([queries that fail](../guides/testing.md#queries-that-fail)).

#### `renderDocument`

```ts
function renderDocument(page: string, options?: RenderDocumentOptions): Promise<string>;

type RenderDocumentOptions = {
  props?: Record<string, unknown>;                   // the page's props, as its loader sends them
  params?: Params;                                   // the route values, as useParams() returns them
  url?: string;                                      // the page's URL, default '/'
  layouts?: Record<string, Record<string, unknown>>; // each layout loader's props, by the layout's file
  shared?: unknown;                                  // the shared props, default null
  status?: number;                                   // default 200, or the boundary file's
  nonce?: string;                                    // the CSP nonce on every script and stylesheet
  lang?: string;                                     // <html lang>, default 'en'
  publicConfig?: Record<string, unknown>;            // what usePublicConfig() reads
  modules?: Record<string, () => Promise<unknown>>;  // the pages map, default the app's from netjes()
  signal?: AbortSignal;                              // stops the render, which rejects with its reason
};
```

Renders the document of `page`, a file of the pages map named from the app's root (`'app/blog/[slug]/page.tsx'`), as the Node sidecar renders it for a crawler, and resolves with its HTML, from `<!DOCTYPE html>` to `</html>`: the page inside the layouts the server wraps it in, with the head, Netjes's inline scripts and the payload. It builds the payload's route and segments from the pages map by the server's rules, and takes everything else from `options`; no C# runs. It waits for every Suspense boundary, and a Promise in the props or a layout's props is a `Page.Defer` value: rendered in place once it resolves, left as its Suspense fallback when it rejects, and waited for while it is pending, until `signal` aborts. What the server adds and `options` cannot set is left out: the build's stylesheets and scripts, development styles, the query entries a loader seeds with `.WithQuery`, metadata from C#, an antiforgery token, the locale, draft mode, the base path and the image optimiser's settings.

It renders a page file and the `not-found`, `error`, `forbidden`, `unauthorized` and `global-error` files, with the status the server gives them (404, 500, 403, 401 and 500) unless `status` sets another, and their props as given. A boundary file renders inside the layouts down to its own folder, as the server renders it in place of a page in or below that folder, but with a `null` route in the payload, and a section's own `forbidden` or `unauthorized` file inside the section's layout, which the server leaves out for a user who fails the section's layout loader's authorization. It rejects a file that is not in the pages map (naming the files it can render), any other file, a page in or below a folder with a parallel route slot, props for a layout the page does not have, and a DOM environment: run it under `// @vitest-environment node`, with the `netjes()` plugin, which provides the pages map, the app's folder and the query shell, also when `modules` is given ([the document](../guides/testing.md#the-document)).

#### `inlineScriptHashes`

```ts
function inlineScriptHashes(html: string): Promise<string[]>;
```

The hash-sources of the inline scripts in `html` that a browser runs and checks against a Content-Security-Policy's script sources, each `'sha256-…'` in its quotes, as a policy lists them and as `CSP_SCRIPT_HASHES` holds Netjes's: one per script element without `src` that has text and no type, a JavaScript type, `module`, `importmap` or `speculationrules`, hashed as a browser hashes it, from its text with each line break as LF. Each hash is listed once, in the order of the first script with it. Data blocks, such as the page payload (`type="application/json"`), and classic scripts with `nomodule` are left out. It runs in any environment. It sees only the scripts in `html`: a document from `renderDocument` is a crawler's, without the scripts React streams to reveal a Suspense boundary for a visitor, and the inline code of an `afterInteractive` or `lazyOnload` `<Script>`, which `<Script>` adds in the browser, is in no document; a `beforeInteractive` one's is ([the document](../guides/testing.md#the-document)).

## `netjes/testing/setup`

A Vitest setup file that runs Testing Library's `cleanup()` after each test. The `netjes()` plugin registers it when `@testing-library/react` is installed and the app's Vitest config sets no `setupFiles` of its own; list it in `setupFiles` yourself when it does.

The plugin also adds a setup file of its own to the `setupFiles` of every app whose Vitest config uses `netjes()`, unless it passes `test: false`, after the app's own setup files, whatever they are. It is not an entry point of the package. It defines a `jest` global through which Testing Library moves Vitest's fake timers on, so `waitFor` and `findBy…` work under `vi.useFakeTimers()`, and so does user-event when its user is created with `userEvent.setup({ advanceTimers: vi.advanceTimersByTime })`. With real timers the global reads `undefined`, though `'jest' in globalThis` stays true ([Fake timers](../guides/testing.md#fake-timers)). It also reads the app's query defaults from `inject('netjes:query')`, which the plugin provides, for `createTestApp()`. Under Vitest 5, an inline project in `test.projects` that shares the Vite server of the config declaring it gets neither setup file; with `sharedViteServer: false` in the root's `test` options it gets them as any config does ([Vitest](../guides/testing.md#vitest)).

## `netjes/vitals`

Core Web Vitals, measured with the `web-vitals` package, which the app installs (`npm install web-vitals`).

#### `useReportWebVitals`

```ts
function useReportWebVitals(callback: (metric: WebVitalsMetric) => void): void;

type WebVitalsMetric = Pick<Metric, 'name' | 'value' | 'rating' | 'delta' | 'id' | 'navigationType'> & { route: string | null };
```

Calls back with each Core Web Vital of the page load (CLS, FCP, INP, LCP, TTFB), with the route pattern of the page the document was loaded on, like Next.js's `useReportWebVitals`. A component that subscribes late also gets the metrics measured before it.

#### `WebVitals`

```ts
function WebVitals(): null;
```

Sends the page load's Core Web Vitals to the server with `navigator.sendBeacon`, which records them as `netjes.web_vitals.*` histograms when `NetjesOptions.CollectWebVitals` is on; without it, it sends nothing. Each measurement is sent once, even when the component unmounts and mounts again. Render it once, in the root layout. See [Deployment](../guides/deployment.md).

## `netjes/vite`

```ts
export default function netjes(options?: NetjesPluginOptions): PluginOption[];
```

The Vite plugin: React, Tailwind CSS, the page module map, the query client, Vitest defaults, and the client and server builds. Its options are in [Vite plugin](vite-plugin.md).

## Internal entry points

These are exported for the plugin and the .NET app; app code does not import them.

| Import | What it is |
|---|---|
| `netjes/client-entry` | The browser entry the plugin builds: before anything else it turns zod's compiled parsers off ([zod in the browser](../guides/security.md#zod-in-the-browser)), then it reads the page payload, runs the external scripts of a page with a `beforeInteractive` [`<Script>`](#script) (once the shell is parsed, before the page's modules), hydrates the document (or renders it, when no sidecar was ready to render it on the server), receives streamed deferred values, and starts the client router, which marks `<html>` with `data-netjes-hydrated` (below). |
| `netjes/server-entry` | The server entry: `render` and `renderToStream`, which the sidecar calls with a page payload. The payload's format is internal and can change in any version; a test renders a page's document with [`renderDocument`](#renderdocument) instead. |
| `netjes/sidecar` | The Node sidecar, `sidecar/server.mjs`, which the .NET app starts and supervises. |

The entry is internal, but the mark is public. The client router puts `data-netjes-hydrated`, with an empty value, on `<html>` once it runs in the document: after React has hydrated the page (the content of its Suspense boundaries can follow), or rendered it when no sidecar was ready, and after the router has registered as the page's action client. It is the sign end-to-end tests wait for before they click ([testing](../guides/testing.md#wait-for-hydration)). The server never renders it, a client navigation keeps it, and the router takes it off if it unmounts. The name is reserved: an app does not set, change or remove it, and a stylesheet or a DOM snapshot that reads `<html>`'s attributes in the browser sees it.

The route announcer is public in the same way. After a client navigation to another path or query, the router puts the new page's name in a visually hidden live region, `<div aria-live="polite" aria-atomic="true" data-netjes-route-announcer="">`, which it renders after the app's content, in the browser only, once the page has hydrated, so the server's HTML does not contain it. It has no role, so `getByRole('status')` does not find it; an end-to-end test finds it by its attribute ([Focus and announcements](../guides/routing.md#focus-and-announcements)). The attribute is reserved, and a test that reads the text of a whole container, such as `body`, gets the region's text too.

## The generated module

`generated/netjes.ts` is written from the app's C# types and page files by the app in Development and by `netjes generate` ([Generated types](../guides/data-loading.md#generated-types)). Next to a zod schema and a type per C# type, it exports:

| Export | What it is |
|---|---|
| `pages` | Each page's props schema by pattern, for `definePage`. |
| `layouts` | Each layout loader's props schema by folder, for `defineLayout`. Only when the app has layout loaders. |
| `sharedProps` | The shared props schema, for `useSharedProps`; `z.unknown()` without `UseSharedProps<T>`. |
| `api` | `{ request?, response? }` schemas of each JSON minimal API, keyed `'GET /api/...'`. |
| `endpointUrl(key, params?, search?)` | The URL of one of the app's endpoints, values encoded, without the base path (pass it through [`useHref`](#usehref)). `key` is the method and route as in `api`: a JSON endpoint of the app has one for each of its methods except `HEAD`, any other endpoint one for `GET`, if it answers `GET` ([Endpoint URLs](../guides/data-loading.md#endpoint-urls)). `search` adds query values the endpoint does not bind. |
| `EndpointParams`, `EndpointKey` | The route values and the query values each endpoint binds, typed from its C# handler, and its keys as a type. |
| `forms` | The `[FromForm]` schema of each page action, by pattern. |
| `serverActions` | A typed `ServerAction` per `MapAction`, by name. Only when the app has server actions. |
| `route(pattern, params?, search?, options?)` | The URL of a page, values encoded, typed by pattern. `options` is `{ hash }`, where `hash` is the id of an element on the page, added as a `#fragment`; pass `undefined` as `search` to give `options` without a query. |
| `Route` | The URLs the pages serve, as template literal types. |
| `Href` | `Route` with a query or fragment, `?…`, `#…`, and `http(s):`, `mailto:` and `tel:` URLs; also a zod schema of the same name, for `Href.parse(value)`, which the schemas use for each C# `Netjes.Href` property ([Links from C#](../guides/data-loading.md#links-from-c)). The file declares the schema, with `isHref` and `routePatterns`, right after its imports, before the schemas that use it. |
| `isHref(value)` | Whether a string is an `Href`, by the rules of [`isRouteHref`](#isroutehref). |
| `RouteParams`, `RoutePattern`, `routePatterns` | The parameters per pattern, the patterns as a type, and as an array. |

TanStack Query's hooks (`useQuery`, `useMutation`, `useQueryClient` and the rest) come straight from `@tanstack/react-query`; Netjes provides the client ([Server state](../guides/data-loading.md#server-state-with-tanstack-query)).
