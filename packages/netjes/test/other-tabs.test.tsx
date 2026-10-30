import { useQuery } from '@tanstack/react-query';
import { act, render, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { serverAction, useAction } from '../src/actions.tsx';
import { Form } from '../src/form.tsx';
import { Link } from '../src/link.tsx';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload } from '../src/payload.ts';
import { createQueryShell } from '../src/query.tsx';
import { RouterProvider, type RouterProviderProps } from '../src/router.tsx';

/**
 * Two tabs of one browser share the app's cookies. Here they are two routers in one document, each with its own
 * QueryClient, as two tabs have; the one rendered last is the action client, so it is the tab that signs out.
 */

const origin = window.location.origin;
const server = setupServer();
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));
afterEach(() => {
  server.resetHandlers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});
afterAll(() => server.close());

const signOut = serverAction<FormData, null>('accounts.signOut');

/** Who the page's props and the client's query cache say is signed in. */
function Account(props: Record<string, unknown>) {
  const me = useQuery({ queryKey: ['me'], queryFn: async () => (await (await fetch(`${origin}/api/me`)).json()) as string });
  return (
    <main>
      <p>{`Page says ${String(props.user)}`}</p>
      <p>{me.data ? `Query says ${me.data}` : 'Query loading'}</p>
      <Link href="/elsewhere">Elsewhere</Link>
      <Form method="post" action="/report">
        <button type="submit">Report</button>
      </Form>
    </main>
  );
}

function SignOut() {
  const [state, formAction] = useAction(signOut);
  return (
    <main>
      <Form action={formAction}>
        <button type="submit">Sign out</button>
      </Form>
      {/* Its own URL, written out: the two tabs share one window, whose location is the other tab's. */}
      <Form method="post" action="/sign-out">
        <button type="submit">Sign out here</button>
      </Form>
      <p>{state?.ok ? 'signed out' : 'signed in'}</p>
    </main>
  );
}

const Elsewhere = (props: Record<string, unknown>) => <h1>{`Elsewhere for ${String(props.user)}`}</h1>;

const modules = {
  '/app/account/page.tsx': async () => ({ default: Account }),
  '/app/sign-out/page.tsx': async () => ({ default: SignOut }),
  '/app/elsewhere/page.tsx': async () => ({ default: Elsewhere }),
};

const antiforgery = { formFieldName: '__RequestVerificationToken', headerName: 'RequestVerificationToken', requestToken: 'tok' };

function payload(page: string, url: string, overrides: Partial<PagePayload> = {}): PagePayload {
  return { kind: 'page', route: url, page, segments: [], props: {}, params: {}, shared: null, status: 200, url, version: 'v1', antiforgery, ...overrides };
}

/** The server: answers for whoever the cookie says, and counts the page loads. */
let signedIn = 'Ada';
let loads: string[] = [];
beforeEach(() => {
  signedIn = 'Ada';
  loads = [];
  const page = (file: string, path: string, basePath?: string) =>
    http.get(`${origin}${basePath ?? ''}${path}`, () => {
      loads.push(`${basePath ?? ''}${path}`);
      return HttpResponse.json(payload(file, `${basePath ?? ''}${path}`, { props: { user: signedIn }, ...(basePath ? { basePath } : {}) }));
    });
  const signedOut = () => {
    signedIn = 'visitor';
    return { 'X-Netjes-Cookies': 'changed' };
  };
  server.use(
    http.get(`${origin}/api/me`, () => HttpResponse.json(signedIn)),
    http.post(`${origin}/_netjes/actions/accounts.signOut`, () =>
      HttpResponse.json({ kind: 'action', ok: true, status: 200, result: null, revalidate: false }, { headers: signedOut() }),
    ),
    // The page action: signs out and redirects back to the page, as a form post answers.
    http.post(`${origin}/sign-out`, () => HttpResponse.json({ kind: 'redirect', location: '/sign-out', version: 'v1' }, { headers: signedOut() })),
    page('app/account/page.tsx', '/account'),
    page('app/account/page.tsx', '/account', '/other'),
    page('app/sign-out/page.tsx', '/sign-out'),
    page('app/elsewhere/page.tsx', '/elsewhere'),
  );
});

