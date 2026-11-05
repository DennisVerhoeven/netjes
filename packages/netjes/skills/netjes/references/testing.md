# Testing

Test first: write the test, watch it fail for the reason you expect, then change the code. Assert what a user or a browser can observe (rendered text, roles, URLs, status codes, the props a page receives), not internals.

## Components and pages: Vitest

`netjes()` in `vite.config.ts` sets up Vitest: jsdom, Testing Library cleanup (a config with `setupFiles` of its own lists `netjes/testing/setup` in them), `e2e/`, `dist/`, `bin/` and `obj/` excluded, and in every app a setup file of Netjes's own, which lets Testing Library work with fake timers and hands `createTestApp` the app's query defaults. Inline `test.projects` that share the root's Vite server, Vitest 5's default, get none of this: set `sharedViteServer: false` in the root's `test` options. Tests sit next to the code as `*.test.tsx` and run with `npm test`.

```tsx
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createTestApp } from 'netjes/testing';
import { expect, it } from 'vitest';
import { HelloForm } from './HelloForm.tsx';

it('shows the validation error from C# next to the field', async () => {
  const app = createTestApp({
    actions: { 'home.greet': () => ({ ok: false, status: 422, errors: { name: ['Tell us your name.'] } }) },
  });
  render(<HelloForm />, { wrapper: app.Wrapper });

  await userEvent.click(screen.getByRole('button', { name: 'Say hello' }));

  expect(await screen.findByText('Tell us your name.')).toBeDefined();
});
```

`createTestApp({ url, params, shared, queries, defaultOptions, navigation, antiforgery, actions })` returns `{ Wrapper, navigations, queryClient }`:

