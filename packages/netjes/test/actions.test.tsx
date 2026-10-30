import { useQuery } from '@tanstack/react-query';
import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, delay, http } from 'msw';
import { setupServer } from 'msw/node';
import { useLayoutEffect, useOptimistic, useState } from 'react';
import { useFormStatus } from 'react-dom';
import { renderToString } from 'react-dom/server';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { z } from 'zod';
import { ServerActionError, serverAction, useAction } from '../src/actions.tsx';
import { Form } from '../src/form.tsx';
import { Link } from '../src/link.tsx';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload } from '../src/payload.ts';
import { createQueryShell } from '../src/query.tsx';
import { RouterProvider, useRouter } from '../src/router.tsx';

const origin = window.location.origin;
const server = setupServer();
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

const sign = serverAction<FormData, { id: number }>('guestbook.sign', { result: z.object({ id: z.number() }) });
const greet = serverAction<{ name: string }, string>('greet');

function SubmitButton() {
  const status = useFormStatus();
  return <button type="submit">{status.pending ? 'Signing…' : 'Sign'}</button>;
}

function Guestbook(props: Record<string, unknown>) {
  const [state, formAction, pending] = useAction(sign);
  const errors = state && !state.ok ? state.errors : {};
  const values = state && !state.ok ? state.values : undefined;
  return (
    <main>
      <h1>{`Guestbook ${String(props.count ?? 0)}`}</h1>
      <Form action={formAction}>
        <label>
          Name <input name="name" defaultValue={String(values?.name ?? '')} />
        </label>
        {errors.name && <p role="alert">{errors.name[0]}</p>}
        <SubmitButton />
      </Form>
      <p>{pending ? 'pending' : state?.ok ? `signed ${state.result.id}` : 'idle'}</p>
    </main>
  );
}

function Likes() {
  const likes = useQuery({ queryKey: ['likes', 'a'], queryFn: async () => (await (await fetch(`${origin}/api/likes`)).json()) as number });
  return <p>{`${likes.data ?? '?'} likes`}</p>;
}

function Greeter() {
  return (
    <button type="button" onClick={async () => document.body.setAttribute('data-greeting', JSON.stringify(await greet({ name: 'Ada' })))}>
      greet
    </button>
  );
}

/** Whether the navigation the "Push elsewhere" button started has finished. */
let pushed = false;

/** Whom the Elsewhere page was on screen for, in the order React showed it. */
let elsewhereShownFor: string[] = [];

function Elsewhere(props: Record<string, unknown>) {
  const user = typeof props.user === 'string' ? props.user : null;
  useLayoutEffect(() => {
    if (user) elsewhereShownFor.push(user);
  }, [user]);
  return (
    <>
      <h1>Elsewhere</h1>
      {user && <p>{`Elsewhere for ${user}`}</p>}
    </>
  );
}

function SignAndLeave(props: Record<string, unknown>) {
  const [state, formAction] = useAction(sign);
  const router = useRouter();
  return (
    <main>
      <h1>Sign and leave</h1>
      {typeof props.user === 'string' && <p>{`Signed in as ${props.user}`}</p>}
      <Form action={formAction}>
        <button type="submit">Sign</button>
      </Form>
      <Link href="/elsewhere">Elsewhere</Link>
      <button type="button" onClick={() => void router.push('/elsewhere').then(() => (pushed = true))}>
        Push elsewhere
      </button>
      <Link href="/leave#comments" prefetch="none">
        Comments
      </Link>
      <p>{state?.ok ? 'signed' : 'idle'}</p>
    </main>
  );
}

const saveNote = serverAction<{ title: string }, null>('notes.save');

function Notes() {
  const [outcome, setOutcome] = useState('idle');
  return (
    <main>
      <button
        type="button"
        onClick={() =>
          saveNote({ title: 'Groceries' }).then(
            () => setOutcome('saved'),
            (error: unknown) => setOutcome(error instanceof ServerActionError ? `failed ${error.status}` : 'failed'),
          )
        }
      >
        Save
      </button>
      <output>{outcome}</output>
    </main>
  );
}

const addTodo = serverAction<FormData, null>('todos.add');

function Todos(props: Record<string, unknown>) {
  const saved = (props.todos as string[] | undefined) ?? [];
  const [todos, addOptimistic] = useOptimistic(saved, (current, title: string) => [...current, `${title} (saving)`]);
  const [, formAction] = useAction(addTodo, { onSubmit: (data) => addOptimistic(String(data.get('title'))) });
  return (
    <main>
      <ul aria-label="Todos">{todos.map((todo) => <li key={todo}>{todo}</li>)}</ul>
      <Form action={formAction}>
        <input name="title" aria-label="Title" defaultValue="" />
        <button type="submit">Add</button>
      </Form>
    </main>
  );
}

const signUp = serverAction<FormData, null>('accounts.signUp');

function SignUp() {
  const [state, formAction] = useAction(signUp);
  return (
    <Form action={formAction}>
      <input name="email" aria-label="Email" defaultValue="" />
      <input name="password" aria-label="Password" type="password" defaultValue="" />
      <button type="submit">Sign up</button>
      <output>{state && !state.ok ? JSON.stringify(state.values) : ''}</output>
    </Form>
  );
}

const signOut = serverAction<FormData, null>('accounts.signOut');

/** Who the client's query cache says is signed in, from `/api/me` or seeded by the page. */
function Account() {
  const me = useQuery({ queryKey: ['me'], queryFn: async () => (await (await fetch(`${origin}/api/me`)).json()) as string });
  return (
    <main>
      <p>{me.data ? `Query says ${me.data}` : 'Query loading'}</p>
      <Link href="/sign-out">Sign out page</Link>
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
      <Link href="/account">Account</Link>
      <p>{state?.ok ? 'signed out' : 'signed in'}</p>
    </main>
  );
}

