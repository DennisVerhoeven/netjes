import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { Form } from '../src/form.tsx';
import { Link } from '../src/link.tsx';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload } from '../src/payload.ts';
import { RouterProvider, useNavigation } from '../src/router.tsx';

const origin = window.location.origin;
const server = setupServer();
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));
afterEach(() => {
  server.resetHandlers();
  vi.restoreAllMocks();
});
afterAll(() => server.close());

function Page() {
  const navigation = useNavigation();
  return (
    <main>
      <Form method="post" action="/api/subscribe">
        <input name="email" defaultValue="a@example.com" aria-label="Email" />
        <button type="submit">Subscribe</button>
      </Form>
      <Link href="/other">Other</Link>
      {navigation.state === 'idle' && navigation.error && (
        <p role="alert">{`Could not send to ${navigation.error.action} (${navigation.error.status ?? 'network error'})`}</p>
      )}
    </main>
  );
}

const modules = { '/app/page/page.tsx': async () => ({ default: Page }) };

function payload(url: string): PagePayload {
  return { kind: 'page', route: url, page: 'app/page/page.tsx', segments: [], props: {}, params: {}, shared: null, status: 200, url, version: 'v1' };
}

async function renderApp() {
  vi.spyOn(console, 'error').mockImplementation(() => {});
  window.history.replaceState(null, '', '/form');
  const loader = createModuleLoader(modules);
  const initial = payload('/form');
  render(<RouterProvider initial={initial} initialModules={await loader.loadRoute(initial)} loader={loader} errorPage={null} />);
  return userEvent.setup();
}

describe('a form post that fails', () => {
  it('is reported by useNavigation with the status, and the input stays', async () => {
    server.use(http.post(`${origin}/api/subscribe`, () => new HttpResponse('nope', { status: 500, headers: { 'Content-Type': 'text/plain' } })));
    const user = await renderApp();

    await user.click(screen.getByRole('button', { name: 'Subscribe' }));

    expect((await screen.findByRole('alert')).textContent).toBe('Could not send to /api/subscribe (500)');
    expect(screen.getByLabelText('Email')).toHaveProperty('value', 'a@example.com');
  });

  it('is reported without a status when the post could not be sent at all', async () => {
    server.use(http.post(`${origin}/api/subscribe`, () => HttpResponse.error()));
    const user = await renderApp();

    await user.click(screen.getByRole('button', { name: 'Subscribe' }));

    expect((await screen.findByRole('alert')).textContent).toBe('Could not send to /api/subscribe (network error)');
  });

  it('is forgotten on the next navigation', async () => {
    server.use(
      http.post(`${origin}/api/subscribe`, () => new HttpResponse('nope', { status: 503, headers: { 'Content-Type': 'text/plain' } })),
      http.get(`${origin}/other`, () => HttpResponse.json(payload('/other'))),
    );
    const user = await renderApp();
    await user.click(screen.getByRole('button', { name: 'Subscribe' }));
    await screen.findByRole('alert');

    await user.click(screen.getByRole('link', { name: 'Other' }));
    await screen.findByRole('link', { name: 'Other', current: 'page' });

    expect(screen.queryByRole('alert')).toBeNull();
  });
});
