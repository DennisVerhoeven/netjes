import { useQuery } from '@tanstack/react-query';
import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useEffect, useLayoutEffect, useState } from 'react';
import { renderToString } from 'react-dom/server';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { z } from 'zod';
import { Form } from '../src/form.tsx';
import { Link } from '../src/link.tsx';
import { mounted } from '../src/mounted.tsx';
import {
  type NavigationAttempt,
  useHistoryState,
  useNavigation,
  useNavigationGuard,
  useParams,
  usePathname,
  useRouter,
  useSearchParams,
  useSharedProps,
} from '../src/router.tsx';
import { serverAction, useAction, usePageActionState } from '../src/actions.tsx';
import { createTestApp } from '../src/testing.tsx';

afterEach(() => {
  vi.unstubAllGlobals();
});

/** Fetches its own data, as a component with a query that no loader seeds does. */
function Likes() {
  const likes = useQuery({
    queryKey: ['likes', 'hello'],
    queryFn: async () => {
      const response = await fetch('/api/posts/hello/likes');
      if (!response.ok) throw new Error(`Likes request failed with ${response.status}`);
      return (await response.json()) as { likes: number };
    },
  });
  return <p>{likes.isError ? 'Failed' : likes.data ? `Loaded ${likes.data.likes} likes` : 'Loading'}</p>;
}

/** Answers every request with `status`, or with `{ likes: 3 }` once `failures` requests have failed. */
function stubFetch(failures = Number.POSITIVE_INFINITY) {
  const fetch = vi.fn(async () =>
    fetch.mock.calls.length <= failures ? new Response(null, { status: 500 }) : new Response(JSON.stringify({ likes: 3 })),
  );
  vi.stubGlobal('fetch', fetch);
  return fetch;
}

function Nav() {
  const site = useSharedProps(z.object({ siteName: z.string() }));
  const pathname = usePathname();
  const search = useSearchParams();
  return (
    <nav>
      <p>{`${site.siteName} at ${pathname}${search.size ? `?${search}` : ''}`}</p>
      <Link href="/blog">Blog</Link>
      <Link href="/about" replace>
        About
      </Link>
    </nav>
  );
}

const sign = serverAction<FormData, string>('guestbook.sign');

function SignForm() {
  const [state, formAction] = useAction(sign);
  return (
    <Form action={formAction}>
      <input name="name" defaultValue="Ada" aria-label="Name" />
      <button type="submit">Sign</button>
      <p>{state === null ? 'idle' : state.ok ? `ok ${state.result}` : `error ${state.errors.name?.[0]}`}</p>
    </Form>
  );
}

const signUp = serverAction<FormData, null>('accounts.signUp');

function SignUpForm() {
  const [state, formAction] = useAction(signUp);
  return (
    <Form action={formAction}>
      <input name="email" defaultValue="ada@example.test" aria-label="Email" />
      <input name="password" type="password" defaultValue="hunter2" aria-label="Password" />
      <button type="submit">Sign up</button>
      <output>{state && !state.ok ? JSON.stringify(state.values) : ''}</output>
    </Form>
  );
}

const greet = serverAction<{ name: string }, string>('greet');

/** Asks the server as it mounts: React runs a component's effects before the wrapper's. */
function Welcome() {
  const [greeting, setGreeting] = useState('…');
  useEffect(() => {
    greet({ name: 'Ada' }).then(
      (state) => setGreeting(state.ok ? state.result : 'rejected'),
      (error: unknown) => setGreeting(String(error)),
    );
  }, []);
  return <p>{greeting}</p>;
}

/** Asks the server in the commit that mounts it, before React runs any component's useEffect. */
function WelcomeAtOnce() {
  const [greeting, setGreeting] = useState('…');
  useLayoutEffect(() => {
    greet({ name: 'Ada' }).then(
      (state) => setGreeting(state.ok ? state.result : 'rejected'),
      (error: unknown) => setGreeting(String(error)),
    );
  }, []);
  return <p>{greeting}</p>;
}

