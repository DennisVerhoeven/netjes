import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { Form, SubmitButton } from '../src/form.tsx';
import { Link } from '../src/link.tsx';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload } from '../src/payload.ts';
import { RouterProvider, useHref, useNavigation, usePathname, useRouter } from '../src/router.tsx';

const origin = window.location.origin;
const server = setupServer();
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

function Page(props: Record<string, unknown>) {
  const pathname = usePathname();
  const router = useRouter();
  const navigation = useNavigation();
  return (
    <main>
      <h1>{`${String(props.title)} at ${pathname}`}</h1>
      <p role="status">{navigation.state === 'idle' ? (navigation.error ? `failed ${navigation.error.action}` : 'idle') : `${navigation.state} ${navigation.location}`}</p>
      <Link href="/about">About</Link>
      <Link href="/base/contact">Prefixed</Link>
      <a href={useHref('/about')}>Plain</a>
      <button type="button" onClick={() => router.push('/contact')}>
        contact
      </button>
      <Form method="post" action="/guestbook">
        <button type="submit">Sign</button>
        <SubmitButton formAction="/guestbook/delete">Delete</SubmitButton>
      </Form>
    </main>
  );
}

const modules = { '/app/page/page.tsx': async () => ({ default: Page }) };

function payload(url: string, title: string): PagePayload {
  return {
    kind: 'page',
    route: url,
    page: 'app/page/page.tsx',
    segments: [],
    props: { title },
    params: {},
    shared: null,
    status: 200,
    url: `/base${url}`,
    version: 'v1',
    basePath: '/base',
  };
}

async function renderApp(initial: PagePayload) {
  window.history.replaceState(null, '', initial.url);
  const loader = createModuleLoader(modules);
  render(<RouterProvider initial={initial} initialModules={await loader.loadRoute(initial)} loader={loader} errorPage={null} />);
  return userEvent.setup();
}

describe('a base path', () => {
  it('is added to links and left out of usePathname', async () => {
    await renderApp(payload('/home', 'Home'));

    expect(screen.getByRole('heading', { name: 'Home at /home' })).toBeDefined();
    expect(screen.getByRole('link', { name: 'About' }).getAttribute('href')).toBe('/base/about');
  });

  it('is added when links and the router navigate', async () => {
    server.use(
      http.get(`${origin}/base/about`, () => HttpResponse.json(payload('/about', 'About'))),
      http.get(`${origin}/base/contact`, () => HttpResponse.json(payload('/contact', 'Contact'))),
    );
    const user = await renderApp(payload('/home', 'Home'));

    await user.click(screen.getByRole('link', { name: 'About' }));
    expect(await screen.findByRole('heading', { name: 'About at /about' })).toBeDefined();
    expect(window.location.pathname).toBe('/base/about');
    await user.click(screen.getByRole('button', { name: 'contact' }));

    expect(await screen.findByRole('heading', { name: 'Contact at /contact' })).toBeDefined();
    expect(window.location.pathname).toBe('/base/contact');
  });

  it('is not added twice to hrefs that already start with it', async () => {
    server.use(http.get(`${origin}/base/contact`, () => HttpResponse.json(payload('/contact', 'Contact'))));
    const user = await renderApp(payload('/home', 'Home'));

    expect(screen.getByRole('link', { name: 'Prefixed' }).getAttribute('href')).toBe('/base/contact');
    expect(screen.getByRole('link', { name: 'Plain' }).getAttribute('href')).toBe('/base/about');
    await user.click(screen.getByRole('link', { name: 'Prefixed' }));

    expect(await screen.findByRole('heading', { name: 'Contact at /contact' })).toBeDefined();
    expect(window.location.pathname).toBe('/base/contact');
  });

  it('is left out of the navigation state, like usePathname', async () => {
    let release: () => void = () => {};
    const slow = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.get(`${origin}/base/about`, async () => {
        await slow;
        return HttpResponse.json(payload('/about', 'About'));
      }),
      http.post(`${origin}/base/guestbook`, () => new HttpResponse('nope', { status: 500 })),
    );
    vi.spyOn(console, 'error').mockImplementation(() => {});
    const user = await renderApp(payload('/home', 'Home'));

    await user.click(screen.getByRole('link', { name: 'About' }));
    expect(screen.getByRole('status').textContent).toBe('loading /about');
    release();
    await screen.findByRole('heading', { name: 'About at /about' });
    await user.click(screen.getByRole('button', { name: 'Sign' }));

    expect(await screen.findByText('failed /guestbook')).toBeDefined();
    vi.restoreAllMocks();
  });

  it('is in the formaction a SubmitButton renders, so it works without JavaScript too', async () => {
    const posted = vi.fn();
    server.use(
      http.post(`${origin}/base/guestbook/delete`, () => {
        posted();
        return HttpResponse.json(payload('/guestbook', 'Deleted'));
      }),
    );
    const user = await renderApp(payload('/home', 'Home'));

    expect(screen.getByRole('button', { name: 'Delete' }).getAttribute('formaction')).toBe('/base/guestbook/delete');
    await user.click(screen.getByRole('button', { name: 'Delete' }));

    expect(await screen.findByRole('heading', { name: 'Deleted at /guestbook' })).toBeDefined();
    expect(posted).toHaveBeenCalledTimes(1);
  });

  it('is added to form actions', async () => {
    const posted = vi.fn();
    server.use(
      http.post(`${origin}/base/guestbook`, () => {
        posted();
        return HttpResponse.json(payload('/guestbook', 'Signed'));
      }),
    );
    const user = await renderApp(payload('/home', 'Home'));

    expect(screen.getByRole('button', { name: 'Sign' }).closest('form')?.getAttribute('action')).toBe('/base/guestbook');
    await user.click(screen.getByRole('button', { name: 'Sign' }));

    expect(await screen.findByRole('heading', { name: 'Signed at /guestbook' })).toBeDefined();
    expect(posted).toHaveBeenCalledTimes(1);
  });
});
