# Testing

This guide is for app developers writing tests for a Netjes app: component and page tests in Vitest with the `netjes/testing` harness, tests of C# loaders and actions with `WebApplicationFactory`, end-to-end tests in Playwright, and the checks to run in CI. An app made with `netjes new` has two component tests to start from, in `components/`.

## Vitest

The template runs Vitest with `npm test` (`vitest run`). The `netjes()` Vite plugin sets these defaults under Vitest, each only when the app's own `test` config does not set it:

- `environment: 'jsdom'`, when `jsdom` is installed;
- `exclude`: `node_modules/`, `dist/`, `bin/`, `obj/`, `e2e/` (Playwright's folder) and `.git/`;
- a setup file that unmounts what each test rendered (Testing Library's `cleanup`), when `@testing-library/react` is installed.

There is no `include` default, so Vitest finds `*.test.*` and `*.spec.*` files outside those folders. `netjes({ test: false })` adds none of them ([Vite plugin](../reference/vite-plugin.md#vitest-defaults)).

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
| `defaultOptions` | TanStack Query defaults, like `netjes({ query: { defaultOptions } })`. Retries are off. |
| `queryClient` | A client of your own instead. |
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
