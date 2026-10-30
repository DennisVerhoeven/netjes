import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import type { ReactNode } from 'react';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { z } from 'zod';
import { defineLayout } from '../src/define-page.tsx';
import { Form } from '../src/form.tsx';
import { Link } from '../src/link.tsx';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload, SegmentRef } from '../src/payload.ts';
import { RouterProvider, useRouter } from '../src/router.tsx';

const origin = window.location.origin;
const server = setupServer();
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

const TeamLayout = defineLayout(z.object({ team: z.string(), members: z.number() }), ({ team, members, children }) => (
  <section aria-label={`team ${team}`}>
    <p>{`${members} members`}</p>
    <Link href="/core/settings">Settings</Link>
    <Link href="/infra">Infra</Link>
    {children}
  </section>
));

function Page(props: Record<string, unknown>) {
  const router = useRouter();
  return (
    <div>
      <h1>{String(props.title)}</h1>
      <button type="button" onClick={() => router.refresh()}>
        refresh
      </button>
      <Form method="post">
        <button type="submit">save</button>
      </Form>
    </div>
  );
}

const modules = {
  '/app/[team]/layout.tsx': async () => ({ default: TeamLayout }),
  '/app/[team]/page.tsx': async () => ({ default: Page }),
  '/app/[team]/settings/page.tsx': async () => ({ default: Page }),
};

const team = (name: string, members: number, key = `key-${name}`): SegmentRef => ({
  id: 'app/[team]',
  layout: 'app/[team]/layout.tsx',
  props: { team: name, members },
  key,
});

function payload(page: string, url: string, segment: SegmentRef, title: string): PagePayload {
  return { kind: 'page', route: url, page, segments: [segment], props: { title }, params: {}, shared: null, status: 200, url, version: 'v1' };
}

const reused = (key: string): SegmentRef => ({ id: 'app/[team]', layout: 'app/[team]/layout.tsx', key, reused: true });

async function renderApp(initial: PagePayload) {
  window.history.replaceState(null, '', initial.url);
  const loader = createModuleLoader(modules);
  const initialModules = await loader.loadRoute(initial);
  await act(async () => {
    render(<RouterProvider initial={initial} initialModules={initialModules} loader={loader} errorPage={null} />);
  });
  return userEvent.setup();
}

describe('layout loader props', () => {
  it('reach the layout, parsed by defineLayout', async () => {
    await renderApp(payload('app/[team]/page.tsx', '/core', team('core', 3), 'Overview'));

    expect(screen.getByRole('region', { name: 'team core' }).textContent).toContain('3 members');
  });

  it('are kept for segments the server marks reused, and the keys the client holds are sent', async () => {
    const sent: (string | null)[] = [];
    server.use(
      http.get(`${origin}/core/settings`, ({ request }) => {
        sent.push(request.headers.get('X-Netjes-Segments'));
        return HttpResponse.json(payload('app/[team]/settings/page.tsx', '/core/settings', reused('key-core'), 'Settings'));
      }),
    );
    const user = await renderApp(payload('app/[team]/page.tsx', '/core', team('core', 3), 'Overview'));

    await user.click(screen.getByRole('link', { name: 'Settings' }));

    expect(await screen.findByRole('heading', { name: 'Settings' })).toBeDefined();
    expect(screen.getByRole('region', { name: 'team core' }).textContent).toContain('3 members');
    expect(sent).toEqual(['key-core']);
  });

  it('are loaded in full when the client no longer has a segment marked reused', async () => {
    const sent: (string | null)[] = [];
    server.use(
      http.get(`${origin}/infra`, ({ request }) => {
        const keys = request.headers.get('X-Netjes-Segments');
        sent.push(keys);
        const segment = keys ? reused('key-infra') : team('infra', 7);
        return HttpResponse.json(payload('app/[team]/page.tsx', '/infra', segment, 'Infra'));
      }),
    );
    const user = await renderApp(payload('app/[team]/page.tsx', '/core', team('core', 3), 'Overview'));

    await user.click(screen.getByRole('link', { name: 'Infra' }));

    expect(await screen.findByRole('region', { name: 'team infra' })).toBeDefined();
    expect(screen.getByText('7 members')).toBeDefined();
    expect(sent).toEqual(['key-core', null]);
  });

  it('are all reloaded by refresh() and after a form post', async () => {
    const sent: (string | null)[] = [];
    const answer = ({ request }: { request: Request }) => {
      sent.push(request.headers.get('X-Netjes-Segments'));
      return HttpResponse.json(payload('app/[team]/page.tsx', '/core', team('core', 4), 'Overview'));
    };
    server.use(http.get(`${origin}/core`, answer), http.post(`${origin}/core`, answer));
    const user = await renderApp(payload('app/[team]/page.tsx', '/core', team('core', 3), 'Overview'));

    await user.click(screen.getByRole('button', { name: 'refresh' }));
    await screen.findByText('4 members');
    await user.click(screen.getByRole('button', { name: 'save' }));

    expect(sent).toEqual([null, null]);
  });

  it('show the error page when they do not match the layout schema', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    await renderApp(payload('app/[team]/page.tsx', '/core', { ...team('core', 3), props: { team: 'core' } }, 'Overview'));

    expect(screen.getByText(/Invalid layout props: members/)).toBeDefined();
    vi.restoreAllMocks();
  });
});
