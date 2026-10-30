import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { type ReactNode, useState } from 'react';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { Link } from '../src/link.tsx';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload } from '../src/payload.ts';
import { RouterProvider, usePathname, useRouter } from '../src/router.tsx';

const origin = window.location.origin;
const server = setupServer();
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

function Root({ children, modal }: { children: ReactNode; modal?: ReactNode }) {
  return (
    <div>
      <p>{`at ${usePathname()}`}</p>
      {children}
      {modal && <dialog open aria-label="Photo">{modal}</dialog>}
    </div>
  );
}

function Feed(props: Record<string, unknown>) {
  const [scrolled, setScrolled] = useState(0);
  return (
    <main>
      <h1>Feed</h1>
      <p>{`${String(props.posts ?? 0)} posts`}</p>
      <button type="button" onClick={() => setScrolled((n) => n + 1)}>{`scrolled ${scrolled}`}</button>
      <Link href="/photo/7">Photo 7</Link>
    </main>
  );
}

const PhotoPage = (props: Record<string, unknown>) => <h1>{`Photo page ${String(props.title)}`}</h1>;
function PhotoModal(props: Record<string, unknown>) {
  const router = useRouter();
  return (
    <>
      <p>{`Photo modal ${String(props.title)}`}</p>
      <p>{`photo id ${String((props.params as Record<string, unknown> | undefined)?.photoId)}`}</p>
      <button type="button" onClick={() => void router.refresh()}>
        refresh
      </button>
    </>
  );
}

const modules = {
  '/app/layout.tsx': async () => ({ default: Root }),
  '/app/feed/page.tsx': async () => ({ default: Feed }),
  '/app/photo/[id]/page.tsx': async () => ({ default: PhotoPage }),
  '/app/@modal/(.)photo/[id]/page.tsx': async () => ({ default: PhotoModal }),
};

const root = { id: 'app', layout: 'app/layout.tsx', slots: { modal: null } };

function payload(page: string, url: string, overrides: Partial<PagePayload> = {}): PagePayload {
  return { kind: 'page', route: url, page, segments: [root], props: {}, params: {}, shared: null, status: 200, url, version: 'v1', ...overrides };
}

const photo = payload('app/photo/[id]/page.tsx', '/photo/7', {
  props: { title: 'sunset' },
  params: { id: '7' },
  // The intercepting page names its parameter differently; the server sends its own params.
  intercepts: [{ segment: 'app', slot: 'modal', page: 'app/@modal/(.)photo/[id]/page.tsx', params: { photoId: '7' } }],
});

async function renderAt(initial: PagePayload) {
  window.history.replaceState(null, '', initial.url);
  const loader = createModuleLoader(modules);
  const initialModules = await loader.loadRoute(initial);
  await act(async () => {
    render(<RouterProvider initial={initial} initialModules={initialModules} loader={loader} errorPage={null} />);
  });
  return userEvent.setup();
}

describe('intercepting routes', () => {
  it('show the intercepted page in its slot over the current page, which stays mounted, and back closes it', async () => {
    server.use(http.get(`${origin}/photo/7`, () => HttpResponse.json(photo)));
    const user = await renderAt(payload('app/feed/page.tsx', '/feed'));
    await user.click(screen.getByRole('button', { name: 'scrolled 0' }));

    await user.click(screen.getByRole('link', { name: 'Photo 7' }));

    expect((await screen.findByRole('dialog', { name: 'Photo' })).textContent).toContain('Photo modal sunset');
    expect(screen.getByText('photo id 7')).toBeDefined();
    expect(screen.getByRole('heading', { name: 'Feed' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'scrolled 1' })).toBeDefined();
    expect(screen.getByText('at /photo/7')).toBeDefined();
    expect(window.location.pathname).toBe('/photo/7');

    await act(async () => window.history.back());
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(screen.getByText('at /feed')).toBeDefined();
  });

  it('stay open when the page refreshes, with fresh data for the modal and the page under it', async () => {
    let edited = false;
    server.use(
      http.get(`${origin}/photo/7`, () => HttpResponse.json(edited ? { ...photo, props: { title: 'sunset (edited)' } } : photo)),
      http.get(`${origin}/feed`, () => HttpResponse.json(payload('app/feed/page.tsx', '/feed', { props: { posts: 3 } }))),
    );
    const user = await renderAt(payload('app/feed/page.tsx', '/feed'));
    await user.click(screen.getByRole('button', { name: 'scrolled 0' }));
    await user.click(screen.getByRole('link', { name: 'Photo 7' }));
    await screen.findByRole('dialog', { name: 'Photo' });

    edited = true;
    await user.click(screen.getByRole('button', { name: 'refresh' }));

    expect(await screen.findByText('Photo modal sunset (edited)')).toBeDefined();
    expect(await screen.findByText('3 posts')).toBeDefined();
    expect(screen.getByRole('button', { name: 'scrolled 1' })).toBeDefined();
    expect(screen.queryByRole('heading', { name: /Photo page/ })).toBeNull();
    expect(window.location.pathname).toBe('/photo/7');
  });

  it('do not apply when the page is loaded directly', async () => {
    await renderAt(photo);

    expect(screen.getByRole('heading', { name: 'Photo page sunset' })).toBeDefined();
    expect(screen.queryByRole('dialog')).toBeNull();
  });
});
