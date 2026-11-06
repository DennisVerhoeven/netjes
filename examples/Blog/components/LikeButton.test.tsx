import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { createTestApp } from 'netjes/testing';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { LikeButton, likeKeys } from './LikeButton.tsx';

const server = setupServer();
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

/**
 * Answers the count, keeping the path each request was sent to: msw matches `/api/posts/hello/likes/` too, which the
 * route group's key ends in, so the handler alone would not tell the two apart.
 */
function likes(count: number, paths: string[]) {
  return http.get('/api/posts/hello/likes', ({ request }) => {
    paths.push(new URL(request.url).pathname);
    return HttpResponse.json({ slug: 'hello', likes: count });
  });
}

describe('LikeButton', () => {
  it('shows the count the page seeded, then the live count from the API', async () => {
    const paths: string[] = [];
    server.use(likes(4, paths));
    const app = createTestApp({ queries: [{ key: likeKeys.detail('hello'), data: { slug: 'hello', likes: 3 } }] });

    render(<LikeButton slug="hello" />, { wrapper: app.Wrapper });

    expect(screen.getByRole('button', { name: '3 likes' })).toBeDefined();
    expect(await screen.findByRole('button', { name: '4 likes' })).toBeDefined();
    expect(paths).toEqual(['/api/posts/hello/likes']);
  });

  it('likes through the server action, counting up at once and settling on its result', async () => {
    const paths: string[] = [];
    server.use(likes(1, paths));
    let release: () => void = () => {};
    const answered = new Promise<void>((resolve) => {
      release = resolve;
    });
    const app = createTestApp({
      queries: [{ key: likeKeys.detail('hello'), data: { slug: 'hello', likes: 1 } }],
      actions: {
        'posts.like': async () => {
          await answered;
          return { ok: true, result: { slug: 'hello', likes: 5 }, queries: [{ key: likeKeys.detail('hello'), data: { slug: 'hello', likes: 5 } }] };
        },
      },
    });
    render(<LikeButton slug="hello" />, { wrapper: app.Wrapper });

    await userEvent.click(await screen.findByRole('button', { name: '1 like' }));

    expect(await screen.findByRole('button', { name: '2 likes' })).toBeDefined();
    release();
    expect(await screen.findByRole('button', { name: '5 likes' })).toBeDefined();
    expect(app.navigations).toEqual([{ type: 'action', name: 'posts.like', input: { slug: 'hello' } }]);
    // The seeded count is stale at once (staleTime 0), so the button fetched it as it mounted.
    expect(new Set(paths)).toEqual(new Set(['/api/posts/hello/likes']));
  });
});
