import { Image, Link, type Metadata, definePage } from 'netjes';
import { PostCard, PostGrid } from '../components/PostCard.tsx';
import { pages } from '../generated/netjes.ts';

export const metadata: Metadata = { description: 'A blog built with React pages and an ASP.NET Core backend.' };

// The props schema is generated from the C# HomeProps record the loader returns (Features/Posts/PostPages.cs).
export default definePage(pages['/'], function Home({ latest }) {
  return (
    <>
      <section className="mb-14 max-w-[44rem]">
        <p className="mt-0 mb-3 text-[0.8rem] font-semibold tracking-[0.12em] text-accent uppercase">Netjes example</p>
        <h1>React on the page, .NET behind it.</h1>
        <p className="text-[1.2rem] text-muted">
          Every page here is a React component in <code>app/</code>, server-rendered on request. The data comes from C#
          loaders, the forms post to minimal API handlers, and navigation happens in the browser.
        </p>
        <p className="mt-7 flex flex-wrap gap-3">
          <Link href="/docs/introduction" className="button">
            Read the docs
          </Link>
          <Link href="/guestbook" className="button button-secondary">
            Sign the guestbook
          </Link>
        </p>
      </section>
      {/* Above the fold: priority loads it eagerly and preloads it; the optimizer serves a size per screen. */}
      <Image
        src="/hero.png"
        width={1600}
        height={800}
        sizes="(max-width: 64rem) 100vw, 64rem"
        priority
        alt="Gradient in the blog's accent colours"
        className="mb-14 h-auto w-full rounded-card border border-line"
      />
      <section aria-labelledby="latest">
        <h2 id="latest" className="mb-4 font-sans text-base tracking-[0.1em] text-muted uppercase">
          Latest posts
        </h2>
        <PostGrid>
          {latest.map((post) => (
            <PostCard key={post.slug} post={post} />
          ))}
        </PostGrid>
      </section>
    </>
  );
});
