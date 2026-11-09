import { defineLayout } from 'netjes';
import { layouts, Link, route } from '../../generated/netjes.ts';

/**
 * The articles' layout: the list of articles next to each page in the (center) group. Its C# layout loader runs once
 * per visit to the group. An app's own page in this group, such as app/help/(center)/shortcuts/page.tsx under a mount
 * at /help, renders inside it too.
 */
export default defineLayout(layouts['app/(center)'], function CenterLayout({ articles, children }) {
  return (
    <div className="grid gap-6 md:grid-cols-[minmax(10rem,13rem)_minmax(0,1fr)]">
      <nav aria-label="Articles in the help center">
        <ul className="m-0 grid list-none gap-0.5 p-0">
          {articles.map((article) => (
            <li key={article.slug}>
              <Link
                href={route('/articles/[slug]', { slug: article.slug })}
                className="block rounded-md px-3 py-1.5 text-help-muted no-underline aria-[current=page]:bg-help-accent aria-[current=page]:text-help-accent-ink"
              >
                {article.title}
              </Link>
            </li>
          ))}
        </ul>
      </nav>
      <div>{children}</div>
    </div>
  );
});
