import { Link } from 'netjes';
import type { ReactNode } from 'react';
import { type PostSummary, route } from '../generated/netjes.ts';
import { formatDate } from './format.ts';

export function PostCard({ post }: { post: PostSummary }) {
  return (
    <article className="card pt-[1.35rem] pb-[1.1rem]">
      <PostMeta>
        <time dateTime={post.publishedOn}>{formatDate(post.publishedOn)}</time>
        {post.tags.map((tag) => (
          <TagLink key={tag} tag={tag} />
        ))}
      </PostMeta>
      <h2 className="mt-1.5 mb-2 text-[1.35rem]">
        <Link href={route('/blog/[slug]', { slug: post.slug })} className="no-underline">
          {post.title}
        </Link>
      </h2>
      <p className="mb-0 text-muted">{post.summary}</p>
    </article>
  );
}

export function PostMeta({ children }: { children: ReactNode }) {
  return <p className="m-0 flex flex-wrap items-center gap-x-3 gap-y-1.5 text-[0.85rem] text-muted">{children}</p>;
}

export function TagLink({ tag }: { tag: string }) {
  return (
    <Link href={route('/blog', {}, { tag })} className="pill px-[0.55rem] py-px text-[0.78rem]">
      {tag}
    </Link>
  );
}

export function PostGrid({ children }: { children: ReactNode }) {
  return <div className="grid grid-cols-[repeat(auto-fill,minmax(17rem,1fr))] gap-4">{children}</div>;
}