/** A tab: a router on its page, inside a container of its own. The browser stays where it is. */
async function renderTab(initial: PagePayload, props: Partial<RouterProviderProps> = {}) {
  const loader = createModuleLoader(modules);
  const initialModules = await loader.loadRoute(initial);
  let container: HTMLElement | undefined;
  await act(async () => {
    ({ container } = render(
      <RouterProvider initial={initial} initialModules={initialModules} loader={loader} errorPage={null} shell={createQueryShell()} {...props} />,
    ));
  });
  return within(container!);
}

const accountFor = (user: string) => payload('app/account/page.tsx', '/account', { props: { user } });
const signOutPage = () => payload('app/sign-out/page.tsx', '/sign-out', { props: { user: 'Ada' } });

describe('another tab of the app', () => {
  it.each([
    { how: 'a server action', button: 'Sign out' },
    { how: 'a page action', button: 'Sign out here' },
  ])("signing out with $how shows this tab's page and queries for whoever is signed in now", async ({ button }) => {
    window.history.replaceState(null, '', '/account');
    const tab = await renderTab(accountFor('Ada'));
    expect(await tab.findByText('Query says Ada')).toBeDefined();
    const posted = vi.spyOn(BroadcastChannel.prototype, 'postMessage');
    const other = await renderTab(signOutPage());

    await userEvent.setup().click(other.getByRole('button', { name: button }));

    expect(await tab.findByText('Page says visitor')).toBeDefined();
    expect(await tab.findByText('Query says visitor')).toBeDefined();
    // Each tab loads its page once: the one that signed out after its call or post, the other on its message. Neither
    // answers its own message or the other's.
    await vi.waitFor(() => expect(loads).toContain('/sign-out'));
    expect(loads.filter((path) => path === '/account')).toEqual(['/account']);
    expect(loads.filter((path) => path === '/sign-out')).toEqual(['/sign-out']);
    expect(posted).toHaveBeenCalledOnce();
  });

  it("signing out makes this tab load the pages behind it from the server, not from memory", async () => {
    window.history.replaceState(null, '', '/account');
    const user = userEvent.setup();
    const tab = await renderTab(accountFor('Ada'));
    await user.click(tab.getByRole('link', { name: 'Elsewhere' }));
    expect(await tab.findByRole('heading', { name: 'Elsewhere for Ada' })).toBeDefined();
    const other = await renderTab(signOutPage());

    await user.click(other.getByRole('button', { name: 'Sign out' }));
    expect(await tab.findByRole('heading', { name: 'Elsewhere for visitor' })).toBeDefined();
    act(() => window.history.back());

    expect(await tab.findByText('Page says visitor')).toBeDefined();
    expect(tab.queryByText('Page says Ada')).toBeNull();
  });

  it('signing out while a click here is on its way lands on that page, for whoever is signed in now', async () => {
    // The first answer for /elsewhere comes late: it went out with the cookies from before the sign-out.
    let answerLate!: () => void;
    const late = new Promise<void>((resolve) => (answerLate = resolve));
    let first = true;
    server.use(
      http.get(`${origin}/elsewhere`, async () => {
        loads.push('/elsewhere');
        if (first) {
          first = false;
          await late;
        }
        return HttpResponse.json(payload('app/elsewhere/page.tsx', '/elsewhere', { props: { user: signedIn } }));
      }),
    );
    window.history.replaceState(null, '', '/account');
    const user = userEvent.setup();
    const tab = await renderTab(accountFor('Ada'));
    const other = await renderTab(signOutPage());
    await user.click(tab.getByRole('link', { name: 'Elsewhere' }));
    await vi.waitFor(() => expect(loads).toContain('/elsewhere'));

    await user.click(other.getByRole('button', { name: 'Sign out' }));
    expect(await other.findByText('signed out')).toBeDefined();
    answerLate();

    // The click is started over, with the new cookies, and wins: the page it left is not loaded again.
    expect(await tab.findByRole('heading', { name: 'Elsewhere for visitor' })).toBeDefined();
    expect(loads.filter((path) => path === '/elsewhere')).toEqual(['/elsewhere', '/elsewhere']);
    expect(loads).not.toContain('/account');
  });

  it('signing out while a page is prefetched here prefetches it again once, and keeps it', async () => {
    let answerLate!: () => void;
    const late = new Promise<void>((resolve) => (answerLate = resolve));
    let first = true;
    server.use(
      http.get(`${origin}/elsewhere`, async () => {
        loads.push('/elsewhere');
        if (first) {
          first = false;
          await late;
        }
        return HttpResponse.json(payload('app/elsewhere/page.tsx', '/elsewhere', { props: { user: signedIn } }));
      }),
    );
    window.history.replaceState(null, '', '/account');
    const user = userEvent.setup();
    const tab = await renderTab(accountFor('Ada'));
    const other = await renderTab(signOutPage());
    await user.hover(tab.getByRole('link', { name: 'Elsewhere' }));
    await vi.waitFor(() => expect(loads).toContain('/elsewhere'));

    await user.click(other.getByRole('button', { name: 'Sign out' }));
    expect(await tab.findByText('Page says visitor')).toBeDefined();
    answerLate();
    await user.click(tab.getByRole('link', { name: 'Elsewhere' }));

    // The prefetch from before the sign-out started over, and the refresh of this tab's page kept the new one.
    expect(await tab.findByRole('heading', { name: 'Elsewhere for visitor' })).toBeDefined();
    expect(loads.filter((path) => path === '/elsewhere')).toEqual(['/elsewhere', '/elsewhere']);
  });

  it('changes nothing here where the browser has no BroadcastChannel', async () => {
    vi.stubGlobal('BroadcastChannel', undefined);
    window.history.replaceState(null, '', '/account');
    const tab = await renderTab(accountFor('Ada'));
    const other = await renderTab(signOutPage());

    await userEvent.setup().click(other.getByRole('button', { name: 'Sign out' }));
    expect(await other.findByText('signed out')).toBeDefined();
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 20));
    });

    expect(tab.getByText('Page says Ada')).toBeDefined();
    expect(loads).not.toContain('/account');
  });

  it('under another base path is another app: its sign-out changes nothing here', async () => {
    window.history.replaceState(null, '', '/other/account');
    const tab = await renderTab(payload('app/account/page.tsx', '/other/account', { basePath: '/other', props: { user: 'Ada' } }));
    const posted = vi.spyOn(BroadcastChannel.prototype, 'postMessage');
    const other = await renderTab(signOutPage());

    await userEvent.setup().click(other.getByRole('button', { name: 'Sign out' }));
    expect(await other.findByText('signed out')).toBeDefined();
    await vi.waitFor(() => expect(posted).toHaveBeenCalledOnce());
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 20));
    });

    expect(tab.getByText('Page says Ada')).toBeDefined();
    expect(loads).not.toContain('/other/account');
  });

  it("signing out leaves a tab alone that handed its page to a document the server sent", async () => {
    server.use(http.post(`${origin}/report`, () => new HttpResponse('<p>Reported</p>', { headers: { 'Content-Type': 'text/html' } })));
    const showDocument = vi.fn();
    window.history.replaceState(null, '', '/account');
    const tab = await renderTab(accountFor('Ada'), { showDocument });
    await userEvent.setup().click(tab.getByRole('button', { name: 'Report' }));
    await vi.waitFor(() => expect(showDocument).toHaveBeenCalledOnce());
    const posted = vi.spyOn(BroadcastChannel.prototype, 'postMessage');
    const other = await renderTab(signOutPage());

    await userEvent.setup().click(other.getByRole('button', { name: 'Sign out' }));
    expect(await other.findByText('signed out')).toBeDefined();
    await vi.waitFor(() => expect(posted).toHaveBeenCalledOnce());
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 20));
    });

    expect(loads).not.toContain('/account');
  });
});
