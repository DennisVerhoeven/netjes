import { Link, definePage } from 'netjes';
import { Suspense } from 'react';
import { LikeButton } from '../../../components/LikeButton.tsx';
import { PostMeta, TagLink } from '../../../components/PostCard.tsx';
import { formatDate } from '../../../components/format.ts';
import { RelatedPosts } from '../../../components/RelatedPosts.tsx';
import { pages } from '../../../generated/netjes.ts';

// The schema is generated from the C# PostProps record; its Deferred<...> Related is a deferred() schema.
export default definePage(pages['/blog/[slug]'], function Post({ post, related }) {
  return (
    <article className="prose">
      <PostMeta>
        <Link href="/blog">← All posts</Link>
        <time dateTime={post.publishedOn}>{formatDate(post.publishedOn)}</time>
      </PostMeta>
      <h1 className="mt-4">{post.title}</h1>
      {post.body.map((paragraph) => (
        <p key={paragraph}>{paragraph}</p>
      ))}
      <footer className="mt-10 flex flex-wrap items-center justify-between gap-4 border-t border-line pt-5">
        <LikeButton slug={post.slug} />
        <span className="flex gap-2">
          {post.tags.map((tag) => (
            <TagLink key={tag} tag={tag} />
          ))}
        </span>
      </footer>
      <section aria-labelledby="related" className="mt-10">
        <h2 id="related" className="mb-3 font-sans text-base tracking-[0.1em] text-muted uppercase">
          Related posts
        </h2>
        <Suspense fallback={<p className="text-muted">Finding related posts…</p>}>
          <RelatedPosts related={related} />
        </Suspense>
      </section>
    </article>
  );
});
