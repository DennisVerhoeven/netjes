import { type ArticleLink, Link, route } from '../generated/netjes.ts';

/** The articles, each linking to its page below the mount. */
export function ArticleList({ articles }: { articles: readonly ArticleLink[] }) {
  return (
    <ul className="m-0 grid list-none gap-3 p-0" aria-label="Articles">
      {articles.map((article) => (
        <li key={article.slug} className="rounded-lg border border-help-line bg-help-surface px-4 py-3">
          <Link href={route('/articles/[slug]', { slug: article.slug })} className="font-semibold text-help-accent">
            {article.title}
          </Link>
          <p className="mt-1 mb-0 text-sm text-help-muted">{article.summary}</p>
        </li>
      ))}
    </ul>
  );
}
