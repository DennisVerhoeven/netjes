import { Link, type Metadata, usePathname } from 'netjes';

export const metadata: Metadata = { title: 'Not found' };

/** Unknown URLs and a loader's Page.NotFound() without a nearer not-found file. */
export default function NotFound() {
  const pathname = usePathname();
  return (
    <section className="max-w-measure pt-8">
      <p className="m-0 font-serif text-[4.5rem]/none font-bold text-accent">404</p>
      <h1>Nothing at {pathname}</h1>
      <p>
        The page may have moved. <Link href="/">Start from the home page</Link> or browse <Link href="/blog">all posts</Link>.
      </p>
    </section>
  );
}
