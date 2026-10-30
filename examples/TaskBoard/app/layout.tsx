import type { LayoutProps, Metadata } from 'netjes';
import '../styles/global.css';

export const metadata: Metadata = {
  title: { default: 'TaskBoard', template: '%s · TaskBoard' },
  description: 'A small project tracker built with Netjes: React pages, ASP.NET Core behind them.',
  themeColor: '#0d0e0f',
};

/** The root of both route groups: (marketing) and the signed-in (app). */
export default function RootLayout({ children }: LayoutProps) {
  return (
    <>
      <link rel="icon" href="/favicon.svg" type="image/svg+xml" />
      {children}
    </>
  );
}