describe('createTestApp', () => {
  it('answers server actions with the responders it was given, and records the calls', async () => {
    const app = createTestApp({ actions: { 'guestbook.sign': (input) => ({ ok: false, status: 422, errors: { name: [`taken: ${(input as FormData).get('name')}`] } }) } });
    render(<SignForm />, { wrapper: app.Wrapper });

    await userEvent.click(screen.getByRole('button', { name: 'Sign' }));

    expect(await screen.findByText('error taken: Ada')).toBeDefined();
    const call = app.navigations[0];
    expect(call?.type === 'action' && call.name).toBe('guestbook.sign');
  });

  it('hands back only the fields an answer names, as the server does', async () => {
    const app = createTestApp({
      actions: { 'accounts.signUp': () => ({ ok: false, status: 422, errors: { email: ['Taken.'] }, fields: ['email'] }) },
    });
    render(<SignUpForm />, { wrapper: app.Wrapper });

    await userEvent.click(screen.getByRole('button', { name: 'Sign up' }));

    expect(await screen.findByText('{"email":"ada@example.test"}')).toBeDefined();
  });

  it('answers an action a component calls from its own effect as it mounts', async () => {
    const app = createTestApp({ actions: { greet: (input) => ({ ok: true, result: `Hello ${(input as { name: string }).name}` }) } });

    render(<Welcome />, { wrapper: app.Wrapper });

    expect(await screen.findByText('Hello Ada')).toBeDefined();
  });

  it('answers an action a component calls from its own layout effect as it mounts', async () => {
    const app = createTestApp({ actions: { greet: (input) => ({ ok: true, result: `Hello ${(input as { name: string }).name}` }) } });

    render(<WelcomeAtOnce />, { wrapper: app.Wrapper });

    expect(await screen.findByText('Hello Ada')).toBeDefined();
  });

  it('answers unknown actions with ok and no result', async () => {
    const app = createTestApp();
    render(<SignForm />, { wrapper: app.Wrapper });

    await userEvent.click(screen.getByRole('button', { name: 'Sign' }));

    expect(await screen.findByText('ok undefined')).toBeDefined();
  });

  it('renders components that use the router hooks', () => {
    const app = createTestApp({ url: '/blog?tag=a', shared: { siteName: 'Demo' } });

    render(<Nav />, { wrapper: app.Wrapper });

    expect(screen.getByText('Demo at /blog?tag=a')).toBeDefined();
    expect(screen.getByRole('link', { name: 'Blog' }).getAttribute('aria-current')).toBe('page');
  });

  it('records navigations and moves to the new URL', async () => {
    const app = createTestApp({ url: '/blog', shared: { siteName: 'Demo' } });
    render(<Nav />, { wrapper: app.Wrapper });

    await userEvent.click(screen.getByRole('link', { name: 'About' }));

    expect(app.navigations).toEqual([{ type: 'replace', href: '/about' }]);
    expect(screen.getByText('Demo at /about')).toBeDefined();
    expect(screen.getByRole('link', { name: 'About' }).getAttribute('aria-current')).toBe('page');
  });

  it('records router calls', async () => {
    function Buttons() {
      const router = useRouter();
      return (
        <>
          <button type="button" onClick={() => router.push('/next?x=1')}>
            push
          </button>
          <button type="button" onClick={() => router.refresh()}>
            refresh
          </button>
          <button type="button" onClick={() => router.back()}>
            back
          </button>
        </>
      );
    }
    const app = createTestApp();
    render(<Buttons />, { wrapper: app.Wrapper });

    await userEvent.click(screen.getByRole('button', { name: 'push' }));
    await userEvent.click(screen.getByRole('button', { name: 'refresh' }));
    await userEvent.click(screen.getByRole('button', { name: 'back' }));

    expect(app.navigations).toEqual([{ type: 'push', href: '/next?x=1' }, { type: 'refresh' }, { type: 'back' }]);
  });

  it('records form submissions with their fields and the antiforgery token', async () => {
    const app = createTestApp({ url: '/guestbook' });
    render(
      <Form method="post">
        <label>
          Name <input name="name" defaultValue="Ada" />
        </label>
        <button type="submit">Sign</button>
      </Form>,
      { wrapper: app.Wrapper },
    );

    await userEvent.click(screen.getByRole('button', { name: 'Sign' }));

    const submission = app.navigations[0];
    expect(submission?.type).toBe('submit');
    if (submission?.type !== 'submit') return;
    expect(new URL(submission.action).pathname).toBe('/guestbook');
    expect(submission.data.get('name')).toBe('Ada');
    expect(submission.data.get('__RequestVerificationToken')).toBe('test-token');
  });

  it('seeds queries the way the server does: fresh for the default staleTime, so not refetched', () => {
    let fetches = 0;
    function Likes() {
      const likes = useQuery({ queryKey: ['likes'], queryFn: async () => ++fetches });
      return <p>{`${likes.data} likes`}</p>;
    }
    const app = createTestApp({ queries: [{ key: ['likes'], data: 3 }] });

    render(<Likes />, { wrapper: app.Wrapper });

    expect(screen.getByText('3 likes')).toBeDefined();
    expect(fetches).toBe(0);
  });

  it('takes QueryClient defaultOptions like netjes({ query })', () => {
    const app = createTestApp({ defaultOptions: { queries: { staleTime: 5 } } });

    expect(app.queryClient.getDefaultOptions().queries?.staleTime).toBe(5);
    expect(app.queryClient.getDefaultOptions().queries?.retry).toBeUndefined();
    expect(app.queryClient.getDefaultOptions().queries?.retryDelay).toBe(0);
    expect(app.queryClient.getDefaultOptions().mutations?.retryDelay).toBe(0);
  });

  it('tries a failed query as often as the app does, without waiting between the tries', async () => {
    const fetch = stubFetch();

    render(<Likes />, { wrapper: createTestApp().Wrapper });

    expect(await screen.findByText('Failed')).toBeDefined();
    expect(fetch).toHaveBeenCalledTimes(4);
  });

  it('shows what a query gets on a later try, as the app does', async () => {
    stubFetch(1);

    render(<Likes />, { wrapper: createTestApp().Wrapper });

    expect(await screen.findByText('Loaded 3 likes')).toBeDefined();
  });

  it("layers the test's defaultOptions over the harness's, and the harness's over the app's", () => {
    // Where Netjes's Vitest setup file keeps the app's netjes({ query }) defaults; this file does not import it.
    const appDefaults = Symbol.for('netjes.testing.queryDefaults');
    const global = globalThis as Record<symbol, unknown>;
    global[appDefaults] = { queries: { retry: 1, staleTime: 5000, retryDelay: 1000, gcTime: 1000 } };
    try {
      expect(createTestApp().queryClient.getDefaultOptions().queries).toEqual({
        retry: 1,
        staleTime: 5000,
        retryDelay: 0,
        gcTime: Number.POSITIVE_INFINITY,
      });
      expect(createTestApp({ defaultOptions: { queries: { retry: false, retryDelay: 10 } } }).queryClient.getDefaultOptions().queries).toEqual({
        retry: false,
        staleTime: 5000,
        retryDelay: 10,
        gcTime: Number.POSITIVE_INFINITY,
      });
    } finally {
      delete global[appDefaults];
    }
  });

  it('provides params, a navigation state and a QueryClient seeded with queries', () => {
    function Post() {
      const { slug } = useParams(z.object({ slug: z.string() }));
      const navigation = useNavigation();
      const likes = useQuery({ queryKey: ['likes', slug], queryFn: (): Promise<number> => new Promise(() => {}) });
      return <p>{`${slug} ${navigation.state} ${likes.data ?? 'loading'} likes`}</p>;
    }
    const app = createTestApp({
      url: '/blog/hello',
      params: { slug: 'hello' },
      navigation: { state: 'loading', location: '/blog' },
      queries: [{ key: ['likes', 'hello'], data: 3 }],
    });

    render(<Post />, { wrapper: app.Wrapper });

    expect(screen.getByText('hello loading 3 likes')).toBeDefined();
    expect(app.queryClient.getQueryData(['likes', 'hello'])).toBe(3);
  });

  it("mounts page packages at the URLs it is given, which a package's links go below as the user navigates", async () => {
    const help = mounted('@acme/help-center');
    function Nav() {
      return (
        <nav>
          <p>{`at ${help.usePathname()}`}</p>
          <help.Link href="/articles">Articles</help.Link>
        </nav>
      );
    }
    const app = createTestApp({ url: '/acme/help', mounts: { '@acme/help-center': '/acme/help' } });
    render(<Nav />, { wrapper: app.Wrapper });

    expect(screen.getByText('at /')).toBeDefined();
    await userEvent.click(screen.getByRole('link', { name: 'Articles' }));

    expect(app.navigations).toEqual([{ type: 'push', href: '/acme/help/articles' }]);
    expect(screen.getByText('at /articles')).toBeDefined();
    expect(screen.getByRole('link', { name: 'Articles' }).getAttribute('aria-current')).toBe('page');
  });
});

