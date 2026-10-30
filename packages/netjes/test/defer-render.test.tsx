import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { Suspense, use, useState } from 'react';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { z } from 'zod';
import { definePage } from '../src/define-page.tsx';
import { createDeferred } from '../src/deferred.ts';
import { deferred } from '../src/zod.ts';
import { Link } from '../src/link.tsx';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload } from '../src/payload.ts';
import { RouterProvider } from '../src/router.tsx';

const origin = window.location.origin;
const server = setupServer();
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

function Comments({ comments }: { comments: PromiseLike<string[]> }) {
  return <ul aria-label="Comments">{use(comments).map((c) => <li key={c}>{c}</li>)}</ul>;
}

const Post = definePage(z.object({ title: z.string(), comments: deferred(z.array(z.string())) }), function Post({ title, comments }) {
  const [clicks, setClicks] = useState(0);
  return (
    <article>
      <h1>{title}</h1>
      <button type="button" onClick={() => setClicks((c) => c + 1)}>{`clicks ${clicks}`}</button>
      <Suspense fallback={<p>loading comments…</p>}>
        <Comments comments={comments} />
      </Suspense>
      <Link href="/posts/2">Next</Link>
    </article>
  );
});

const modules = { '/app/posts/[id]/page.tsx': async () => ({ default: Post }) };

function payload(url: string, props: Record<string, unknown>): PagePayload {
  return { kind: 'page', route: '/posts/[id]', page: 'app/posts/[id]/page.tsx', segments: [], props, params: {}, shared: null, status: 200, url, version: 'v1' };
}

async function renderApp(initial: PagePayload) {
  window.history.replaceState(null, '', initial.url);
  const loader = createModuleLoader(modules);
  const initialModules = await loader.loadRoute(initial);
  await act(async () => {
    render(<RouterProvider initial={initial} initialModules={initialModules} loader={loader} errorPage={null} />);
  });
  return userEvent.setup();
}

describe('deferred page props', () => {
  it('show the Suspense fallback until the value arrives, and stay put across re-renders', async () => {
    const comments = createDeferred<unknown>();
    const user = await renderApp(payload('/posts/1', { title: 'One', comments: comments.promise }));

    expect(screen.getByText('loading comments…')).toBeDefined();
    await act(async () => comments.resolve(['first!']));

    expect((await screen.findByRole('listitem')).textContent).toBe('first!');
    // A re-render parses the props again; the value must not suspend a second time.
    await user.click(screen.getByRole('button', { name: 'clicks 0' }));
    expect(screen.getByRole('button', { name: 'clicks 1' })).toBeDefined();
    expect(screen.getByRole('listitem').textContent).toBe('first!');
  });

  it('reject when the value does not match the schema', async () => {
    const comments = createDeferred<unknown>();
    const { vi } = await import('vitest');
    vi.spyOn(console, 'error').mockImplementation(() => {});
    await renderApp(payload('/posts/1', { title: 'One', comments: comments.promise }));

    await act(async () => comments.resolve('not a list'));

    expect(await screen.findByText(/Invalid deferred value/)).toBeDefined();
    vi.restoreAllMocks();
  });

  it('stream in on client navigations: the page first, then each value', async () => {
    let finish: () => void = () => {};
    const rest = new Promise<void>((resolve) => {
      finish = resolve;
    });
    server.use(
      http.get(`${origin}/posts/2`, () => {
        const encoder = new TextEncoder();
        const body = new ReadableStream({
          async start(controller) {
            controller.enqueue(encoder.encode(`${JSON.stringify(payload('/posts/2', { title: 'Two', comments: { $netjes: 'deferred', id: 'd0' } }))}\n`));
            await rest;
            controller.enqueue(encoder.encode(`${JSON.stringify({ id: 'd0', value: ['streamed'] })}\n`));
            controller.close();
          },
        });
        return new HttpResponse(body, { headers: { 'Content-Type': 'application/x-ndjson; charset=utf-8' } });
      }),
    );
    const done = createDeferred<unknown>();
    done.resolve([]);
    const user = await renderApp(payload('/posts/1', { title: 'One', comments: done.promise }));

    await user.click(screen.getByRole('link', { name: 'Next' }));

    expect(await screen.findByRole('heading', { name: 'Two' })).toBeDefined();
    expect(screen.getByText('loading comments…')).toBeDefined();
    await act(async () => finish());
    expect((await screen.findByRole('listitem')).textContent).toBe('streamed');
  });

  it('fail the values a stream never delivered, instead of loading forever', async () => {
    const { vi } = await import('vitest');
    vi.spyOn(console, 'error').mockImplementation(() => {});
    server.use(
      http.get(`${origin}/posts/2`, () => {
        const encoder = new TextEncoder();
        const body = new ReadableStream({
          start(controller) {
            // The server failed after sending the page: d0 never comes.
            controller.enqueue(encoder.encode(`${JSON.stringify(payload('/posts/2', { title: 'Two', comments: { $netjes: 'deferred', id: 'd0' } }))}\n`));
            controller.close();
          },
        });
        return new HttpResponse(body, { headers: { 'Content-Type': 'application/x-ndjson; charset=utf-8' } });
      }),
    );
    const done = createDeferred<unknown>();
    done.resolve([]);
    const user = await renderApp(payload('/posts/1', { title: 'One', comments: done.promise }));

    await user.click(screen.getByRole('link', { name: 'Next' }));

    expect(await screen.findByText(/ended before .*d0/)).toBeDefined();
    vi.restoreAllMocks();
  });
});
