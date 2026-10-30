import { Link, type LayoutProps, type Metadata } from 'netjes';
import '../styles/site.css';

export const metadata: Metadata = { title: { default: 'Netjes bench', template: '%s · Netjes bench' } };

export default function RootLayout({ children }: LayoutProps) {
  return (
    <div className="shell">
      <header>
        <Link href="/items">Items</Link>
        <Link href="/cached">Cached</Link>
      </header>
      <main>{children}</main>
    </div>
  );
}
