import { render, screen } from '@testing-library/react';
import type { ReactNode } from 'react';
import { describe, expect, it } from 'vitest';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload } from '../src/payload.ts';
import { RouterProvider, useSelectedLayoutSegment, useSelectedLayoutSegments } from '../src/router.tsx';

function Root({ children }: { children: ReactNode }) {
  return (
    <div>
      <p>{`root: ${useSelectedLayoutSegments().join('/')}`}</p>
      {children}
    </div>
  );
}

function Dashboard({ children }: { children: ReactNode }) {
  return (
    <div>
      <p>{`dashboard tab: ${useSelectedLayoutSegment() ?? 'none'}`}</p>
      <p>{`dashboard below: ${useSelectedLayoutSegments().join('/')}`}</p>
      {children}
    </div>
  );
}

function Docs({ children }: { children: ReactNode }) {
  return (
    <div>
      <p>{`docs below: ${useSelectedLayoutSegments().join('/') || 'nothing'}`}</p>
      {children}
    </div>
  );
}

const modules = {
  '/src/app/layout.tsx': async () => ({ default: Root }),
  '/src/app/docs/[...path]/layout.tsx': async () => ({ default: Docs }),
  '/src/app/docs/[...path]/page.tsx': async () => ({ default: () => <h1>doc</h1> }),
  '/app/layout.tsx': async () => ({ default: Root }),
  '/app/(admin)/dashboard/layout.tsx': async () => ({ default: Dashboard }),
  '/app/(admin)/dashboard/page.tsx': async () => ({ default: () => <h1>overview</h1> }),
  '/app/(admin)/dashboard/[team]/settings/page.tsx': async () => ({ default: () => <h1>settings</h1> }),
};

const dashboard = [
  { id: 'app', layout: 'app/layout.tsx' },
  { id: 'app/(admin)/dashboard', layout: 'app/(admin)/dashboard/layout.tsx' },
];

async function renderAt(url: string, page: string, segments: PagePayload['segments'] = dashboard) {
  const initial: PagePayload = {
    kind: 'page',
    route: url,
    page,
    segments,
    props: {},
    params: {},
    shared: null,
    status: 200,
    url,
    version: 'v1',
  };
  window.history.replaceState(null, '', url);
  const loader = createModuleLoader(modules);
  render(<RouterProvider initial={initial} initialModules={await loader.loadRoute(initial)} loader={loader} errorPage={null} />);
}

describe('useSelectedLayoutSegment(s)', () => {
  it('return the URL segments below the layout that calls them, route groups aside', async () => {
    await renderAt('/dashboard/core%20team/settings', 'app/(admin)/dashboard/[team]/settings/page.tsx');

    expect(screen.getByText('root: dashboard/core team/settings')).toBeDefined();
    expect(screen.getByText('dashboard tab: core team')).toBeDefined();
    expect(screen.getByText('dashboard below: core team/settings')).toBeDefined();
  });

  it('count from the depth the server sent: catch-alls cover what they matched, the app directory is not a segment', async () => {
    await renderAt('/docs/a/b', 'src/app/docs/[...path]/page.tsx', [
      { id: 'src/app', layout: 'src/app/layout.tsx', depth: 0 },
      { id: 'src/app/docs/[...path]', layout: 'src/app/docs/[...path]/layout.tsx', depth: 3 },
    ]);

    expect(screen.getByText('root: docs/a/b')).toBeDefined();
    expect(screen.getByText('docs below: nothing')).toBeDefined();
  });

  it('return nothing below a layout at its own page', async () => {
    await renderAt('/dashboard', 'app/(admin)/dashboard/page.tsx');

    expect(screen.getByText('dashboard tab: none')).toBeDefined();
    expect(screen.getByText('dashboard below:')).toBeDefined();
  });
});