describe('createTestApp with history state', () => {
  function Selection() {
    const [from] = useHistoryState('from', 'nowhere');
    const [ticked, setTicked] = useHistoryState('ticked', 0);
    const router = useRouter();
    return (
      <>
        <p>from {from}</p>
        <button type="button" onClick={() => setTicked((n) => n + 1)}>
          ticked {ticked}
        </button>
        <Link href="/orders/7" historyState={{ from: 'the list' }}>
          Order 7
        </Link>
        <button type="button" onClick={() => router.replace('/orders?page=2')}>
          next page
        </button>
      </>
    );
  }

  it("seeds the entry's values, as a navigation carries them or an earlier visit left them", () => {
    const app = createTestApp({ url: '/orders/7', historyState: { from: 'the list', ticked: 2 } });
    render(<Selection />, { wrapper: app.Wrapper });

    expect(screen.getByText('from the list')).toBeDefined();
    expect(screen.getByRole('button', { name: 'ticked 2' })).toBeDefined();
  });

  it('sets a value, records what a navigation carries, and keeps the values of a page that stays', async () => {
    const app = createTestApp({ url: '/orders' });
    render(<Selection />, { wrapper: app.Wrapper });

    await userEvent.click(screen.getByRole('button', { name: 'ticked 0' }));
    await userEvent.click(screen.getByRole('button', { name: 'next page' }));
    expect(screen.getByRole('button', { name: 'ticked 1' })).toBeDefined();
    await userEvent.click(screen.getByRole('link', { name: 'Order 7' }));

    expect(app.navigations).toEqual([
      { type: 'replace', href: '/orders?page=2' },
      { type: 'push', href: '/orders/7', historyState: { from: 'the list' } },
    ]);
    expect(screen.getByText('from the list')).toBeDefined();
    expect(screen.getByRole('button', { name: 'ticked 0' })).toBeDefined();
  });

  describe('a setter from before a navigation, as the router has it', () => {
    let navigated: Promise<void> | undefined;
    let setterEffects = 0;

    /** Sets the ticks with the setter of the render that was clicked, once the navigation has ended. */
    function ActsOnTicks() {
      const [ticked, setTicked] = useHistoryState('ticked', 0);
      const router = useRouter();
      useEffect(() => {
        setterEffects += 1;
      }, [setTicked]);
      return (
        <>
          <button type="button" onClick={() => setTicked((n) => n + 1)}>
            ticked {ticked}
          </button>
          <button type="button" onClick={() => (navigated = router.replace('/orders?page=2').then(() => setTicked(0)))}>
            clear on the next page
          </button>
          <button type="button" onClick={() => (navigated = router.push('/orders/7').then(() => setTicked(5)))}>
            open order 7
          </button>
        </>
      );
    }

    afterEach(() => {
      setterEffects = 0;
    });

    it('sets the value while the page stays, and is the same function throughout', async () => {
      const app = createTestApp({ url: '/orders' });
      render(<ActsOnTicks />, { wrapper: app.Wrapper });

      await userEvent.click(screen.getByRole('button', { name: 'ticked 0' }));
      await userEvent.click(screen.getByRole('button', { name: 'clear on the next page' }));
      await act(() => navigated);

      expect(screen.getByRole('button', { name: 'ticked 0' })).toBeDefined();
      expect(setterEffects).toBe(1);
    });

    it('sets nothing once the navigation went to another page', async () => {
      const app = createTestApp({ url: '/orders' });
      render(<ActsOnTicks />, { wrapper: app.Wrapper });

      await userEvent.click(screen.getByRole('button', { name: 'ticked 0' }));
      await userEvent.click(screen.getByRole('button', { name: 'open order 7' }));
      await act(() => navigated);

      expect(window.location.pathname).toBe('/orders/7');
      expect(screen.getByRole('button', { name: 'ticked 0' })).toBeDefined();
    });
  });

  it("passes what a page package's router carries", async () => {
    const help = mounted('@acme/help-center');
    function Search() {
      const router = help.useRouter();
      return (
        <button type="button" onClick={() => router.push('/articles', { historyState: { query: 'refunds' } })}>
          search
        </button>
      );
    }
    const app = createTestApp({ url: '/help', mounts: { '@acme/help-center': '/help' } });
    render(<Search />, { wrapper: app.Wrapper });

    await userEvent.click(screen.getByRole('button', { name: 'search' }));

    expect(app.navigations).toEqual([{ type: 'push', href: '/help/articles', historyState: { query: 'refunds' } }]);
  });
});

