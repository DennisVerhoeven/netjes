import { Link, type LayoutProps, type Metadata, Script, useBasePath } from 'netjes';
import { WebVitals } from 'netjes/vitals';
import { ProgressBar } from '../components/ProgressBar.tsx';
import { SiteHeader } from '../components/SiteHeader.tsx';
import '../styles/global.css';

export const metadata: Metadata = {
  title: { default: 'Netjes Blog', template: '%s · Netjes Blog' },
  openGraph: { siteName: 'Netjes Blog', type: 'website' },
};

/** Wraps every page in the site chrome. Netjes provides the QueryClient, so there is no provider to set up here. */
export default function RootLayout({ children }: LayoutProps) {
  const basePath = useBasePath();
  return (
    <>
      <link rel="icon" href={`${basePath}/favicon.svg`} type="image/svg+xml" />
      {/* Runs where it stands, as the parser reaches it, before anything below it is painted: styles can tell a page
          with JavaScript from one without, from the first paint. It takes no focus, so the skip link stays first. */}
      <Script id="js-enabled" strategy="beforeInteractive">
        {"document.documentElement.classList.add('js')"}
      </Script>
      {/* The first element Tab reaches, after a page load and after every client navigation, when the router starts
          the focus over at the top of the document. It points to a place on this page, which needs no prefetch. */}
      <Link
        href="#content"
        prefetch="none"
        className="sr-only focus:not-sr-only focus:fixed focus:top-3 focus:left-3 focus:z-20 focus:rounded-card focus:bg-surface focus:px-4 focus:py-2 focus:text-ink focus:shadow-md"
      >
        Skip to content
      </Link>
      <ProgressBar />
      <WebVitals />
      <div className="mx-auto flex min-h-screen max-w-5xl flex-col px-6">
        <SiteHeader />
        <main id="content" className="flex-1 pt-10 pb-16">
          {children}
        </main>
        <footer className="border-t border-line pt-6 pb-8 text-[0.9rem] text-muted">
          <p className="m-0">
            React pages served by ASP.NET Core. Rendered by Netjes. Questions? <Link href="/help">Visit the help center</Link>.
          </p>
        </footer>
      </div>
    </>
  );
}
