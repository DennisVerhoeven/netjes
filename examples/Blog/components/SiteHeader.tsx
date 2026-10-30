import { Link, useSharedProps } from 'netjes';
import { Href, sharedProps } from '../generated/netjes.ts';

export function SiteHeader() {
  const shared = useSharedProps(sharedProps);
  return (
    <header className="flex flex-wrap items-baseline justify-between gap-x-8 gap-y-3 border-b border-line py-6">
      <Link href="/" className="font-serif text-[1.35rem] font-semibold no-underline">
        {shared.siteName}
      </Link>
      <nav aria-label="Main">
        <ul className="m-0 flex list-none gap-6 p-0">
          {shared.navigation.map((item) => (
            <li key={item.href}>
              <Link
                // Navigation comes from C# as plain strings; checked against the app's routes, not cast.
                href={Href.parse(item.href)}
                className="text-muted no-underline hover:text-ink aria-[current=page]:text-ink aria-[current=page]:shadow-[inset_0_-2px_0_var(--color-accent)]"
              >
                {item.label}
              </Link>
            </li>
          ))}
        </ul>
      </nav>
    </header>
  );
}
