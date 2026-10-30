import type { AnchorHTMLAttributes, MouseEvent } from 'react';
import type { Href } from './href.ts';
import { useBasePath, useInternalRouter, usePathname, withBasePath } from './router.tsx';

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

function isCurrentPath(href: string, pathname: string) {
  const url = new URL(href, BASE + pathname);
  return url.origin === BASE && url.pathname === pathname;
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
  const basePath = useBasePath();
  const warm = () => {
    if (prefetch === 'intent' && !reloadDocument) router.prefetch(href);
  };

  return (
    <a
      href={withBasePath(href, basePath)}
      aria-current={isCurrentPath(href, pathname) ? 'page' : undefined}
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
