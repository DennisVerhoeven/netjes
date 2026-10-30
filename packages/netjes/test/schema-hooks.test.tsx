import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { z } from 'zod';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload } from '../src/payload.ts';
import { RouterProvider, useParams, useSearchParams, useSharedProps } from '../src/router.tsx';

const shared = z.object({ siteName: z.string(), since: z.coerce.number() });
const params = z.object({ id: z.coerce.number().int(), rest: z.array(z.string()) });
const search = z.object({ page: z.coerce.number().default(1), tag: z.union([z.string(), z.array(z.string())]).optional() });

function Page() {
  const site = useSharedProps(shared);
  const { id, rest } = useParams(params);
  const query = useSearchParams(search);
  const untyped: unknown = useSharedProps();
  return (
    <main>
      <h1>{`${site.siteName} since ${site.since + 1}`}</h1>
      <p>{`id ${id * 2} rest ${rest.join('/')}`}</p>
      <p>{`page ${query.page} tags ${JSON.stringify(query.tag)}`}</p>
      <p>{`untyped ${typeof untyped}`}</p>
    </main>
  );
}

function payload(overrides: Partial<PagePayload> = {}): PagePayload {
  return {
    kind: 'page',
    route: '/items/[id]/[...rest]',
    page: 'app/item/page.tsx',
    segments: [],
    props: {},
    params: { id: '21', rest: ['a', 'b'] },
    shared: { siteName: 'Demo', since: '2020' },
    status: 200,
    url: '/items/21/a/b?tag=x&tag=y',
    version: 'v1',
    ...overrides,
  };
}

async function renderPage(initial: PagePayload) {
  window.history.replaceState(null, '', initial.url);
  const loader = createModuleLoader({ '/app/item/page.tsx': async () => ({ default: Page }) });
  render(<RouterProvider initial={initial} initialModules={await loader.loadRoute(initial)} loader={loader} errorPage={null} />);
}

describe('schema-aware hooks', () => {
  it('parse shared props, params and search params with the given schema', async () => {
    await renderPage(payload());

    expect(screen.getByRole('heading', { name: 'Demo since 2021' })).toBeDefined();
    expect(screen.getByText('id 42 rest a/b')).toBeDefined();
    expect(screen.getByText('page 1 tags ["x","y"]')).toBeDefined();
    expect(screen.getByText('untyped object')).toBeDefined();
  });

  it('pass a single search value as a string', async () => {
    await renderPage(payload({ url: '/items/21/a/b?page=3&tag=x' }));

    expect(screen.getByText('page 3 tags "x"')).toBeDefined();
  });

  it('drop search values the schema rejects, so a visitor typing ?page=abc gets the default', async () => {
    await renderPage(payload({ url: '/items/21/a/b?page=abc&tag=x' }));

    expect(screen.getByText('page 1 tags "x"')).toBeDefined();
  });

  it('show the error page when the server sent something the schema rejects', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    await renderPage(payload({ shared: { since: 'soon' } }));

    expect(screen.getByText(/Invalid shared props: siteName: .+; since: .+/)).toBeDefined();
    vi.restoreAllMocks();
  });

  it('name the boundary that failed for params', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    await renderPage(payload({ params: { id: 'abc', rest: [] } }));

    expect(screen.getByText(/Invalid route params: id: /)).toBeDefined();
    vi.restoreAllMocks();
  });
});
