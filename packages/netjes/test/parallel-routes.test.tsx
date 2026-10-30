import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import type { ReactNode } from 'react';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { Link } from '../src/link.tsx';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload, SegmentRef } from '../src/payload.ts';
import { RouterProvider } from '../src/router.tsx';

const origin = window.location.origin;
const server = setupServer();
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

function Dashboard({ children, team, stats }: { children: ReactNode; team?: ReactNode; stats?: ReactNode }) {
  return (
    <div>
      <Link href="/dashboard/settings">Settings</Link>
      <aside aria-label="Team">{team}</aside>
      <aside aria-label="Stats">{stats ?? 'no stats'}</aside>
      <main>{children}</main>
    </div>
  );
}

const modules = {
  '/app/dashboard/layout.tsx': async () => ({ default: Dashboard }),
  '/app/dashboard/page.tsx': async () => ({ default: () => <h1>Overview</h1> }),
  '/app/dashboard/settings/page.tsx': async () => ({ default: () => <h1>Settings</h1> }),
  '/app/dashboard/@team/default.tsx': async () => ({ default: () => <p>everyone</p> }),
  '/app/dashboard/@team/settings/page.tsx': async () => ({ default: () => <p>team settings</p> }),
  '/app/dashboard/@stats/page.tsx': async () => ({ default: () => <p>42 tasks</p> }),
  '/app/dashboard/[id]/page.tsx': async () => ({ default: () => <h1>Project</h1> }),
  '/app/dashboard/@team/[project]/page.tsx': async () => ({ default: ({ params }: { params: Record<string, unknown> }) => <p>{`team of ${String(params.project)}`}</p> }),
};

const dashboard = (slots: Record<string, string | null>): SegmentRef => ({ id: 'app/dashboard', layout: 'app/dashboard/layout.tsx', slots });

function payload(page: string, url: string, slots: Record<string, string | null>): PagePayload {
  return { kind: 'page', route: url, page, segments: [dashboard(slots)], props: {}, params: {}, shared: null, status: 200, url, version: 'v1' };
}

describe('parallel routes', () => {
  it('hand each slot to the layout as a prop, and follow the URL on navigation', async () => {
    server.use(
      http.get(`${origin}/dashboard/settings`, () =>
        HttpResponse.json(payload('app/dashboard/settings/page.tsx', '/dashboard/settings', { stats: null, team: 'app/dashboard/@team/settings/page.tsx' })),
      ),
    );
    const initial = payload('app/dashboard/page.tsx', '/dashboard', { stats: 'app/dashboard/@stats/page.tsx', team: 'app/dashboard/@team/default.tsx' });
    window.history.replaceState(null, '', initial.url);
    const loader = createModuleLoader(modules);
    const initialModules = await loader.loadRoute(initial);
    await act(async () => {
      render(<RouterProvider initial={initial} initialModules={initialModules} loader={loader} errorPage={null} />);
    });

    expect(screen.getByRole('complementary', { name: 'Team' }).textContent).toBe('everyone');
    expect(screen.getByRole('complementary', { name: 'Stats' }).textContent).toBe('42 tasks');

    await userEvent.click(screen.getByRole('link', { name: 'Settings' }));

    expect(await screen.findByRole('heading', { name: 'Settings' })).toBeDefined();
    expect(screen.getByRole('complementary', { name: 'Team' }).textContent).toBe('team settings');
    expect(screen.getByRole('complementary', { name: 'Stats' }).textContent).toBe('no stats');
  });

  it('give a slot page its own params, which can be named differently from the page', async () => {
    const initial: PagePayload = {
      ...payload('app/dashboard/[id]/page.tsx', '/dashboard/acme', { team: 'app/dashboard/@team/[project]/page.tsx', stats: null }),
      params: { id: 'acme' },
    };
    initial.segments = [{ ...dashboard({ team: 'app/dashboard/@team/[project]/page.tsx', stats: null }), slotParams: { team: { project: 'acme' } } }];
    window.history.replaceState(null, '', initial.url);
    const loader = createModuleLoader(modules);
    const initialModules = await loader.loadRoute(initial);
    await act(async () => {
      render(<RouterProvider initial={initial} initialModules={initialModules} loader={loader} errorPage={null} />);
    });

    expect(screen.getByRole('complementary', { name: 'Team' }).textContent).toBe('team of acme');
  });
});
