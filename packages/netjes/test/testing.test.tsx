import { useQuery } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useEffect, useLayoutEffect, useState } from 'react';
import { renderToString } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { z } from 'zod';
import { Form } from '../src/form.tsx';
import { Link } from '../src/link.tsx';
import { useNavigation, useParams, usePathname, useRouter, useSearchParams, useSharedProps } from '../src/router.tsx';
import { serverAction, useAction } from '../src/actions.tsx';
import { createTestApp } from '../src/testing.tsx';

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
    expect(app.queryClient.getDefaultOptions().queries?.retry).toBe(false);
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
