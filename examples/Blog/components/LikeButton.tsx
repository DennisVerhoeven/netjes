import { useQuery } from '@tanstack/react-query';
import { useOptimistic, useTransition } from 'react';
import { LikesResponse, serverActions } from '../generated/netjes.ts';

/** Query keys. The post loader and the like action set `likes/<slug>` from C#, so keep the two in sync. */
export const likeKeys = {
  all: ['likes'] as const,
  detail: (slug: string) => [...likeKeys.all, slug] as const,
};

/** The C# server action app.MapAction("posts.like", ...), typed from its signature by the generator. */
const likePost = serverActions['posts.like'];

async function fetchLikes(slug: string): Promise<LikesResponse> {
  const response = await fetch(`/api/posts/${encodeURIComponent(slug)}/likes`);
  if (!response.ok) throw new Error(`Likes request failed with ${response.status}`);
  return LikesResponse.parse(await response.json());
}

/**
 * The count is server-rendered from the query the C# loader seeds, and refetched once the page is interactive (the
 * page is cached for 30 seconds). Liking calls a server action; the count goes up at once (useOptimistic) and the
 * action's result replaces it.
 */
export function LikeButton({ slug }: { slug: string }) {
  const likes = useQuery({ queryKey: likeKeys.detail(slug), queryFn: () => fetchLikes(slug), staleTime: 0 });
  const [pending, startTransition] = useTransition();
  const [count, addLike] = useOptimistic(likes.data?.likes, (current) => (current ?? 0) + 1);

  return (
    <button
      type="button"
      onClick={() =>
        startTransition(async () => {
          addLike(undefined);
          await likePost({ slug });
        })
      }
      disabled={pending || count === undefined}
      className="cursor-pointer rounded-full border border-line bg-surface px-4 py-2 text-[0.95rem] font-semibold text-ink hover:border-accent disabled:cursor-progress"
    >
      <span aria-hidden="true" className="text-accent">
        ♥
      </span>{' '}
      {count === undefined ? '–' : count} {count === 1 ? 'like' : 'likes'}
    </button>
  );
}
