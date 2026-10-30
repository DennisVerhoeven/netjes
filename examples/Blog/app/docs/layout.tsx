import { Link, type Metadata, defineLayout } from 'netjes';
import { layouts } from '../../generated/netjes.ts';

export const metadata: Metadata = { title: { template: '%s · Docs · Netjes Blog' } };

/**
 * The docs section. Its table of contents comes from a C# layout loader (MapLayoutLoader("/docs")), which runs once
 * per visit to the section: moving between docs pages only loads the page.
 */
export default defineLayout(layouts['app/docs'], function DocsLayout({ toc, children }) {
  return (
    <div className="grid gap-4 md:grid-cols-[minmax(11rem,14rem)_minmax(0,1fr)] md:gap-10">
      <nav aria-label="Documentation">
        <ul className="sticky top-6 m-0 grid list-none gap-0.5 p-0">
          {toc.map((entry) => (
            <li key={entry.path}>
              <Link
                href={`/docs/${entry.path}`}
                className="block rounded-md px-3 py-1.5 text-muted no-underline aria-[current=page]:bg-surface aria-[current=page]:text-ink aria-[current=page]:shadow-[inset_2px_0_0_var(--color-accent)]"
              >
                {entry.title}
              </Link>
            </li>
          ))}
        </ul>
      </nav>
      <div>{children}</div>
    </div>
  );
});
