import { Link, type Metadata, usePathname } from 'netjes';

export const metadata: Metadata = { title: 'Not found' };

export default function NotFound() {
  return (
    <section className="space-y-4">
      <h1 className="text-3xl font-bold tracking-tight">Nothing at {usePathname()}</h1>
      <Link href="/" className="underline">
        Back to the home page
      </Link>
    </section>
  );
}
