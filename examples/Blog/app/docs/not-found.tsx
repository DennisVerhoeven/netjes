import { Link, type Metadata, usePathname } from 'netjes';

export const metadata: Metadata = { title: 'No such doc' };

/** A docs URL that matches no doc: shown inside the docs layout, so the table of contents stays. */
export default function DocNotFound() {
  const pathname = usePathname();
  return (
    <section className="prose">
      <h1>No doc at {pathname}</h1>
      <p>
        Pick one from the list, or start with the <Link href="/docs/introduction">introduction</Link>.
      </p>
    </section>
  );
}
