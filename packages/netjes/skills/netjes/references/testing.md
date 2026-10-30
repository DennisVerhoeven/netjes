# Testing

Test first: write the test, watch it fail for the reason you expect, then change the code. Assert what a user or a browser can observe (rendered text, roles, URLs, status codes, the props a page receives), not internals.

## Components and pages: Vitest

`netjes()` in `vite.config.ts` sets up Vitest: jsdom, Testing Library cleanup, and `e2e/`, `dist/`, `bin/` and `obj/` excluded. Tests sit next to the code as `*.test.tsx` and run with `npm test`.

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

`createTestApp({ url, params, shared, queries, navigation, antiforgery, actions })` returns `{ Wrapper, navigations, queryClient }`:

- `url` and `params` set what `usePathname`, `useSearchParams` and `useParams` see; `shared` is what `useSharedProps` returns; `queries: [{ key, data }]` seeds TanStack Query as a loader's `.WithQuery` would.
- `actions` answers server action calls by name: `{ ok: true, result }` or `{ ok: false, status: 422, errors }` (add `fields: ['email']` to name the fields a failed state's `values` keep, as the server does). A component that calls an action, even from an effect as it mounts, must render inside `app.Wrapper`: outside it the call fails with "called before the page was ready".
- `navigations` records what happened: `{ type: 'push' | 'replace', href }`, `{ type: 'refresh' | 'back' | 'forward' }`, `{ type: 'submit', action, data }` for form posts, `{ type: 'action', name, input }` for action calls.

Render a page component directly with the props its loader would send: `render(<Post title="Hello" body="..." />, { wrapper: createTestApp({ url: '/blog/hello' }).Wrapper })`. A page made with `definePage` parses them with its schema, so props the loader could not send fail the test with `Invalid props: ...`.

## Loaders and actions: C#

- Loader logic: call the loader or the method it uses and read `PageResult<T>.Props` in an xUnit test.
- Endpoints: test the real app with `WebApplicationFactory<Program>` from `Microsoft.AspNetCore.Mvc.Testing` (ASP.NET Core 10 makes the `Program` class of top-level statements public, so the test project sees it). No Node is needed when the test sets `Netjes:Mode` to `Development` (routes come from `app/` on disk) and registers an `ISsrRenderer` (`Netjes.Rendering`) of its own that returns fixed HTML, in `ConfigureServices`. Request a page as the client router does, with the header `X-Netjes-Data: 1` (`NetjesHeaders.Data`): the answer is the page payload as JSON, with `props`, `status` and `antiforgery`; a page with `Page.Defer` values answers NDJSON, the payload first. When the app cannot start (no build in Production, `node` not found), `CreateClient()` throws an `InvalidOperationException` that says why.
- Server actions are `POST /_netjes/actions/{name}`. Call one as the client router does: the fields form-encoded, `X-Netjes-Data: 1`, and the payload's `antiforgery.requestToken` in the header named by `antiforgery.headerName` (a client that keeps cookies, so the antiforgery cookie goes along; `GET /_netjes/antiforgery` returns a token too). A plain form post, as without JavaScript, sends the token as the `antiforgery.formFieldName` field instead.
- Use a real database for data access tests (Testcontainers), not an in-memory fake.

## End to end: Playwright

Put Playwright tests in `e2e/` (Vitest skips it). Run them against the app in Development (`dotnet run --no-launch-profile` with `ASPNETCORE_ENVIRONMENT=Development`) and in Production (`npm run build`, then `dotnet run --no-launch-profile` with `ASPNETCORE_ENVIRONMENT=Production`), waiting on `/healthz`. Test the forms with JavaScript turned off too (`test.use({ javaScriptEnabled: false })`): they must post, show errors and keep what was typed.

## Before calling a change done

```bash
npm test && npm run typecheck && dotnet build && npx netjes generate --check
```

and `npm run routes` without new warnings.

## More

[Testing](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/testing.md) in the guides, and `createTestApp` in the [React API](https://github.com/DennisVerhoeven/netjes/blob/main/docs/reference/react-api.md#createtestapp).
