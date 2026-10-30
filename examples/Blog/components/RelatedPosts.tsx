import { Link } from 'netjes';
import { use } from 'react';
import type { PostSummary } from '../generated/netjes.ts';

/** Reads the deferred related posts; the Suspense boundary around it shows a placeholder until they stream in. */
export function RelatedPosts({ related }: { related: PromiseLike<PostSummary[]> }) {
  const posts = use(related);
  if (posts.length === 0) return <p className="text-muted">No related posts yet.</p>;
  return (
    <ul aria-label="Related posts" className="m-0 grid list-none gap-2 p-0">
      {posts.map((post) => (
        <li key={post.slug}>
          <Link href={`/blog/${post.slug}`}>{post.title}</Link>
        </li>
      ))}
    </ul>
  );
}
