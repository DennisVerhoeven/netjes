import type { LayoutProps, Metadata } from 'netjes';
import { Link } from '../generated/netjes.ts';

export const metadata: Metadata = { title: { default: 'Help center', template: '%s · Help center' } };

const navLink =
  'rounded-full px-3 py-1 text-help-accent-ink no-underline hover:bg-help-accent-ink/15 aria-[current=page]:bg-help-accent-ink/20';

/**
 * The help center's own layout, inside the app's layouts: a band with the package's name and navigation over its
 * pages. It draws no page shell of its own, so the app's header and footer stay around it. Its links are the
 * package's mounted ones, so they go below wherever the app mounted it.
 */
export default function HelpCenterLayout({ children }: LayoutProps) {
  return (
    <div data-help-center="" className="mx-auto max-w-5xl overflow-hidden rounded-xl border border-help-line bg-help-surface text-help-ink">
      <header className="flex flex-wrap items-center justify-between gap-3 bg-help-accent px-5 py-3 text-help-accent-ink">
        <Link href="/" className="text-lg font-semibold text-help-accent-ink no-underline">
          Help center
        </Link>
        <nav aria-label="Help center">
          <ul className="m-0 flex list-none gap-1 p-0">
            <li>
              <Link href="/" className={navLink}>
                Articles
              </Link>
            </li>
            <li>
              <Link href="/contact" className={navLink}>
                Contact
              </Link>
            </li>
          </ul>
        </nav>
      </header>
      <div className="px-5 py-6">{children}</div>
    </div>
  );
}
