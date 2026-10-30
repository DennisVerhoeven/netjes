import { type LayoutProps, type Metadata, useBasePath } from 'netjes';
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
      <ProgressBar />
      <WebVitals />
      <div className="mx-auto flex min-h-screen max-w-5xl flex-col px-6">
        <SiteHeader />
        <main id="content" className="flex-1 pt-10 pb-16">
          {children}
        </main>
        <footer className="border-t border-line pt-6 pb-8 text-[0.9rem] text-muted">
          <p className="m-0">React pages served by ASP.NET Core. Rendered by Netjes.</p>
        </footer>
      </div>
    </>
  );
}
