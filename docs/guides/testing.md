# Testing

This guide is for app developers writing tests for a Netjes app: component and page tests in Vitest with the `netjes/testing` harness, tests of C# loaders and actions with `WebApplicationFactory`, end-to-end tests in Playwright, and the checks to run in CI. An app made with `netjes new` has two component tests to start from, in `components/`, and a test of the home page's whole document, `app/document.test.ts`.

## Vitest

The template runs Vitest with `npm test` (`vitest run`). The `netjes()` Vite plugin sets these defaults under Vitest, each only when the app's own `test` config does not set it:

- `environment: 'jsdom'`, when `jsdom` is installed;
- `exclude`: `node_modules/`, `dist/`, `bin/`, `obj/`, `e2e/` (Playwright's folder) and `.git/`;
- a setup file that unmounts what each test rendered (Testing Library's `cleanup`), when `@testing-library/react` is installed and the app sets no `setupFiles` of its own. An app that sets them lists `netjes/testing/setup` there itself.

Whatever the app's config says, it also adds two settings to it:

- a setup file of its own, in `setupFiles` after the app's own setup files. It lets Testing Library work with Vitest's fake timers ([Fake timers](#fake-timers)), and hands `createTestApp()` the app's query defaults;
- `provide['netjes:query']`: the `defaultOptions` of `netjes({ query })` (`{}` when there are none, and nothing with `query: false`), which that setup file reads ([Queries that fail](#queries-that-fail)). The app's own `provide` keys stay as they are.

There is no `include` default, so Vitest finds `*.test.*` and `*.spec.*` files outside those folders. `netjes({ test: false })` adds none of these settings ([Vite plugin](../reference/vite-plugin.md#vitest-defaults)).

An inline project in `test.projects` is the exception. Under Vitest 5, one that changes no Vite options shares the Vite server of the config that declares it, and Vitest builds its `test` options from that config's as written, before any plugin adds to them: neither these defaults nor Netjes's setup file reaches its tests. Set `sharedViteServer: false` in the root's `test` options, and each project gets them.

## Component and page tests

`createTestApp()` from `netjes/testing` gives a component what a Netjes page has around it: the router, the page payload, the query client and the public config. Pass its `Wrapper` to Testing Library:

```tsx
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createTestApp } from 'netjes/testing';
import { describe, expect, it } from 'vitest';
import { Greeting } from './Greeting.tsx';

describe('Greeting', () => {
  it('links to the about page without a full page load', async () => {
    const app = createTestApp();
    render(<Greeting message="Hello" renderedAt="2026-10-28T12:00:00Z" />, { wrapper: app.Wrapper });

    await userEvent.click(screen.getByRole('link', { name: 'About this app' }));

    expect(app.navigations).toEqual([{ type: 'push', href: '/about' }]);
  });
});
```

The router is a fake. `<Link>`, `router.push` and `router.replace` move to the new URL, so hooks such as `usePathname()` and `useSearchParams()` see it, but no page is loaded. Every navigation, router call, form post and server action call is recorded in `app.navigations`, in order:

| Entry | For |
|---|---|
| `{ type: 'push' \| 'replace', href }` | A link, `router.push`, `router.replace` |
| `{ type: 'refresh' \| 'back' \| 'forward' }` | `router.refresh()`, `router.back()`, `router.forward()` |
| `{ type: 'submit', action, data }` | A `<Form method="post">` post, with its fields as `URLSearchParams` or `FormData` |
| `{ type: 'action', name, input }` | A server action call, with its input (an object, or the form's `FormData`) |

### Options

| Option | What it sets |
|---|---|
| `url` | The current URL. Default `/`. |
| `route` | The page's route pattern, such as `/blog/[slug]`. |
| `params` | What `useParams()` returns. |
| `shared` | The shared props, as the server would send them. |
| `queries` | Entries seeded into the query client, as `.WithQuery` seeds them: `[{ key, data }]`. |
| `defaultOptions` | TanStack Query defaults, like `netjes({ query: { defaultOptions } })`, merged over the app's own and the harness's. Failed queries and mutations are tried as often as in the app, with no wait between the tries ([Queries that fail](#queries-that-fail)). |
| `queryClient` | A client of your own instead, used as it is, without any of those defaults. |
| `actions` | Answers for server action calls, by name (below). |
| `navigation` | What `useNavigation()` returns. Default idle. |
| `antiforgery` | The token `<Form method="post">` adds. Default a test token; `false` for none. |
| `publicConfig` | What `usePublicConfig()` returns. |
| `locale` | What `useLocale()` returns. |
| `draftMode` | What `useDraftMode()` reports. |

`createTestApp()` returns `{ Wrapper, navigations, queryClient }`. The [React API reference](../reference/react-api.md#createtestapp) has the types.

### Answering server actions

`actions` answers calls by name, as the C# action would. An action without an answer succeeds with no result. A failure can name the fields its state's `values` keep, as the server does for the fields an action declares:

```tsx
const app = createTestApp({
  actions: {
    'home.greet': () => ({ ok: false, status: 422, errors: { name: ['Tell us your name.'] }, fields: ['name'] }),
  },
});
render(<HelloForm />, { wrapper: app.Wrapper });

await userEvent.click(screen.getByRole('button', { name: 'Say hello' }));

expect(await screen.findByText('Tell us your name.')).toBeDefined();
```

An answer can also carry `queries` (`[{ key, data }]`) and `invalidate` (query keys), like `.WithQuery` and `.Invalidate` on a C# result.

The `Wrapper` puts its stand-in for the router in place before the components inside it run any effect, so an action a component calls from `useEffect` or `useLayoutEffect` as it mounts is answered and recorded like any other call. Render such a component inside the `Wrapper`: outside it there is no router to wait for, and the call fails at once with "called before the page was ready".

### Pages

A page file's default export is a component like any other. Render it with the props its loader would send; `definePage` checks them against the generated schema, as it does in the app, so a test with props the schema rejects fails the way the page would:

```tsx
import Guestbook from './page.tsx';

it('shows the validation errors the action sent back, next to their fields', () => {
  const app = createTestApp({ url: '/guestbook' });

  render(<Guestbook entries={[]} errors={{ name: 'Tell us your name.' }} values={{ name: '', message: 'Hi' }} />, {
    wrapper: app.Wrapper,
  });

  expect(screen.getByText('Tell us your name.')).toBeDefined();
});
```

A component that fetches its own data with `useQuery` gets seeded data from `queries`. For what it fetches after that, answer the requests with a mock server such as [MSW](https://mswjs.io), as the Blog example's `LikeButton.test.tsx` does.

### Queries that fail

In the browser a failed query is tried three more times, after 1, 2 and 4 seconds (TanStack Query's defaults for `retry` and `retryDelay`), unless the app's `netjes({ query: { defaultOptions } })` says otherwise. `createTestApp()` builds its client from those same defaults, which the plugin hands to the tests, with the harness's own over them and the test's `defaultOptions` over those:

- A failed query or mutation is tried as often as in the app, but with no wait between the tries. So a test sees what the app shows in the end: the error once every try has failed, or the data that a later try got.
- The app's `staleTime`, `retry`, refetch options and mutation `retry` apply in tests. Its `retryDelay` and its queries' `gcTime` do not: the harness sets `retryDelay: 0`, and keeps every query's data for the whole test (`gcTime: Infinity`).
- `defaultOptions: { queries: { retry: false } }` shows the error after the first request. To test the waits themselves, pass a `retryDelay` of your own, such as TanStack Query's `(attemptIndex) => Math.min(1000 * 2 ** attemptIndex, 30_000)`, and move the clock on with [fake timers](#fake-timers).
- Under fake timers, each try after the first still waits on a timer of 0 ms, which runs only when the fake clock moves on. `findBy…` and `waitFor` move it on as they wait.
- Seeded data is fresh for the app's `staleTime`, counted from when `createTestApp()` is called. With a `staleTime` of 0, a component fetches its seeded query again as it mounts, as it does in the browser; with a longer one, only a component that mounts after that time, such as one rendered after the test moved [fake timers](#fake-timers) on. Answer that request, or set `defaultOptions: { queries: { staleTime: 60_000 } }`.

A Vitest config without `netjes()` hands over no app defaults, and `createTestApp()` starts from the Netjes defaults (a `staleTime` of one minute) and the harness's ([ADR 0028](../decisions/0028-test-harness-starts-from-the-apps-query-defaults.md)). In Vitest's browser mode, Vitest sends provided values to the test page as JSON, so an `Infinity` in `netjes({ query })`, such as `staleTime: Infinity`, reaches `createTestApp()` as `null`, and seeded data is stale at once: pass such a value in the test's `defaultOptions` there.

### Fake timers

A component that waits, such as a search box that searches once the typing stops, is tested with Vitest's fake timers. Testing Library's queries and user-event work with them:

```tsx
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createTestApp } from 'netjes/testing';
import { afterEach, expect, it, vi } from 'vitest';
import { SearchBox } from './SearchBox.tsx';

afterEach(() => {
  vi.useRealTimers();
});

it('searches once the typing stops', async () => {
  vi.useFakeTimers();
  const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
  const app = createTestApp({ url: '/search' });
  render(<SearchBox />, { wrapper: app.Wrapper });

  await user.type(screen.getByRole('searchbox'), 'tea');

  await waitFor(() => expect(app.navigations).toEqual([{ type: 'replace', href: '/search?q=tea' }]));
});
```

- `findBy…` queries and `waitFor` move the fake clock on by their polling interval (50 ms) while they wait, and their `timeout` (1 s by default) runs on the fake clock.
- user-event moves it on through its `advanceTimers` option: create the user with `userEvent.setup({ advanceTimers: vi.advanceTimersByTime })`. Without that option, as with user-event's direct API (`userEvent.click(...)` with no `setup()`), user-event waits out its own delays on the fake clock, and the call never returns.
- `fireEvent` dispatches the event at once. To run a timer it started, move the clock on inside `act`: `await act(() => vi.advanceTimersByTimeAsync(300))`.

Testing Library moves fake timers on only through a `jest` global, which Vitest does not have, so Netjes's setup file defines one while Vitest's timers are fake. `typeof jest` is `'object'` then and `'undefined'` with real timers, and `'jest' in globalThis` is always true. A test on `vi.useFakeTimers({ shouldAdvanceTime: true })`, whose fake clock also follows real time, takes Testing Library's path for fake timers too. A `jest` global that the app's own setup file defines, or that a test assigns, is kept instead of Netjes's ([ADR 0027](../decisions/0027-testing-library-advances-vitest-fake-timers.md)).

### The document

`renderDocument()` from `netjes/testing` renders a page's whole document, from `<!DOCTYPE html>` to `</html>`, as the Node process renders it for a crawler: the page inside the layouts the server wraps it in, the head with the metadata of the page and its layouts, Netjes's inline scripts and the page payload. It returns the HTML. Name the page by its file, as `app/` holds it, and pass what the C# side would send with it:

```ts
// @vitest-environment node
import { renderDocument } from 'netjes/testing';
import { expect, it } from 'vitest';

it('renders the post inside the blog layout, which lists the categories', async () => {
  const html = await renderDocument('app/blog/[slug]/page.tsx', {
    url: '/blog/hello',
    params: { slug: 'hello' },
    props: { title: 'Hello', body: 'The first post.' },
    layouts: { 'app/blog/layout.tsx': { categories: ['react', 'dotnet'] } },
    shared: { siteName: 'Shop' },
  });

  expect(html).toContain('<h1>Hello</h1>');
  expect(html).toContain('<li>dotnet</li>');
});
```

- The test file runs in Vitest's node environment: start it with `// @vitest-environment node`. The server renders without a DOM, and parts of a page take another path where there is one, such as a `<Form>` for a server action, which renders with the action's URL only on the server. In a DOM environment, such as the jsdom the plugin sets by default, `renderDocument` rejects with an error that says so.
- The Vitest config has the `netjes()` plugin, as the template's `vite.config.ts` does. The plugin provides the pages map: the page, layout and boundary files in `app/`, from which `renderDocument` builds the segments the server would send, by the server's rules ([ADR 0038](../decisions/0038-netjes-testing-renders-a-pages-document.md)). A test can pass a map of its own as `modules`, keyed `/app/...` as `import.meta.glob` keys it.
- No C# runs. The props, each layout's props (by the layout's file), the shared props, the route values and the URL are what the test passes, and a layout that `layouts` does not name gets no props. Nothing else the server would send with the page is in the document (below).
- It renders a page file, or a `not-found`, `error`, `forbidden`, `unauthorized` or `global-error` file, with the status the server gives it (404, 500, 403, 401 and 500) unless `status` sets another. An error file gets the props the test passes and no others: the server sends an error page `{ status, message, stack }`, from which Netjes builds the `error` prop that `error.tsx` reads, so pass those. A page in a parallel route slot (`app/@modal/...`) is refused, and so is any page in or below a folder that has a slot, since the server chooses what each slot renders.
- A boundary file renders as the server renders it in place of a page in or below its folder: inside the layouts down to its own folder, that folder's layout included, and `global-error` without any layout. The payload's route is `null`, which the server sends only when no page matched; in place of a page, it sends that page's route. One case renders otherwise: for a user who fails the authorization of a section's layout loader, the server renders the section's own `forbidden` or `unauthorized` file without the section's layout, and `renderDocument` renders that file inside it.
- The document is a crawler's: it waits for every Suspense boundary and renders what they hold in place.

What the server adds to a page's document, and a test cannot pass, is left out, so a page that reads it renders as it does without it:

- The TanStack Query entries a loader seeds with `.WithQuery`. A component that reads one with `useQuery` renders as it does before its data arrives, where the server's document has the data.
- Metadata from a C# loader (`.WithMetadata(...)`). The head has what the files' `metadata` exports give, and the elements the page and its layouts render there, such as a `<title>`.
- The antiforgery token: a `<Form method="post">` renders without its token field.
- The request's locale, draft mode and base path: `useLocale()` returns `null`, `useDraftMode()` reports draft mode off, and `useBasePath()` returns `''`.
- The image optimiser's settings from `Netjes:Images`: `<Image>` builds its URLs from the default path and widths, with the quality it is given.
- The build's stylesheets and module scripts, and the styles that development inlines.

| Option | What it sets |
|---|---|
| `props` | The page's props, as its C# loader sends them. A Promise in them is a `Page.Defer` value. |
| `params` | The route values, as `useParams()` returns them: a string for `[slug]`, an array for a catch-all. Default none. |
| `url` | The page's URL, path and query. Default `/`. |
| `layouts` | Each layout loader's props, by the layout's file: `{ 'app/blog/layout.tsx': { categories } }`. A Promise in them is a `Page.Defer` value. |
| `shared` | The shared props, as the server sends them (`UseSharedProps`). Default `null`. |
| `status` | The page's status. Default 200, or the boundary file's (above). |
| `nonce` | A Content-Security-Policy nonce, which goes on every script and stylesheet, as under a policy with `{nonce}`. |
| `lang` | `<html lang>`. Default `en`. |
| `publicConfig` | What `usePublicConfig()` reads, as the `Netjes:PublicConfig` section. |
| `modules` | The pages map, keyed `/app/...`. Default the app's, from the plugin. |
| `signal` | Stops the render, which then rejects with the signal's reason. |

A Promise in the props, at any depth, or in a layout's props, is a `Page.Defer` value, which reaches the page as the server's does. One that resolves renders in place. One that rejects leaves the fallback of the Suspense boundary that reads it in the document, with the error's message in the script that hands the failure to the browser, as the server sends a value that failed; the error is also logged with `console.error`, as the Node process logs it. One that never settles keeps the render waiting, and the test runs into Vitest's timeout: pass `signal: AbortSignal.timeout(1000)`, and `renderDocument` rejects with the signal's reason instead.

A Content-Security-Policy without a nonce lists the hash of each inline script a page runs ([hash-based policies](security.md#hash-based)). `inlineScriptHashes(html)` returns the hashes of those in a document, as `'sha256-…'` sources in their quotes: one for each script element without `src` that a browser runs and checks against the policy, with no type, a JavaScript type, `module`, `importmap` or `speculationrules`, in the order they first appear, each once. The payload, a `type="application/json"` data block, is not one. A page that renders no inline scripts of its own has only Netjes's in its document, whose hashes `CSP_SCRIPT_HASHES` holds, as the new app's `app/document.test.ts` checks for the home page:

```ts
// @vitest-environment node
import { CSP_SCRIPT_HASHES } from 'netjes';
import { inlineScriptHashes, renderDocument } from 'netjes/testing';
import { expect, it } from 'vitest';

it('has no inline script in its document but the one whose hash Netjes lists for a policy without a nonce', async () => {
  const html = await renderDocument('app/page.tsx', {
    props: { message: 'Hello from C#', renderedAt: '2026-11-05T12:00:00Z' },
    shared: { siteName: 'Shop' },
  });

  expect(await inlineScriptHashes(html)).toEqual(CSP_SCRIPT_HASHES.split(' '));
});
```

The policy in C# is built from `NetjesContentSecurityPolicy.ScriptHashes`, which holds the same hashes. When the app's pages or layouts render `<script>` elements with inline code of their own, such as a theme script, the policy lists their hashes too, and so does the test's expected list. A page with `Page.Defer` values has one more inline script per value in its document, which carries the value, so no fixed hash covers it: under a hash-based policy such a page must not stream.

The check sees the document a crawler gets. A visitor's browser also gets inline scripts that are not in it, which a policy without a nonce checks too:

- React's scripts that reveal a Suspense boundary still waiting when a visitor's shell goes out. A crawler's document waits for every boundary, so it has none of them, and no hash covers them, since they name the boundaries of that render. Under a hash-based policy no boundary may still wait when the shell goes out, lazy code under `loading.tsx` included ([hash-based policies](security.md#hash-based)), and this check cannot show that none does.
- The inline code of a `<Script>`, which `<Script>` adds in the browser. It is in no document, so it stays out of the test's expected list, and its hash goes in the policy.

## C# tests of loaders and actions

### Loaders as plain methods

`Page.Props(x)` returns a `PageResult<T>` whose `Props` holds `x` (null for a redirect or not-found), so you can call a loader written as a method from a test:

```csharp
public static class HomePage
{
    public sealed record HomeProps(string Message, DateTimeOffset RenderedAt);

    public static PageResult<HomeProps> Home(TimeProvider time) => Page.Props(new HomeProps("Hello from C#", time.GetUtcNow()));

    public static void MapHomePage(this IEndpointRouteBuilder app) => app.MapPageLoader("/", Home);
}

[Fact]
public void Home_SaysHello() => Assert.Equal("Hello from C#", HomePage.Home(TimeProvider.System).Props?.Message);
```

### The app over HTTP

`WebApplicationFactory` from `Microsoft.AspNetCore.Mvc.Testing` runs the real app in memory, with its pipeline, authorization and services. Two settings let it run without a client build or Node: development mode, so the routes come from the files in `app/`, and a stand-in for the renderer, so no sidecar starts. The TaskBoard example's tests do this:

```csharp
public sealed class AppFactory : WebApplicationFactory<Program>
{
    public FakeRenderer Renderer { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // Development mode reads the routes from app/ on disk; nothing is rendered, so no Vite or Node is needed.
        builder.UseSetting("Netjes:Mode", "Development");
        builder.ConfigureServices(services => services.AddSingleton<ISsrRenderer>(Renderer));
    }
}

/// <summary>Keeps the payload of the last document instead of rendering it.</summary>
public sealed class FakeRenderer : ISsrRenderer
{
    public string? Last { get; private set; }

    public Task<string> RenderAsync(string payloadJson, CancellationToken cancellationToken)
    {
        Last = payloadJson;
        return Task.FromResult("<!DOCTYPE html><html><body>rendered</body></html>");
    }
}
```

The factory runs the app in the `Testing` environment, with Netjes in development mode, rather than in `Development`. In the Development environment Netjes output-caches no page: each request renders the page again, and an output-cached page goes out `no-store` there ([caching](caching.md#in-the-development-environment)). So a test of what `CachePage` or other output caching stores runs in `Testing`, or any environment other than `Development`; a test of what Development does sets `UseEnvironment("Development")`.

The test project references the app's project and `Microsoft.AspNetCore.Mvc.Testing`; ASP.NET Core 10 makes the `Program` class of top-level statements public, so `WebApplicationFactory<Program>` can start it. In development mode the app also writes `generated/netjes.ts` at startup when it is out of date, as `dotnet run` does.

When the app cannot start, `CreateClient()` throws why, as an `InvalidOperationException` whose message says what to do: `No production build found at …` for a test that runs the app in Production without a build, or `Could not start 'node'. …` for one that renders with the real Node processes where Node.js is not installed. Run as its own program, the app logs that message in one line and exits with code 1 instead ([deployment](deployment.md#what-the-server-needs)).

A host that the test project builds itself with `WebApplication.CreateBuilder`, rather than `WebApplicationFactory`, has the test assembly's name, so Netjes takes it for the program: when its Node processes cannot start, `StartAsync()` returns, and the app logs why and stops. Give it another name with `WebApplicationOptions.ApplicationName`, as `WebApplicationFactory` gives the app its own, and `StartAsync()` throws instead.

Talk to it as the client router does. A request with `X-Netjes-Data: 1` gets the page payload as JSON:

```csharp
public sealed class HomePageTests(AppFactory app) : IClassFixture<AppFactory>
{
    [Fact]
    public async Task TheHomePage_HasTheLoadersMessage()
    {
        var client = app.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(NetjesHeaders.Data, "1");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Hello from C#", (string?)payload["props"]!["message"]);
    }
}
```

A page with `Page.Defer` values answers with NDJSON: the payload on the first line, then one line per value. A document request (without the header) goes through the fake renderer, whose `Last` then holds the payload the sidecar would have rendered.

A server action needs an antiforgery token. Get one from `/_netjes/antiforgery`, which also sets the antiforgery cookie (the factory's client keeps cookies), and send it as the header the response names:

```csharp
[Fact]
public async Task Greeting_WithoutAName_IsAFieldError()
{
    var client = app.CreateClient();
    var token = JsonNode.Parse(await client.GetStringAsync("/_netjes/antiforgery", TestContext.Current.CancellationToken))!;
    var request = new HttpRequestMessage(HttpMethod.Post, "/_netjes/actions/home.greet")
    {
        Content = new FormUrlEncodedContent([new("name", "")]),
    };
    request.Headers.Add(NetjesHeaders.Data, "1");
    request.Headers.Add((string)token["headerName"]!, (string)token["requestToken"]!);

    var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
    var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

    Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    Assert.Equal("Tell us your name.", (string?)result["errors"]!["name"]![0]);
}
```

Without `X-Netjes-Data` the same post is a form post without JavaScript: put the token in the form field `token["formFieldName"]` instead, and expect a 303 redirect on success, or the form's page rendered again with status 422.

`tests/TaskBoard.Tests` has a fuller harness: a `Browser` type that keeps cookies and the last token, signs up users and calls actions, against Postgres in Testcontainers.

A test about every page at once, such as one that checks that each page `CachePage` caches has a tag to revalidate it by, needs no request: it walks the app's endpoints and reads the metadata Netjes puts on them ([endpoint metadata](../reference/csharp-api.md#endpoint-metadata)).

## End-to-end tests

Playwright tests the app in a real browser. The examples run each suite against two servers at once, one in Development (the Vite dev server) and one in Production (the build), since some failures show in one only. A server in the Playwright config waits for the app's health check:

```ts
webServer: [
  {
    command: 'dotnet run --no-build --no-launch-profile',
    url: `${DEV_URL}/healthz`,
    env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: DEV_URL, Netjes__ViteDevPort: VITE_PORT },
    reuseExistingServer: !process.env.CI,
  },
  {
    command: 'npm run build && dotnet run --no-build --no-launch-profile',
    url: `${PROD_URL}/healthz`,
    env: { ASPNETCORE_ENVIRONMENT: 'Production', ASPNETCORE_URLS: PROD_URL },
    reuseExistingServer: !process.env.CI,
  },
],
```

`--no-build` starts the last build, so build the app first: the examples' `test:e2e` script is `dotnet build && playwright test`. Give each development server its own `Netjes__ViteDevPort`, so two of them can run side by side. Put the specs in `e2e/`, which the Vitest defaults leave out, and install the browser once with `npx playwright install chromium`. `examples/Blog/playwright.config.ts` also runs the app under a base path, with no Node process ready (the client-rendered fallback), and as a static export.

### Wait for hydration

Until the page has hydrated, a `<Link>` loads the next page in full, a `<Form>` posts natively and an `onClick` handler does not run, so a test that clicks or types too early can pass on the path a browser without JavaScript takes. Once the client router runs in the document, Netjes puts `data-netjes-hydrated` on `<html>`, with an empty value. After each full page load, wait for it before the test's first click or keystroke:

```ts
await page.goto('/guestbook');
await page.locator('html[data-netjes-hydrated]').waitFor({ state: 'attached' });
```

`waitFor` waits as long as the test may run, unless the config sets an `actionTimeout`. The same wait as a web-first assertion, which retries for `expect`'s timeout instead (5 seconds by default):

```ts
await expect(page.locator('html')).toHaveAttribute('data-netjes-hydrated', '');
```

What the mark says, and what it does not:

- The client router runs in this document. React has hydrated the page (all but the content of its Suspense boundaries, below), or rendered it in the browser when no Node process was ready ([the client-rendered fallback](rendering-and-streaming.md#when-no-sidecar-is-ready)). The router has keyed the history entry, listens for back and forward, and is the page's action client, so a server action call goes out at once.
- Each document gets its own. After a reload, or a back or forward that loads the page again, the new document has no mark until its own router runs. A client navigation keeps it.
- It does not say that every Suspense boundary has hydrated. React hydrates the content of a boundary (a `loading.tsx`, or one of the page's own) in a pass of its own after the rest of the page, and content that is still streaming in once it arrives. When a click lands on such content before React gets to it, React hydrates it at once and then handles the click. Content that has not arrived cannot be clicked: wait for it to show, as Playwright's actions do.
- The server never renders it, so a test with JavaScript turned off never sees it. A page whose modules fail to load never gets it either: the page stays as the server rendered it, without the client router, and a test that waits for the mark there times out.

Do not wait for the router's key in `history.state` instead. It is internal, and the browser keeps a history entry's state when it loads the document again, so after a reload the key is there before any of the page's scripts has run.

## In CI

```bash
npm ci
npx netjes generate --check            # the committed generated/netjes.ts matches the C# code
npm run typecheck
npm test                               # Vitest
dotnet test -c Release                 # loaders and actions
dotnet build                           # the Debug build that dotnet run --no-build starts
npx playwright install --with-deps chromium && npx playwright test
```

`netjes generate --check` runs the app once in command mode, without a build or a Node process and on a throwaway loopback port, and exits 1 when the generated types are out of date. An app that does not start, such as one with a loader or action minimal APIs cannot bind, fails the check with the exception's message. Startup code that needs a database can skip that work with `app.GetNetjesCommand()`; see the [CLI reference](../reference/cli.md#netjes-generate).
