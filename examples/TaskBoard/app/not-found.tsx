import { Link, type Metadata, usePathname } from 'netjes';
import { Logo } from '../components/brand.tsx';

export const metadata: Metadata = { title: 'Not found' };

export default function NotFound() {
  const pathname = usePathname();
  return (
    <main className="grid min-h-screen place-items-center px-6">
      <div className="grid max-w-md gap-4 text-center">
        <div className="mx-auto">
          <Logo />
        </div>
        <p className="key">404 · {pathname}</p>
        <h1 className="text-2xl font-semibold">Nothing lives here</h1>
        <Link href="/projects" className="btn mx-auto">
          Back to your projects
        </Link>
      </div>
    </main>
  );
}
