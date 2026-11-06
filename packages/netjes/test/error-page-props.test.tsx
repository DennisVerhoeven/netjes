import { act, cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import type { ReactNode } from 'react';
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { ServerActionError, serverAction, useAction } from '../src/actions.tsx';
import { Form } from '../src/form.tsx';
import { type ErrorPageProps, createModuleLoader } from '../src/modules.ts';
import type { PagePayload, SegmentRef, SpecialPageRef } from '../src/payload.ts';
import { createQueryShell } from '../src/query.tsx';
import { RouteTree } from '../src/route-tree.tsx';
import { RouterProvider } from '../src/router.tsx';
import { createTestApp } from '../src/testing.tsx';

const origin = window.location.origin;
const server = setupServer();
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

// React reports every error a boundary catches.
beforeEach(() => {
  vi.spyOn(console, 'error').mockImplementation(() => {});
});
afterEach(() => {
  vi.restoreAllMocks();
});

/** The error the app's error page was last handed. */
let handed: ErrorPageProps['error'] | undefined;

/** An error page as an app writes one: the message only when it is worth showing. */
function AppError({ error }: ErrorPageProps) {
  handed = error;
  return <p role="alert">{error.withheld ? 'We could not show this page' : error.message}</p>;
}

/** What the page below throws. */
let thrown: unknown;

function Thrower(): ReactNode {
  throw thrown;
}

const archive = serverAction<FormData, null>('posts.archive');

function Archive() {
  const [, formAction] = useAction(archive);
  return (
    <Form action={formAction}>
      <button type="submit">Archive</button>
    </Form>
  );
}

const modules = {
  '/app/error.tsx': async () => ({ default: AppError }),
  '/app/throws/page.tsx': async () => ({ default: Thrower }),
  '/app/archive/page.tsx': async () => ({ default: Archive }),
};

const withErrorPage: SegmentRef[] = [{ id: 'app', error: 'app/error.tsx' }];
const rootErrorPage: SpecialPageRef = { page: 'app/error.tsx', segments: [] };
const antiforgery = { formFieldName: '__RequestVerificationToken', headerName: 'RequestVerificationToken', requestToken: 'tok' };

function payload(page: string, url: string, overrides: Partial<PagePayload> = {}): PagePayload {
  return { kind: 'page', route: url, page, segments: [], props: {}, params: {}, shared: null, status: 200, url, version: 'v1', antiforgery, ...overrides };
}

async function renderApp(initial: PagePayload, errorPage: SpecialPageRef | null = null) {
  handed = undefined;
  window.history.replaceState(null, '', initial.url);
  const loader = createModuleLoader(modules);
  const initialModules = await loader.loadRoute(initial);
  await act(async () => {
    render(<RouterProvider initial={initial} initialModules={initialModules} loader={loader} errorPage={errorPage} shell={createQueryShell()} />);
  });
  return userEvent.setup();
}

describe('the error an error page is handed', () => {
  it("marks a server-rendered error page's message as withheld when the server says so, and keeps only the server's stack", async () => {
    await renderApp(
      payload('app/error.tsx', '/', { pageKind: 'error', status: 500, props: { status: 500, message: 'Internal Server Error', stack: null, withheld: true } }),
    );

    expect(screen.getByRole('alert').textContent).toBe('We could not show this page');
    expect(handed).toMatchObject({ message: 'Internal Server Error', status: 500, withheld: true });
    // Not the stack of the code that built the error in the browser: there is none to show.
    expect(handed?.stack).toBeUndefined();

    cleanup();
    const stack = 'System.InvalidOperationException: database is down\n   at Loader()';
    await renderApp(payload('app/error.tsx', '/', { pageKind: 'error', status: 500, props: { status: 500, message: 'database is down', stack } }));

    expect(screen.getByRole('alert').textContent).toBe('database is down');
    expect(handed?.withheld).toBeUndefined();
    expect(handed?.stack).toBe(stack);
  });

  it("gives a segment error page a withheld stand-in for a thrown value without a message, and a thrown string's text", async () => {
    thrown = { code: 'E_DOWN' };
    await renderApp(payload('app/throws/page.tsx', '/throws', { segments: withErrorPage }));

    expect(await screen.findByRole('alert')).toHaveProperty('textContent', 'We could not show this page');
    expect(handed).toBeInstanceOf(Error);
    expect(handed).toMatchObject({ message: 'Something went wrong', withheld: true });

    cleanup();
    thrown = 'The archive is closed for the night';
    await renderApp(payload('app/throws/page.tsx', '/throws', { segments: withErrorPage }));

    expect(await screen.findByRole('alert')).toHaveProperty('textContent', 'The archive is closed for the night');
    expect(handed?.withheld).toBeUndefined();

    cleanup();
    thrown = new RangeError('page 7 of 3');
    await renderApp(payload('app/throws/page.tsx', '/throws', { segments: withErrorPage }));

    expect(await screen.findByRole('alert')).toHaveProperty('textContent', 'page 7 of 3');
    expect(handed).toBe(thrown);
  });

  it('a server action that fails reaches the segment error page with its status, withheld only when the server sent no message', async () => {
    // An app's own test: the fake answers like a server with error details off, which sends a 5xx without its message.
    const app = createTestApp({ actions: { 'posts.archive': () => ({ ok: false, status: 503, errors: {} }) } });
    render(
      <RouteTree modules={{ page: Archive, segments: [{ id: 'app', Error: AppError }] }} props={{}} params={{}} status={200} pageKey="/archive" />,
      { wrapper: app.Wrapper },
    );

    await userEvent.setup().click(screen.getByRole('button', { name: 'Archive' }));

    expect((await screen.findByRole('alert')).textContent).toBe('We could not show this page');
    expect(handed).toBeInstanceOf(ServerActionError);
    expect(handed).toMatchObject({ status: 503, withheld: true });

    cleanup();
    // The server itself, for a call it refused: the reason phrase is the message, and worth showing.
    server.use(
      http.post(`${origin}/_netjes/actions/posts.archive`, () =>
        HttpResponse.json({ kind: 'action', revalidate: false, ok: false, status: 403, message: 'Forbidden' }, { status: 403 }),
      ),
    );
    const user = await renderApp(payload('app/archive/page.tsx', '/archive', { segments: withErrorPage }));

    await user.click(screen.getByRole('button', { name: 'Archive' }));

    expect((await screen.findByRole('alert')).textContent).toBe('Forbidden');
    expect(handed).toBeInstanceOf(ServerActionError);
    expect(handed).toMatchObject({ status: 403, withheld: false });
  });

  it("the router's top-level boundary hands the app's error page the error it caught", async () => {
    thrown = new TypeError("Cannot read properties of undefined (reading 'title')");
    await renderApp(payload('app/throws/page.tsx', '/throws'), rootErrorPage);

    expect((await screen.findByRole('alert')).textContent).toBe("Cannot read properties of undefined (reading 'title')");
    // As thrown: its class, its own stack and no status of a server answer it never had.
    expect(handed).toBe(thrown);
    expect(handed?.status).toBeUndefined();

    cleanup();
    thrown = new ServerActionError('Forbidden', 403);
    await renderApp(payload('app/throws/page.tsx', '/throws'), rootErrorPage);

    expect((await screen.findByRole('alert')).textContent).toBe('Forbidden');
    expect(handed).toBe(thrown);
    expect(handed?.status).toBe(403);

    cleanup();
    thrown = 'The archive is closed for the night';
    await renderApp(payload('app/throws/page.tsx', '/throws'), rootErrorPage);

    expect((await screen.findByRole('alert')).textContent).toBe('The archive is closed for the night');

    cleanup();
    // Without an error page of the app's own, the built-in one still says 500.
    thrown = new TypeError('no title');
    await renderApp(payload('app/throws/page.tsx', '/throws'));

    expect(await screen.findByText('500')).toBeDefined();
    expect(screen.getByText('no title')).toBeDefined();

    cleanup();
    // A thrown string keeps its text there too.
    thrown = 'The archive is closed for the night';
    await renderApp(payload('app/throws/page.tsx', '/throws'));

    expect(await screen.findByText('500')).toBeDefined();
    expect(screen.getByText('The archive is closed for the night')).toBeDefined();
  });
});