- `url` and `params` set what `usePathname`, `useSearchParams` and `useParams` see; `shared` is what `useSharedProps` returns; `queries: [{ key, data }]` seeds TanStack Query as a loader's `.WithQuery` would.
- The `QueryClient` starts from the app's `netjes({ query: { defaultOptions } })`: a failed query or mutation is tried as often as in the app, with no wait between the tries, so the test sees the error the app shows after its last try. `defaultOptions` goes over them: `{ queries: { retry: false } }` shows the error after the first request. Seeded data is fresh for the app's `staleTime` from when `createTestApp()` is called: with a `staleTime` of 0, or in a component rendered after a fake clock moved past it, a seeded query is fetched again as it mounts; answer that request (MSW) or pass `{ queries: { staleTime: 60_000 } }`.
- `actions` answers server action calls by name: `{ ok: true, result }` or `{ ok: false, status: 422, errors }` (add `fields: ['email']` to name the fields a failed state's `values` keep, as the server does). A component that calls an action, even from an effect as it mounts, must render inside `app.Wrapper`: outside it the call fails with "called before the page was ready".
- `navigations` records what happened: `{ type: 'push' | 'replace', href }`, `{ type: 'refresh' | 'back' | 'forward' }`, `{ type: 'submit', action, data }` for form posts, `{ type: 'action', name, input }` for action calls.

Render a page component directly with the props its loader would send: `render(<Post title="Hello" body="..." />, { wrapper: createTestApp({ url: '/blog/hello' }).Wrapper })`. A page made with `definePage` parses them with its schema, so props the loader could not send fail the test with `Invalid props: ...`.

For the whole document, as the server sends it (the layouts, the head, Netjes's inline scripts), `renderDocument('app/blog/[slug]/page.tsx', { props, params: { slug: 'hello' }, url: '/blog/hello', layouts: { 'app/blog/layout.tsx': { categories } }, shared })` from `netjes/testing` resolves with the HTML. The test file starts with `// @vitest-environment node`: in a DOM environment it rejects, since the server renders without one. No C# runs, so pass what the loaders would send; a layout that `layouts` does not name gets no props. Nothing else the server adds is there: query entries seeded with `.WithQuery` (a `useQuery` renders without its data), metadata from C#, the antiforgery token, the locale, draft mode, the base path and the image optimiser's settings. A boundary file renders inside the layouts down to its own folder, with no route. A Promise in the props is a `Page.Defer` value; pass `signal: AbortSignal.timeout(1000)` when one may never settle. `inlineScriptHashes(html)` lists the `'sha256-…'` hashes of the inline scripts in the document that a browser runs, for a Content-Security-Policy without a nonce: with no inline scripts of the app's own they equal `CSP_SCRIPT_HASHES.split(' ')` from `netjes`, as `app/document.test.ts` in a new app checks for the home page. The document is a crawler's, so React's scripts that reveal a Suspense boundary for a visitor are not in it, nor the inline code of an `afterInteractive` or `lazyOnload` `<Script>`, which the browser adds: its hash goes in the policy only. A `beforeInteractive` `<Script>`'s inline code is in the document, so its hash goes in both.

Fake timers work with Testing Library: after `vi.useFakeTimers()`, create the user with `userEvent.setup({ advanceTimers: vi.advanceTimersByTime })`, and let `findBy…` or `waitFor` wait, which moves the fake clock on (their timeout counts fake time). Run a timer that `fireEvent` started with `await act(() => vi.advanceTimersByTimeAsync(ms))`, and call `vi.useRealTimers()` in an `afterEach`.

## Loaders and actions: C#

- Loader logic: call the loader or the method it uses and read `PageResult<T>.Props` in an xUnit test.
- Endpoints: test the real app with `WebApplicationFactory<Program>` from `Microsoft.AspNetCore.Mvc.Testing` (ASP.NET Core 10 makes the `Program` class of top-level statements public, so the test project sees it). No Node is needed when the test sets `Netjes:Mode` to `Development` (routes come from `app/` on disk) and registers an `ISsrRenderer` (`Netjes.Rendering`) of its own that returns fixed HTML, in `ConfigureServices`. Run it in the `Testing` environment (`builder.UseEnvironment("Testing")`), as the testing guide's factory does: `WebApplicationFactory` uses `Development` unless told otherwise, and there Netjes output-caches no page, so a test of `CachePage` would see every request render. Request a page as the client router does, with the header `X-Netjes-Data: 1` (`NetjesHeaders.Data`): the answer is the page payload as JSON, with `props`, `status` and `antiforgery`; a page with `Page.Defer` values answers NDJSON, the payload first. When the app cannot start (no build in Production, `node` not found), `CreateClient()` throws an `InvalidOperationException` that says why.
- Server actions are `POST /_netjes/actions/{name}`. Call one as the client router does: the fields form-encoded, `X-Netjes-Data: 1`, and the payload's `antiforgery.requestToken` in the header named by `antiforgery.headerName` (a client that keeps cookies, so the antiforgery cookie goes along; `GET /_netjes/antiforgery` returns a token too). A plain form post, as without JavaScript, sends the token as the `antiforgery.formFieldName` field instead.
- Use a real database for data access tests (Testcontainers), not an in-memory fake.

## End to end: Playwright

Put Playwright tests in `e2e/` (Vitest skips it). Run them against the app in Development (`dotnet run --no-launch-profile` with `ASPNETCORE_ENVIRONMENT=Development`) and in Production (`npm run build`, then `dotnet run --no-launch-profile` with `ASPNETCORE_ENVIRONMENT=Production`), waiting on `/healthz`. Test the forms with JavaScript turned off too (`test.use({ javaScriptEnabled: false })`): they must post, show errors and keep what was typed.

With JavaScript on, wait for hydration after each full page load, before the first click or keystroke; until then links load pages in full and forms post natively, so the test can pass without the client. Netjes puts `data-netjes-hydrated` on `<html>` once the client router runs in the document:

```ts
await page.goto('/');
await expect(page.locator('html')).toHaveAttribute('data-netjes-hydrated', '');
```

`page.locator('html[data-netjes-hydrated]').waitFor({ state: 'attached' })` waits for the same thing without `expect`'s 5-second limit. Never wait on `history.state`: a reload keeps it, so it is there before the page's scripts have run. Content inside a Suspense boundary can hydrate after the mark; React hydrates it first when a click lands on it.

## Before calling a change done

```bash
npm test && npm run typecheck && dotnet build && npx netjes generate --check
```

and `npm run routes` without new warnings.

## More

[Testing](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/testing.md) in the guides, and [`createTestApp`](https://github.com/DennisVerhoeven/netjes/blob/main/docs/reference/react-api.md#createtestapp) and [`renderDocument`](https://github.com/DennisVerhoeven/netjes/blob/main/docs/reference/react-api.md#renderdocument) in the React API.
