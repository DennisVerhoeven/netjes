import { Link, definePage } from 'netjes';
import { PostCard, PostGrid } from '../../components/PostCard.tsx';
import { pages, route } from '../../generated/netjes.ts';

const filterLink = 'pill px-[0.85rem] py-[0.3rem] text-[0.9rem] aria-[current=page]:border-ink aria-[current=page]:bg-ink aria-[current=page]:text-paper';

export default definePage(pages['/blog'], function BlogIndex({ tag, tags, posts }) {
  return (
    <>
      <h1>{tag ? `Posts tagged “${tag}”` : 'All posts'}</h1>
      <nav className="mb-7 flex flex-wrap gap-2" aria-label="Filter by tag">
        <Link href="/blog" aria-current={tag === null ? 'page' : undefined} className={filterLink}>
          All
        </Link>
        {tags.map((t) => (
          <Link key={t} href={route('/blog', {}, { tag: t })} aria-current={t === tag ? 'page' : undefined} className={filterLink}>
            {t}
          </Link>
        ))}
      </nav>
      <PostGrid>
        {posts.map((post) => (
          <PostCard key={post.slug} post={post} />
        ))}
      </PostGrid>
    </>
  );
});