/** A page action's form next to a server action, which may sign someone in or out while the form posts. */
function SaveAndGreet(props: Record<string, unknown>) {
  return (
    <main>
      <h1>Save and greet</h1>
      {typeof props.user === 'string' && <p>{`Saved for ${props.user}`}</p>}
      <Form method="post">
        <button type="submit">Save</button>
      </Form>
      <Greeter />
      <Link href="/elsewhere">Elsewhere</Link>
    </main>
  );
}

const modules = {
  '/app/account/page.tsx': async () => ({ default: Account }),
  '/app/sign-out/page.tsx': async () => ({ default: SignOut }),
  '/app/me/page.tsx': async () => ({
    default: () => (
      <>
        <Account />
        <SignOut />
      </>
    ),
  }),
  '/app/save/page.tsx': async () => ({ default: SaveAndGreet }),
  '/app/signup/page.tsx': async () => ({ default: SignUp }),
  '/app/leave/page.tsx': async () => ({ default: SignAndLeave }),
  '/app/elsewhere/page.tsx': async () => ({ default: Elsewhere }),
  '/app/todos/page.tsx': async () => ({ default: Todos }),
  '/app/notes/page.tsx': async () => ({ default: Notes }),
  '/app/guestbook/page.tsx': async () => ({ default: Guestbook }),
  '/app/likes/page.tsx': async () => ({ default: () => <><Likes /><Greeter /></> }),
};

const antiforgery = { formFieldName: '__RequestVerificationToken', headerName: 'RequestVerificationToken', requestToken: 'tok' };

function payload(page: string, url: string, overrides: Partial<PagePayload> = {}): PagePayload {
  return { kind: 'page', route: url, page, segments: [], props: {}, params: {}, shared: null, status: 200, url, version: 'v1', antiforgery, ...overrides };
}

async function renderApp(initial: PagePayload, { hardNavigate }: { hardNavigate?: (href: string) => void } = {}) {
  window.history.replaceState(null, '', initial.url);
  const loader = createModuleLoader(modules);
  const initialModules = await loader.loadRoute(initial);
  await act(async () => {
    render(
      <RouterProvider
        initial={initial}
        initialModules={initialModules}
        loader={loader}
        errorPage={null}
        shell={createQueryShell()}
        {...(hardNavigate ? { hardNavigate } : {})}
      />,
    );
  });
  return userEvent.setup();
}

const action = (body: Record<string, unknown>, status = 200) => HttpResponse.json({ kind: 'action', revalidate: false, ...body }, { status });

/** The server's answer to a call whose antiforgery token it refused: marked, because the action did not run. */
const tokenRejected = () =>
  HttpResponse.json(
    { kind: 'action', revalidate: false, ok: false, status: 400, message: 'Invalid antiforgery token.' },
    { status: 400, headers: { 'X-Netjes-Antiforgery': 'invalid' } },
  );

