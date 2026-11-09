import { type Metadata, usePathname } from 'netjes';
import { Link } from '../generated/netjes.ts';

export const metadata: Metadata = { title: 'Not found · Help center' };

/**
 * An unknown URL below the mount, and Page.NotFound() from the package's loaders, such as an article that does not
 * exist. It renders inside the package's layout, so its links are the mounted ones. The path it names is the whole
 * one, from the core usePathname: the package's own gives the path below the mount.
 */
export default function HelpNotFound() {
  const pathname = usePathname();
  return (
    <section>
      <h1 className="mt-0 text-help-ink">No help page at {pathname}</h1>
      <p>
        <Link href="/" className="text-help-accent">
          Browse the articles
        </Link>{' '}
        or{' '}
        <Link href="/contact" className="text-help-accent">
          ask us directly
        </Link>
        .
      </p>
    </section>
  );
}
