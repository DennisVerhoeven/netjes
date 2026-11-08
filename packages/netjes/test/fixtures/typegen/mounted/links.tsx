// The file of an app that mounts a page package and the package's own file, in one program: the app's links are typed
// by every route, the package's by its routes below the mount, and a URL from the package's C# side is one of the app's.
import { Link } from 'netjes';
import { route as appRoute } from './app.generated.ts';
import { Link as HelpLink, pages, route } from './package.generated.ts';

const article = pages['/articles/[slug]'].parse({ slug: 'a', title: 'A', back: '/help' });

export const links = (
  <>
    <Link href={appRoute('/help/articles/[slug]', { slug: 'a' })}>An article, from the app</Link>
    <Link href={article.back}>Back, to a URL from the C# side</Link>
    <HelpLink href={route('/articles/[slug]', { slug: 'a' })}>An article, from the package</HelpLink>
    <HelpLink href="/contact?topic=billing">Contact</HelpLink>
    {/* @ts-expect-error The app has no such page. */}
    <Link href="/nope">Nowhere</Link>
    {/* @ts-expect-error The package links below its mount, so a link with the mount in it names no page of the package. */}
    <HelpLink href="/help/contact">The mount twice</HelpLink>
    {/* @ts-expect-error Another site goes through the Link from 'netjes'. */}
    <HelpLink href="https://example.com">Elsewhere</HelpLink>
  </>
);
