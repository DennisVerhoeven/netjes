import type { AnchorHTMLAttributes, MouseEvent } from 'react';
import type { Href } from './href.ts';
import { useBasePath, useInternalRouter, usePathname, useSearchParams, withBasePath } from './router.tsx';

/** The props of `<Link>`: an anchor's own, with a typed `href` and the client navigation options. */
export type LinkProps = Omit<AnchorHTMLAttributes<HTMLAnchorElement>, 'href'> & {
  /** Typed by the app's routes once `netjes generate` has registered them. */
  href: Href;
  replace?: boolean;
  /** Scroll to the top (or the #fragment) after navigating. Default true. */
  scroll?: boolean;
  /** `intent` (default) fetches the next page on hover or focus. */
  prefetch?: 'intent' | 'none';
  /** Let the browser follow it with a full page load, e.g. for a file download or a page outside Netjes. */
  reloadDocument?: boolean;
};

const BASE = 'http://netjes.invalid';

/** One trailing slash makes no other page: `/docs/` is `/docs`, and `/` stays `/`. */
function withoutTrailingSlash(path: string) {
  return path.length > 1 && path.endsWith('/') ? path.slice(0, -1) : path;
}

/** The same values, in any order, as often each. */
function sameValues(a: string[], b: string[]) {
  const sorted = [...b].sort();
  return a.length === b.length && [...a].sort().every((value, i) => value === sorted[i]);
}

/**
 * Whether the link goes to the page on screen. An href with a `#` points to a place in a page, so it never does.
 * Otherwise its URL under the base path has the page's path, give or take one trailing slash, and the page's
 * values for each query parameter it names; parameters it leaves out do not count. Only the path and the query
 * enter, against a fixed origin, so the server and the browser agree.
 */
function isCurrentPage(href: string, pathname: string, search: URLSearchParams, basePath: string) {
  if (href.includes('#')) return false;
  const query = search.toString();
  // The page's URL as the browser has it: the base path, then the page's path, even one that starts like the base path.
  const page = new URL(BASE + basePath + pathname + (query ? `?${query}` : ''));
  const target = new URL(withBasePath(href, basePath), page);
  if (target.origin !== BASE || withoutTrailingSlash(target.pathname) !== withoutTrailingSlash(page.pathname)) return false;
  return [...new Set(target.searchParams.keys())].every((key) => sameValues(target.searchParams.getAll(key), search.getAll(key)));
}

function isPlainLeftClick(event: MouseEvent<HTMLAnchorElement>) {
  const anchor = event.currentTarget;
  return (
    event.button === 0 &&
    !event.metaKey &&
    !event.ctrlKey &&
    !event.shiftKey &&
    !event.altKey &&
    (!anchor.target || anchor.target === '_self') &&
    !anchor.hasAttribute('download')
  );
}

/** An `<a>` that navigates on the client. Ctrl/cmd-click, new tabs and external URLs behave like a normal link. */
export function Link({ href, replace = false, scroll = true, prefetch = 'intent', reloadDocument = false, onClick, onMouseEnter, onFocus, onTouchStart, ...rest }: LinkProps) {
  const router = useInternalRouter();
  const pathname = usePathname();
  const search = useSearchParams();
  const basePath = useBasePath();
  const warm = () => {
    if (prefetch === 'intent' && !reloadDocument) router.prefetch(href);
  };

  return (
    <a
      href={withBasePath(href, basePath)}
      aria-current={isCurrentPage(href, pathname, search, basePath) ? 'page' : undefined}
      {...rest}
      onClick={(event) => {
        onClick?.(event);
        if (event.defaultPrevented || reloadDocument || !isPlainLeftClick(event)) return;
        if (new URL(href, window.location.href).origin !== window.location.origin) return;
        event.preventDefault();
        void (replace ? router.replace(href, { scroll }) : router.push(href, { scroll }));
      }}
      onMouseEnter={(event) => {
        onMouseEnter?.(event);
        warm();
      }}
      onFocus={(event) => {
        onFocus?.(event);
        warm();
      }}
      onTouchStart={(event) => {
        onTouchStart?.(event);
        warm();
      }}
    />
  );
}