describe('createTestApp with a page action that failed', () => {
  function Guestbook() {
    const failed = usePageActionState();
    return (
      <Form method="post">
        <label>
          Name <input name="name" defaultValue={String(failed?.values.name ?? '')} />
        </label>
        {failed?.errors.name && <p role="alert">{failed.errors.name[0]}</p>}
        <output>{failed ? `failed ${failed.status}` : 'none'}</output>
      </Form>
    );
  }

  it('shows no failure by default', () => {
    render(<Guestbook />, { wrapper: createTestApp({ route: '/guestbook' }).Wrapper });

    expect(screen.getByRole('status').textContent).toBe('none');
  });

  it("hands usePageActionState the failed state it is given, as the server sends it after a 422", () => {
    const app = createTestApp({
      route: '/guestbook',
      pageActionState: { errors: { name: ['Tell us who you are.'] }, values: { name: 'Ada' } },
    });

    render(<Guestbook />, { wrapper: app.Wrapper });

    expect(screen.getByRole('alert').textContent).toBe('Tell us who you are.');
    expect(screen.getByRole('status').textContent).toBe('failed 422');
    expect((screen.getByLabelText('Name') as HTMLInputElement).value).toBe('Ada');
  });

  it('hands usePageActionState a refusal with the status it is given, such as 429 after a rate limit', () => {
    const app = createTestApp({ route: '/guestbook', pageActionState: { status: 429, errors: { name: ['Slow down.'] }, values: { name: 'Ada' } } });

    render(<Guestbook />, { wrapper: app.Wrapper });

    expect(screen.getByRole('alert').textContent).toBe('Slow down.');
    expect(screen.getByRole('status').textContent).toBe('failed 429');
    expect((screen.getByLabelText('Name') as HTMLInputElement).value).toBe('Ada');
  });

  it('takes the page from the URL when no route is given, and defaults the values to none', () => {
    const app = createTestApp({ url: '/guestbook', pageActionState: { errors: { name: ['Required.'] } } });

    render(<Guestbook />, { wrapper: app.Wrapper });

    expect(screen.getByRole('alert').textContent).toBe('Required.');
    expect((screen.getByLabelText('Name') as HTMLInputElement).value).toBe('');
  });
});