describe('server actions', () => {
  it('run onSubmit inside the action, where useOptimistic updates belong', async () => {
    let release: () => void = () => {};
    const answered = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.post(`${origin}/_netjes/actions/todos.add`, async () => {
        await answered;
        return action({ ok: true, status: 200, result: null, revalidate: true });
      }),
      http.get(`${origin}/todos`, () => HttpResponse.json(payload('app/todos/page.tsx', '/todos', { props: { todos: ['Ship it'] } }))),
    );
    const user = await renderApp(payload('app/todos/page.tsx', '/todos', { props: { todos: [] } }));

    await user.type(screen.getByLabelText('Title'), 'Ship it');
    await user.click(screen.getByRole('button', { name: 'Add' }));

    expect(await screen.findByText('Ship it (saving)')).toBeDefined();
    release();
    expect(await screen.findByText('Ship it')).toBeDefined();
    expect(screen.queryByText('Ship it (saving)')).toBeNull();
  });

  it('post the form with the antiforgery token and show validation errors', async () => {
    const requests: Request[] = [];
    server.use(
      http.post(`${origin}/_netjes/actions/guestbook.sign`, async ({ request }) => {
        requests.push(request.clone());
        await delay(20);
        return action({ ok: false, status: 422, errors: { name: ['Tell us your name.'] } }, 422);
      }),
    );
    const user = await renderApp(payload('app/guestbook/page.tsx', '/guestbook'));

    await user.click(screen.getByRole('button', { name: 'Sign' }));
    expect(await screen.findByRole('button', { name: 'Signing…' })).toBeDefined();

    expect((await screen.findByRole('alert')).textContent).toBe('Tell us your name.');
    expect(requests[0]?.headers.get('RequestVerificationToken')).toBe('tok');
    expect(requests[0]?.headers.get('X-Netjes-Data')).toBe('1');
    const body = new URLSearchParams(await requests[0]!.text());
    expect(body.get('name')).toBe('');
    expect(body.get('__netjes_return')).toBe('/guestbook');
  });

  it('keep what the user typed when the action rejects it (React resets the form after an action)', async () => {
    server.use(
      http.post(`${origin}/_netjes/actions/guestbook.sign`, () => action({ ok: false, status: 422, errors: { name: ['Too short.'] } }, 422)),
    );
    const user = await renderApp(payload('app/guestbook/page.tsx', '/guestbook'));

    await user.type(screen.getByLabelText('Name'), 'Ad');
    await user.click(screen.getByRole('button', { name: 'Sign' }));

    expect((await screen.findByRole('alert')).textContent).toBe('Too short.');
    expect(screen.getByLabelText('Name')).toHaveProperty('value', 'Ad');
  });

  it('hand back only the fields the server names, as a post without JavaScript would', async () => {
    server.use(
      http.post(`${origin}/_netjes/actions/accounts.signUp`, () =>
        action({ ok: false, status: 422, errors: { email: ['Taken.'] }, fields: ['email'] }, 422),
      ),
    );
    const user = await renderApp(payload('app/signup/page.tsx', '/signup'));

    await user.type(screen.getByLabelText('Email'), 'ada@example.test');
    await user.type(screen.getByLabelText('Password'), 'hunter2');
    await user.click(screen.getByRole('button', { name: 'Sign up' }));

    expect(await screen.findByText('{"email":"ada@example.test"}')).toBeDefined();
  });

  it('fetch a token first on pages that carry none (output-cached ones)', async () => {
    const tokens: (string | null)[] = [];
    server.use(
      http.get(`${origin}/_netjes/antiforgery`, () =>
        HttpResponse.json({ formFieldName: '__RequestVerificationToken', headerName: 'RequestVerificationToken', requestToken: 'fresh' }),
      ),
      http.post(`${origin}/_netjes/actions/greet`, ({ request }) => {
        tokens.push(request.headers.get('RequestVerificationToken'));
        return action({ ok: true, status: 200, result: 'hi' });
      }),
      http.get(`${origin}/api/likes`, () => HttpResponse.json(1)),
    );
    const { antiforgery: _, ...cached } = payload('app/likes/page.tsx', '/likes');
    const user = await renderApp(cached);

    await user.click(screen.getByRole('button', { name: 'greet' }));
    await user.click(screen.getByRole('button', { name: 'greet' }));

    await expect.poll(() => tokens).toEqual(['fresh', 'fresh']);
  });

  it('fetch the token again after an action, since signing in or out changes it', async () => {
    const issued = ['one', 'two'];
    const tokens: (string | null)[] = [];
    server.use(
      http.get(`${origin}/_netjes/antiforgery`, () =>
        HttpResponse.json({ formFieldName: '__RequestVerificationToken', headerName: 'RequestVerificationToken', requestToken: issued.shift() }),
      ),
      http.post(`${origin}/_netjes/actions/greet`, ({ request }) => {
        tokens.push(request.headers.get('RequestVerificationToken'));
        return action({ ok: true, status: 200, result: 'hi' });
      }),
      http.get(`${origin}/api/likes`, () => HttpResponse.json(1)),
    );
    const { antiforgery: _, ...cached } = payload('app/likes/page.tsx', '/likes');
    const user = await renderApp(cached);

    await user.click(screen.getByRole('button', { name: 'greet' }));
    await expect.poll(() => tokens.length).toBe(1);
    await user.click(screen.getByRole('button', { name: 'greet' }));

    await expect.poll(() => tokens).toEqual(['one', 'two']);
  });

  it('call an action once when it answers 400 for anything but the token: it may have changed data first', async () => {
    const tokens: (string | null)[] = [];
    server.use(
      http.get(`${origin}/_netjes/antiforgery`, () => HttpResponse.json({ ...antiforgery, requestToken: 'fresh' })),
      http.post(`${origin}/_netjes/actions/notes.save`, ({ request }) => {
        tokens.push(request.headers.get('RequestVerificationToken'));
        // Saved, then answered Results.BadRequest() (or threw a BadHttpRequestException).
        return action({ ok: false, status: 400, message: 'Bad Request' }, 400);
      }),
    );
    const user = await renderApp(payload('app/notes/page.tsx', '/notes'));

    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('failed 400')).toBeDefined();
    expect(tokens).toEqual(['tok']);
  });

  it('retry once with a fresh token when the server rejects the one it had', async () => {
    const tokens: (string | null)[] = [];
    server.use(
      http.get(`${origin}/_netjes/antiforgery`, () =>
        HttpResponse.json({ formFieldName: '__RequestVerificationToken', headerName: 'RequestVerificationToken', requestToken: 'fresh' }),
      ),
      http.post(`${origin}/_netjes/actions/greet`, ({ request }) => {
        const token = request.headers.get('RequestVerificationToken');
        tokens.push(token);
        return token === 'fresh' ? action({ ok: true, status: 200, result: 'hi' }) : tokenRejected();
      }),
      http.get(`${origin}/api/likes`, () => HttpResponse.json(1)),
    );
    // The page's own token went stale: the user signed in on another tab.
    const user = await renderApp(payload('app/likes/page.tsx', '/likes', { antiforgery: { ...antiforgery, requestToken: 'stale' } }));

    await user.click(screen.getByRole('button', { name: 'greet' }));

    await expect.poll(() => document.body.getAttribute('data-greeting')).toBe(JSON.stringify({ ok: true, result: 'hi' }));
    expect(tokens).toEqual(['stale', 'fresh']);
  });

  it('retry a rejected token only once: a second rejection goes to the caller', async () => {
    const tokens: (string | null)[] = [];
    const issued = ['fresh', 'fresher'];
    server.use(
      http.get(`${origin}/_netjes/antiforgery`, () => HttpResponse.json({ ...antiforgery, requestToken: issued.shift() })),
      http.post(`${origin}/_netjes/actions/notes.save`, ({ request }) => {
        tokens.push(request.headers.get('RequestVerificationToken'));
        return tokenRejected();
      }),
    );
    const user = await renderApp(payload('app/notes/page.tsx', '/notes', { antiforgery: { ...antiforgery, requestToken: 'stale' } }));

    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('failed 400')).toBeDefined();
    expect(tokens).toEqual(['stale', 'fresh']);
  });

  it('do not pull the user back when they navigated while the action ran', async () => {
    let finish: () => void = () => {};
    const done = new Promise<void>((resolve) => {
      finish = resolve;
    });
    const pageRequests: string[] = [];
    server.use(
      http.post(`${origin}/_netjes/actions/guestbook.sign`, async () => {
        await done;
        return action({ ok: true, status: 200, result: { id: 1 }, redirect: '/likes', revalidate: true });
      }),
      http.get(`${origin}/api/settled`, () => HttpResponse.json(true)),
      http.get(`${origin}/elsewhere`, ({ request }) => {
        pageRequests.push(new URL(request.url).pathname);
        return HttpResponse.json(payload('app/elsewhere/page.tsx', '/elsewhere'));
      }),
      http.get(`${origin}/likes`, ({ request }) => {
        pageRequests.push(new URL(request.url).pathname);
        return HttpResponse.json(payload('app/likes/page.tsx', '/likes'));
      }),
    );
    const user = await renderApp(payload('app/leave/page.tsx', '/leave'));

    await user.click(screen.getByRole('button', { name: 'Sign' }));
    await user.click(screen.getByRole('link', { name: 'Elsewhere' }));
    // React holds other transitions (this navigation) until the pending action finishes.
    await expect.poll(() => pageRequests).toEqual(['/elsewhere']);
    const answered = new Promise<void>((resolve) => {
      const onResponse = ({ request }: { request: Request }) => {
        if (!request.url.endsWith('/_netjes/actions/guestbook.sign')) return;
        server.events.removeListener('response:mocked', onResponse);
        resolve();
      };
      server.events.on('response:mocked', onResponse);
    });
    await act(async () => finish());

    expect(await screen.findByRole('heading', { name: 'Elsewhere' })).toBeDefined();
    // The action has answered; whatever the router does next (a redirect, a refresh) starts a request before another
    // round trip to the server returns, so one more request is a signal that nothing else was sent.
    await answered;
    await fetch(`${origin}/api/settled`);
    expect(window.location.pathname).toBe('/elsewhere');
    expect(pageRequests).toEqual(['/elsewhere']);
  });

  it('resolve to the parsed result and refresh the page when the action revalidates', async () => {
    const pageRequests: (string | null)[] = [];
    server.use(
      http.post(`${origin}/_netjes/actions/guestbook.sign`, () => action({ ok: true, status: 200, result: { id: 7 }, revalidate: true })),
      http.get(`${origin}/guestbook`, ({ request }) => {
        pageRequests.push(request.headers.get('X-Netjes-Segments'));
        return HttpResponse.json(payload('app/guestbook/page.tsx', '/guestbook', { props: { count: 1 } }));
      }),
    );
    const user = await renderApp(payload('app/guestbook/page.tsx', '/guestbook'));

    await user.type(screen.getByLabelText('Name'), 'Ada');
    await user.click(screen.getByRole('button', { name: 'Sign' }));

    expect(await screen.findByText('signed 7')).toBeDefined();
    expect(await screen.findByRole('heading', { name: 'Guestbook 1' })).toBeDefined();
    expect(pageRequests).toEqual([null]);
  });

  it('navigate when the action redirects', async () => {
    server.use(
      http.post(`${origin}/_netjes/actions/guestbook.sign`, () => action({ ok: true, status: 200, result: { id: 1 }, redirect: '/likes' })),
      http.get(`${origin}/api/likes`, () => HttpResponse.json(3)),
      http.get(`${origin}/likes`, () => HttpResponse.json(payload('app/likes/page.tsx', '/likes'))),
    );
    const user = await renderApp(payload('app/guestbook/page.tsx', '/guestbook'));

    await user.click(screen.getByRole('button', { name: 'Sign' }));

    expect(await screen.findByText('3 likes')).toBeDefined();
    expect(window.location.pathname).toBe('/likes');
  });

  // Signing in or out: the pages the router kept for back and forward showed whoever was signed in before. The page
  // the action ran on is one of them once the action has redirected.
  it.each([
    { cookies: 'changed', loads: ['/leave'] },
    { cookies: null, loads: [] },
  ])('load the pages behind them from the server when they change cookies (X-Netjes-Cookies: $cookies)', async ({ cookies, loads }) => {
    const pageRequests: string[] = [];
    server.use(
      http.post(`${origin}/_netjes/actions/guestbook.sign`, () =>
        HttpResponse.json(
          { kind: 'action', ok: true, status: 200, result: { id: 1 }, redirect: '/elsewhere', revalidate: false },
          { headers: cookies ? { 'X-Netjes-Cookies': cookies } : {} },
        ),
      ),
      http.get(`${origin}/elsewhere`, () => HttpResponse.json(payload('app/elsewhere/page.tsx', '/elsewhere'))),
      http.get(`${origin}/leave`, ({ request }) => {
        pageRequests.push(new URL(request.url).pathname);
        return HttpResponse.json(payload('app/leave/page.tsx', '/leave'));
      }),
    );
    const user = await renderApp(payload('app/leave/page.tsx', '/leave'));

    await user.click(screen.getByRole('button', { name: 'Sign' }));
    await screen.findByRole('heading', { name: 'Elsewhere' });
    act(() => window.history.back());

    expect(await screen.findByRole('heading', { name: 'Sign and leave' })).toBeDefined();
    expect(pageRequests).toEqual(loads);
  });

  it('load the pages ahead of them from the server when they change cookies', async () => {
    const pageRequests: string[] = [];
    server.use(
      http.post(`${origin}/_netjes/actions/guestbook.sign`, () =>
        HttpResponse.json(
          { kind: 'action', ok: true, status: 200, result: { id: 1 }, revalidate: false },
          { headers: { 'X-Netjes-Cookies': 'changed' } },
        ),
      ),
      http.get(`${origin}/elsewhere`, ({ request }) => {
        pageRequests.push(new URL(request.url).pathname);
        return HttpResponse.json(payload('app/elsewhere/page.tsx', '/elsewhere'));
      }),
      http.get(`${origin}/leave`, ({ request }) => {
        pageRequests.push(new URL(request.url).pathname);
        return HttpResponse.json(payload('app/leave/page.tsx', '/leave'));
      }),
    );
    const user = await renderApp(payload('app/leave/page.tsx', '/leave'));
    await user.click(screen.getByRole('link', { name: 'Elsewhere' }));
    await screen.findByRole('heading', { name: 'Elsewhere' });
    act(() => window.history.back());
    await screen.findByRole('heading', { name: 'Sign and leave' });

    await user.click(screen.getByRole('button', { name: 'Sign' }));
    expect(await screen.findByText('signed')).toBeDefined();
    act(() => window.history.forward());

    expect(await screen.findByRole('heading', { name: 'Elsewhere' })).toBeDefined();
    expect(pageRequests).toEqual(['/elsewhere', '/leave', '/elsewhere']);
  });

  // The page stays on screen as it was until its refresh arrives. A fragment link followed on it meanwhile supersedes the
  // refresh, and must not keep the page for the entry it adds. Pages loaded after the change are kept again.
  it('load the page they changed cookies on from the server after a fragment link followed before its refresh', async () => {
    let release: () => void = () => {};
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    const pageRequests: string[] = [];
    server.use(
      http.post(`${origin}/_netjes/actions/guestbook.sign`, () =>
        HttpResponse.json(
          { kind: 'action', ok: true, status: 200, result: { id: 1 }, revalidate: false },
          { headers: { 'X-Netjes-Cookies': 'changed' } },
        ),
      ),
      http.get(`${origin}/elsewhere`, ({ request }) => {
        pageRequests.push(new URL(request.url).pathname);
        return HttpResponse.json(payload('app/elsewhere/page.tsx', '/elsewhere'));
      }),
      http.get(`${origin}/leave`, async ({ request }) => {
        pageRequests.push(new URL(request.url).pathname);
        // The refresh after the action waits; the load after going back does not.
        if (pageRequests.length === 1) await held;
        return HttpResponse.json(payload('app/leave/page.tsx', '/leave'));
      }),
    );
    const user = await renderApp(payload('app/leave/page.tsx', '/leave'));
    await user.click(screen.getByRole('button', { name: 'Sign' }));
    await expect.poll(() => pageRequests).toEqual(['/leave']);

    await user.click(screen.getByRole('link', { name: 'Comments' }));
    release();
    expect(await screen.findByText('signed')).toBeDefined();
    await user.click(screen.getByRole('link', { name: 'Elsewhere' }));
    await screen.findByRole('heading', { name: 'Elsewhere' });
    act(() => window.history.back());

    expect(await screen.findByRole('heading', { name: 'Sign and leave' })).toBeDefined();
    expect(window.location.hash).toBe('#comments');
    expect(pageRequests).toEqual(['/leave', '/elsewhere', '/leave']);
    act(() => window.history.forward());

    expect(await screen.findByRole('heading', { name: 'Elsewhere' })).toBeDefined();
    expect(pageRequests).toEqual(['/leave', '/elsewhere', '/leave']);
  });

  /**
   * A signing in or out that runs while the user moves on: the navigation's request went out with the old cookie, and
   * React holds its page until the action finishes. The server's handlers answer for whoever the cookie says.
   */
  function userChangeWhileMovingOn() {
    let signedIn = 'Ada';
    let finish: () => void = () => {};
    const finished = new Promise<void>((resolve) => {
      finish = resolve;
    });
    const pageRequests: string[] = [];
    const elsewhere: { user: string; answer: () => void }[] = [];
    server.use(
      http.post(`${origin}/_netjes/actions/guestbook.sign`, async () => {
        await finished;
        signedIn = 'Grace';
        return HttpResponse.json(
          { kind: 'action', ok: true, status: 200, result: { id: 1 }, revalidate: false },
          { headers: { 'X-Netjes-Cookies': 'changed' } },
        );
      }),
      http.get(`${origin}/elsewhere`, async ({ request }) => {
        pageRequests.push(new URL(request.url).pathname);
        const user = signedIn;
        await new Promise<void>((answer) => elsewhere.push({ user, answer }));
        return HttpResponse.json(payload('app/elsewhere/page.tsx', '/elsewhere', { props: { user } }));
      }),
      http.get(`${origin}/leave`, ({ request }) => {
        pageRequests.push(new URL(request.url).pathname);
        return HttpResponse.json(payload('app/leave/page.tsx', '/leave', { props: { user: signedIn } }));
      }),
    );
    return { pageRequests, elsewhere, finish };
  }

  it('load a page again when cookies change while it is loading', async () => {
    const { pageRequests, elsewhere, finish } = userChangeWhileMovingOn();
    elsewhereShownFor = [];
    const user = await renderApp(payload('app/leave/page.tsx', '/leave', { props: { user: 'Ada' } }));

    await user.click(screen.getByRole('button', { name: 'Sign' }));
    await user.click(screen.getByRole('link', { name: 'Elsewhere' }));
    await expect.poll(() => pageRequests).toEqual(['/elsewhere']);
    await act(async () => finish());
    await expect.poll(() => pageRequests).toEqual(['/elsewhere', '/elsewhere']);
    await act(async () => elsewhere.forEach((request) => request.answer()));

    expect(await screen.findByText('Elsewhere for Grace')).toBeDefined();
    expect(elsewhere.map((request) => request.user)).toEqual(['Ada', 'Grace']);
    // Every request has its answer, and the page loaded with the old cookie was never on screen.
    expect(elsewhereShownFor).toEqual(['Grace']);
    expect(window.location.pathname).toBe('/elsewhere');
  });

  it('load a page again when cookies change after it loaded, before React showed it', async () => {
    const { pageRequests, elsewhere, finish } = userChangeWhileMovingOn();
    elsewhereShownFor = [];
    const user = await renderApp(payload('app/leave/page.tsx', '/leave', { props: { user: 'Ada' } }));

    await user.click(screen.getByRole('button', { name: 'Sign' }));
    await user.click(screen.getByRole('link', { name: 'Elsewhere' }));
    await expect.poll(() => elsewhere.length).toBe(1);
    elsewhere[0]!.answer();
    // The router has its page (and moved the URL); React shows it once the action is done.
    await expect.poll(() => window.location.pathname).toBe('/elsewhere');
    await act(async () => finish());
    await expect.poll(() => elsewhere.length).toBe(2);
    await act(async () => elsewhere[1]!.answer());

    expect(await screen.findByText('Elsewhere for Grace')).toBeDefined();
    expect(pageRequests).toEqual(['/elsewhere', '/elsewhere']);
    expect(elsewhereShownFor).toEqual(['Grace']);
  });

  it('finish a navigation started over when cookies changed once the page loaded again is in', async () => {
    const { elsewhere, finish } = userChangeWhileMovingOn();
    pushed = false;
    const user = await renderApp(payload('app/leave/page.tsx', '/leave', { props: { user: 'Ada' } }));

    await user.click(screen.getByRole('button', { name: 'Sign' }));
    await user.click(screen.getByRole('button', { name: 'Push elsewhere' }));
    await expect.poll(() => elsewhere.length).toBe(1);
    await act(async () => finish());
    await expect.poll(() => elsewhere.length).toBe(2);
    elsewhere[0]!.answer();
    await act(() => new Promise((r) => setTimeout(r, 0)));
    expect(pushed).toBe(false);
    await act(async () => elsewhere[1]!.answer());

    await expect.poll(() => pushed).toBe(true);
    expect(await screen.findByText('Elsewhere for Grace')).toBeDefined();
  });

  it('prefetch a page again when cookies change while it is prefetched', async () => {
    const { pageRequests, elsewhere, finish } = userChangeWhileMovingOn();
    const user = await renderApp(payload('app/leave/page.tsx', '/leave', { props: { user: 'Ada' } }));

    await user.hover(screen.getByRole('link', { name: 'Elsewhere' }));
    await expect.poll(() => elsewhere.length).toBe(1);
    finish();
    await user.click(screen.getByRole('button', { name: 'Sign' }));
    expect(await screen.findByText('Signed in as Grace')).toBeDefined();
    await expect.poll(() => elsewhere.map((request) => request.user)).toEqual(['Ada', 'Grace']);
    elsewhere.forEach((request) => request.answer());
    await user.click(screen.getByRole('link', { name: 'Elsewhere' }));

    expect(await screen.findByText('Elsewhere for Grace')).toBeDefined();
    // The prefetch starts over as soon as the answer says cookies changed, before the refresh after the action.
    expect(pageRequests).toEqual(['/elsewhere', '/elsewhere', '/leave']);
  });

  // A post is never sent again: what it answers is shown as it is, and loaded from the server once the user leaves it.
  it('keep the page a form post answered for no history entry when cookies changed while it ran', async () => {
    let signedIn = 'Ada';
    let answer: () => void = () => {};
    const answered = new Promise<void>((resolve) => {
      answer = resolve;
    });
    const requests: string[] = [];
    server.use(
      http.post(`${origin}/save`, async () => {
        requests.push('POST /save');
        const user = signedIn;
        await answered;
        return HttpResponse.json(payload('app/save/page.tsx', '/save', { props: { user } }));
      }),
      http.get(`${origin}/save`, () => {
        requests.push('GET /save');
        return HttpResponse.json(payload('app/save/page.tsx', '/save', { props: { user: signedIn } }));
      }),
      http.get(`${origin}/elsewhere`, () => HttpResponse.json(payload('app/elsewhere/page.tsx', '/elsewhere'))),
      http.post(`${origin}/_netjes/actions/greet`, () => {
        requests.push('greet');
        signedIn = 'Grace';
        return HttpResponse.json(
          { kind: 'action', ok: true, status: 200, result: 'hi', revalidate: false },
          { headers: { 'X-Netjes-Cookies': 'changed' } },
        );
      }),
    );
    const user = await renderApp(payload('app/save/page.tsx', '/save'));

    await user.click(screen.getByRole('button', { name: 'Save' }));
    await expect.poll(() => requests).toEqual(['POST /save']);
    await user.click(screen.getByRole('button', { name: 'greet' }));
    await expect.poll(() => requests).toEqual(['POST /save', 'greet']);
    await act(async () => answer());
    expect(await screen.findByText('Saved for Ada')).toBeDefined();
    await user.click(screen.getByRole('link', { name: 'Elsewhere' }));
    await screen.findByRole('heading', { name: 'Elsewhere' });
    act(() => window.history.back());

    expect(await screen.findByText('Saved for Grace')).toBeDefined();
    expect(requests).toEqual(['POST /save', 'greet', 'GET /save']);
  });

  it('load the pages behind them from the server when a call sent again with a fresh token changes cookies', async () => {
    const pageRequests: string[] = [];
    server.use(
      http.get(`${origin}/_netjes/antiforgery`, () => HttpResponse.json({ ...antiforgery, requestToken: 'fresh' })),
      http.post(`${origin}/_netjes/actions/guestbook.sign`, ({ request }) =>
        request.headers.get('RequestVerificationToken') === 'fresh'
          ? HttpResponse.json(
              { kind: 'action', ok: true, status: 200, result: { id: 1 }, redirect: '/elsewhere', revalidate: false },
              { headers: { 'X-Netjes-Cookies': 'changed' } },
            )
          : tokenRejected(),
      ),
      http.get(`${origin}/elsewhere`, () => HttpResponse.json(payload('app/elsewhere/page.tsx', '/elsewhere'))),
      http.get(`${origin}/leave`, ({ request }) => {
        pageRequests.push(new URL(request.url).pathname);
        return HttpResponse.json(payload('app/leave/page.tsx', '/leave'));
      }),
    );
    const user = await renderApp(payload('app/leave/page.tsx', '/leave', { antiforgery: { ...antiforgery, requestToken: 'stale' } }));

    await user.click(screen.getByRole('button', { name: 'Sign' }));
    await screen.findByRole('heading', { name: 'Elsewhere' });
    act(() => window.history.back());

    expect(await screen.findByRole('heading', { name: 'Sign and leave' })).toBeDefined();
    expect(pageRequests).toEqual(['/leave']);
  });

  // Like Next.js, where cookies an action sets refresh the page: it shows whoever is signed in now. One load each: the
  // redirect or the refresh the action asks for is that load. A redirect to a fragment of the page loads nothing itself.
  it.each([
    { cookies: 'changed', answer: {}, loads: ['/leave'], shown: 'Signed in as Grace', at: '/leave' },
    { cookies: 'changed', answer: { revalidate: true }, loads: ['/leave'], shown: 'Signed in as Grace', at: '/leave' },
    { cookies: 'changed', answer: { redirect: '/elsewhere' }, loads: ['/elsewhere'], shown: 'Elsewhere for Grace', at: '/elsewhere' },
    { cookies: 'changed', answer: { redirect: '/leave#done' }, loads: ['/leave'], shown: 'Signed in as Grace', at: '/leave#done' },
    { cookies: null, answer: {}, loads: [], shown: 'Signed in as Ada', at: '/leave' },
  ])('load the page from the server once when they change cookies (X-Netjes-Cookies: $cookies, $answer)', async ({ cookies, answer, loads, shown, at }) => {
    let signedIn = 'Ada';
    const pageRequests: string[] = [];
    server.use(
      http.post(`${origin}/_netjes/actions/guestbook.sign`, () => {
        if (cookies) signedIn = 'Grace';
        return HttpResponse.json(
          { kind: 'action', ok: true, status: 200, result: { id: 1 }, revalidate: false, ...answer },
          { headers: cookies ? { 'X-Netjes-Cookies': cookies } : {} },
        );
      }),
      http.get(`${origin}/elsewhere`, ({ request }) => {
        pageRequests.push(new URL(request.url).pathname);
        return HttpResponse.json(payload('app/elsewhere/page.tsx', '/elsewhere', { props: { user: signedIn } }));
      }),
      http.get(`${origin}/leave`, ({ request }) => {
        pageRequests.push(new URL(request.url).pathname);
        return HttpResponse.json(payload('app/leave/page.tsx', '/leave', { props: { user: signedIn } }));
      }),
    );
    const user = await renderApp(payload('app/leave/page.tsx', '/leave', { props: { user: signedIn } }));

    await user.click(screen.getByRole('button', { name: 'Sign' }));

    expect(await screen.findByText(shown)).toBeDefined();
    // The action is done, and with it every load it starts, once its state shows. React shows the page it redirected
    // to only then.
    if (screen.queryByRole('heading', { name: 'Sign and leave' })) await screen.findByText('signed');
    expect(pageRequests).toEqual(loads);
    expect(window.location.pathname + window.location.hash).toBe(at);
  });

  // Signing in with another site (OAuth, say) stores a cookie and sends the browser there: the page it leaves is not
  // loaded again on its way out.
  it('load no page when they change cookies and redirect to another site', async () => {
    const pageRequests: string[] = [];
    server.use(
      http.post(`${origin}/_netjes/actions/guestbook.sign`, () =>
        HttpResponse.json(
          { kind: 'action', ok: true, status: 200, result: { id: 1 }, redirect: 'https://id.example/authorize', revalidate: false },
          { headers: { 'X-Netjes-Cookies': 'changed' } },
        ),
      ),
      http.get(`${origin}/leave`, ({ request }) => {
        pageRequests.push(new URL(request.url).pathname);
        return HttpResponse.json(payload('app/leave/page.tsx', '/leave'));
      }),
    );
    const hardNavigate = vi.fn();
    const user = await renderApp(payload('app/leave/page.tsx', '/leave'), { hardNavigate });

    await user.click(screen.getByRole('button', { name: 'Sign' }));

    expect(await screen.findByText('signed')).toBeDefined();
    expect(hardNavigate).toHaveBeenCalledWith('https://id.example/authorize');
    expect(pageRequests).toEqual([]);
  });

  /** Signing out on /sign-out; `/api/me` answers for whoever the cookie says. */
  function signOutLater(answer: Record<string, unknown> = {}) {
    let signedIn = 'Ada';
    const fetched: string[] = [];
    server.use(
      http.get(`${origin}/api/me`, () => {
        fetched.push(signedIn);
        return HttpResponse.json(signedIn);
      }),
      http.post(`${origin}/_netjes/actions/accounts.signOut`, () => {
        signedIn = 'visitor';
        return HttpResponse.json(
          { kind: 'action', ok: true, status: 200, result: null, revalidate: false, ...answer },
          { headers: { 'X-Netjes-Cookies': 'changed' } },
        );
      }),
      http.get(`${origin}/sign-out`, () => HttpResponse.json(payload('app/sign-out/page.tsx', '/sign-out'))),
      http.get(`${origin}/account`, () => HttpResponse.json(payload('app/account/page.tsx', '/account'))),
    );
    return fetched;
  }

  // The QueryClient outlives navigations: a query from before the change, fetched by the client or seeded by a page
  // rendered for the previous user, would show that user's data.
  it.each([{ seeded: false }, { seeded: true }])('drop the cached queries when they change cookies (seeded: $seeded)', async ({ seeded }) => {
    const fetched = signOutLater();
    const queries = seeded ? [{ key: ['me'], data: 'Ada', updatedAt: Date.now() }] : undefined;
    const user = await renderApp(payload('app/account/page.tsx', '/account', queries ? { queries } : {}));
    expect(await screen.findByText('Query says Ada')).toBeDefined();

    await user.click(screen.getByRole('link', { name: 'Sign out page' }));
    await user.click(await screen.findByRole('button', { name: 'Sign out' }));
    expect(await screen.findByText('signed out')).toBeDefined();
    await user.click(screen.getByRole('link', { name: 'Account' }));

    expect(await screen.findByText('Query says visitor')).toBeDefined();
    expect(fetched).toEqual(seeded ? ['visitor'] : ['Ada', 'visitor']);
  });

  // Its components render again with the page loaded after the change, and their queries start over.
  it('load the queries on the page they change cookies on again', async () => {
    const fetched = signOutLater();
    server.use(http.get(`${origin}/me`, () => HttpResponse.json(payload('app/me/page.tsx', '/me'))));
    const user = await renderApp(payload('app/me/page.tsx', '/me'));
    expect(await screen.findByText('Query says Ada')).toBeDefined();

    await user.click(screen.getByRole('button', { name: 'Sign out' }));

    expect(await screen.findByText('signed out')).toBeDefined();
    expect(await screen.findByText('Query says visitor')).toBeDefined();
    expect(fetched).toEqual(['Ada', 'visitor']);
  });

  it('keep the query entries a call that changes cookies sends itself', async () => {
    const fetched = signOutLater({ queries: [{ key: ['me'], data: 'visitor', updatedAt: Date.now() }] });
    const user = await renderApp(payload('app/account/page.tsx', '/account'));
    expect(await screen.findByText('Query says Ada')).toBeDefined();

    await user.click(screen.getByRole('link', { name: 'Sign out page' }));
    await user.click(await screen.findByRole('button', { name: 'Sign out' }));
    expect(await screen.findByText('signed out')).toBeDefined();
    await user.click(screen.getByRole('link', { name: 'Account' }));

    expect(await screen.findByText('Query says visitor')).toBeDefined();
    expect(fetched).toEqual(['Ada']);
  });

  it('resolve without parsing a result when a typed action only redirects', async () => {
    let outcome = '';
    function Signer() {
      return (
        <button type="button" onClick={() => sign(new FormData()).then((state) => (outcome = `resolved ${state.ok}`), (error: Error) => (outcome = error.message))}>
          sign now
        </button>
      );
    }
    server.use(
      // ServerAction.Redirect(...) from an action whose C# type promises a result: no result is sent.
      http.post(`${origin}/_netjes/actions/guestbook.sign`, () => action({ ok: true, status: 200, redirect: '/elsewhere' })),
      http.get(`${origin}/elsewhere`, () => HttpResponse.json(payload('app/elsewhere/page.tsx', '/elsewhere'))),
    );
    window.history.replaceState(null, '', '/signer');
    const initial = payload('app/signer/page.tsx', '/signer');
    const loader = createModuleLoader({ ...modules, '/app/signer/page.tsx': async () => ({ default: Signer }) });
    const initialModules = await loader.loadRoute(initial);
    await act(async () => {
      render(<RouterProvider initial={initial} initialModules={initialModules} loader={loader} errorPage={null} shell={createQueryShell()} />);
    });

    await userEvent.setup().click(screen.getByRole('button', { name: 'sign now' }));

    await expect.poll(() => outcome).toBe('resolved true');
  });

  it('can be called like a function, with a JSON body, and update TanStack Query entries', async () => {
    let body: unknown;
    server.use(
      http.get(`${origin}/api/likes`, () => HttpResponse.json(3)),
      http.post(`${origin}/_netjes/actions/greet`, async ({ request }) => {
        body = await request.json();
        return action({ ok: true, status: 200, result: 'Hello, Ada', queries: [{ key: ['likes', 'a'], data: 9, updatedAt: Date.now() }] });
      }),
    );
    const user = await renderApp(payload('app/likes/page.tsx', '/likes'));
    await screen.findByText('3 likes');

    await user.click(screen.getByRole('button', { name: 'greet' }));

    expect(await screen.findByText('9 likes')).toBeDefined();
    expect(body).toEqual({ name: 'Ada' });
    expect(JSON.parse(document.body.getAttribute('data-greeting') ?? 'null')).toEqual({ ok: true, result: 'Hello, Ada' });
  });

  it('refetch the queries an action invalidates', async () => {
    let likes = 3;
    server.use(
      http.get(`${origin}/api/likes`, () => HttpResponse.json(likes)),
      http.post(`${origin}/_netjes/actions/greet`, () => {
        likes = 4;
        return action({ ok: true, status: 200, result: 'hi', invalidate: [['likes']] });
      }),
    );
    const user = await renderApp(payload('app/likes/page.tsx', '/likes'));
    await screen.findByText('3 likes');

    await user.click(screen.getByRole('button', { name: 'greet' }));

    expect(await screen.findByText('4 likes')).toBeDefined();
  });

  it('throw to the error boundary when the server fails', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    server.use(http.post(`${origin}/_netjes/actions/guestbook.sign`, () => action({ ok: false, status: 500, message: 'db down' }, 500)));
    const user = await renderApp(payload('app/guestbook/page.tsx', '/guestbook'));

    await user.click(screen.getByRole('button', { name: 'Sign' }));

    expect(await screen.findByText('db down')).toBeDefined();
    vi.restoreAllMocks();
  });

  it('start from the state a failed post without JavaScript left in the payload', async () => {
    await renderApp(
      payload('app/guestbook/page.tsx', '/guestbook', {
        actionState: { action: 'guestbook.sign', errors: { name: ['Tell us your name.'] }, values: { name: 'Ad' } },
      }),
    );

    expect(screen.getByRole('alert').textContent).toBe('Tell us your name.');
    expect(screen.getByLabelText('Name')).toHaveProperty('value', 'Ad');
  });

  it('render a real form post on the server, so forms work without JavaScript', async () => {
    const initial = payload('app/guestbook/page.tsx', '/guestbook');
    const loader = createModuleLoader(modules);
    const initialModules = await loader.loadRoute(initial);
    const window_ = globalThis.document;
    // Server rendering: no document.
    vi.stubGlobal('document', undefined);
    let html: string;
    try {
      html = renderToString(<RouterProvider initial={initial} initialModules={initialModules} loader={loader} errorPage={null} />);
    } finally {
      vi.stubGlobal('document', window_);
      vi.unstubAllGlobals();
    }

    expect(html).toContain('<form action="/_netjes/actions/guestbook.sign" method="post">');
    expect(html).toContain('<input type="hidden" name="__RequestVerificationToken" value="tok"/>');
    expect(html).toContain('<input type="hidden" name="__netjes_return" value="/guestbook"/>');
  });
});
