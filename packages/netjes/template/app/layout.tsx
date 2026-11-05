import { Link, type LayoutProps, type Metadata, useSharedProps } from 'netjes';
import { sharedProps } from '../generated/netjes.ts';
import '../styles/global.css';

export const metadata: Metadata = {
  title: { default: '__TITLE__', template: '%s · __TITLE__' },
  icons: { icon: '/favicon.svg' },
};

const navLink = 'text-slate-500 no-underline hover:text-slate-900 aria-[current=page]:text-slate-900';

/** The root layout: wraps every page and stays mounted across navigations. */
export default function RootLayout({ children }: LayoutProps) {
  const site = useSharedProps(sharedProps);
  return (
    <div className="mx-auto min-h-screen max-w-3xl px-6 py-10 font-sans text-slate-900">
      {/* The first element Tab reaches, after a page load and after every client navigation, when the router starts
          the focus over at the top of the document. It points to a place on this page, which needs no prefetch. */}
      <Link
        href="#content"
        prefetch="none"
        className="sr-only focus:not-sr-only focus:fixed focus:top-3 focus:left-3 focus:z-50 focus:rounded focus:bg-white focus:px-4 focus:py-2 focus:shadow"
      >
        Skip to content
      </Link>
      <header className="mb-12 flex items-baseline justify-between">
        <Link href="/" className="text-xl font-semibold no-underline">
          {site.siteName}
        </Link>
        <nav aria-label="Main" className="flex gap-5">
          <Link href="/" className={navLink}>
            Home
          </Link>
          <Link href="/about" className={navLink}>
            About
          </Link>
        </nav>
      </header>
      <main id="content">{children}</main>
    </div>
  );
}