describe('a server action called outside any Wrapper', () => {
  const notReady = 'netjes: server action greet was called before the page was ready';

  it('fails at once after a Wrapper that was only rendered to a string', async () => {
    const { Wrapper } = createTestApp();
    renderToString(
      <Wrapper>
        <p>Hello</p>
      </Wrapper>,
    );

    await expect(greet({ name: 'Ada' })).rejects.toThrow(notReady);
  });

  it('fails at once after a Wrapper whose children threw as it rendered', async () => {
    function Broken(): never {
      throw new Error('broken');
    }
    const quiet = vi.spyOn(console, 'error').mockImplementation(() => {});
    expect(() => render(<Broken />, { wrapper: createTestApp().Wrapper })).toThrow('broken');
    quiet.mockRestore();

    await expect(greet({ name: 'Ada' })).rejects.toThrow(notReady);
  });
});

describe('createTestApp with a navigation guard', () => {
  /** A form with changes that asks before a link or the router leaves it, with a question of its own. */
  function Draft() {
    const [leaving, setLeaving] = useState<NavigationAttempt | null>(null);
    const [changed, setChanged] = useState(true);
    useNavigationGuard({ when: changed, onAttempt: setLeaving });
    const router = useRouter();
    const pathname = usePathname();
    return (
      <>
        <p>at {pathname}</p>
        <Link href="/orders">Orders</Link>
        <Link href="#notes">Notes</Link>
        <button type="button" onClick={() => void router.replace('/orders?page=2')}>
          next page
        </button>
        <button type="button" onClick={() => setChanged(false)}>
          Save
        </button>
        {leaving && (
          <div role="alertdialog" aria-label="Leave without saving?">
            <button type="button" onClick={() => (leaving.stay(), setLeaving(null))}>
              Keep editing
            </button>
            <button type="button" onClick={() => (leaving.proceed(), setLeaving(null))}>
              Leave
            </button>
          </div>
        )}
      </>
    );
  }

  it('holds push and replace until the guard lets them go on, and records them then', async () => {
    const app = createTestApp({ url: '/orders/7' });
    render(<Draft />, { wrapper: app.Wrapper });

    await userEvent.click(screen.getByRole('link', { name: 'Orders' }));
    expect(screen.getByRole('alertdialog', { name: 'Leave without saving?' })).toBeDefined();
    expect(app.navigations).toEqual([]);
    await userEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
    expect(screen.getByText('at /orders/7')).toBeDefined();

    await userEvent.click(screen.getByRole('button', { name: 'next page' }));
    await userEvent.click(screen.getByRole('button', { name: 'Leave' }));
    expect(app.navigations).toEqual([{ type: 'replace', href: '/orders?page=2' }]);
    expect(screen.getByText('at /orders')).toBeDefined();
  });

  it('holds no link to a fragment of the page on screen', async () => {
    const app = createTestApp({ url: '/orders/7' });
    render(<Draft />, { wrapper: app.Wrapper });

    await userEvent.click(screen.getByRole('link', { name: 'Notes' }));

    expect(screen.queryByRole('alertdialog')).toBeNull();
    expect(app.navigations).toEqual([{ type: 'push', href: '#notes' }]);
  });

  it('drops the attempt waiting for its answer once a navigation goes at once, as after a save', async () => {
    const app = createTestApp({ url: '/orders/7' });
    render(<Draft />, { wrapper: app.Wrapper });

    await userEvent.click(screen.getByRole('link', { name: 'Orders' }));
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    await userEvent.click(screen.getByRole('button', { name: 'next page' }));
    await userEvent.click(screen.getByRole('button', { name: 'Leave' }));

    expect(app.navigations).toEqual([{ type: 'replace', href: '/orders?page=2' }]);
    expect(screen.getByText('at /orders')).toBeDefined();
  });
});
