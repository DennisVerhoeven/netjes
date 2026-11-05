import { type CSSProperties, useEffect, useRef, useState, useSyncExternalStore } from 'react';

/**
 * Out of sight and still read by screen readers. React sets these styles through the CSSOM on an element it creates
 * in the browser, which a Content-Security-Policy without `'unsafe-inline'` in `style-src` allows.
 */
const VISUALLY_HIDDEN: CSSProperties = {
  position: 'absolute',
  width: 1,
  height: 1,
  margin: -1,
  padding: 0,
  border: 0,
  overflow: 'hidden',
  clip: 'rect(0 0 0 0)',
  clipPath: 'inset(50%)',
  whiteSpace: 'nowrap',
};

const subscribe = () => () => {};
const inBrowser = () => true;
const onServer = () => false;

/** A segment as a person reads it, `café` rather than `caf%C3%A9`; one that does not decode stays as it is. */
function decodeSegment(segment: string): string {
  try {
    return decodeURIComponent(segment);
  } catch {
    return segment;
  }
}

/** How a screen reader can name the page on screen: its title, else its first `h1`, else its path, decoded. */
function pageName(path: string): string {
  return document.title.trim() || document.querySelector('h1')?.textContent?.trim() || path.split('/').map(decodeSegment).join('/');
}

type RouteAnnouncerProps = {
  /** The page's path and query: a navigation that changes neither (a refresh, a fragment link) names no page. */
  location: string;
  /** The path without the base path, the name of a page with neither a title nor an `h1`. */
  path: string;
};

/**
 * A polite live region that names the page after a client navigation, as a screen reader names the page a document
 * load opens. It renders in the browser only, after hydration, so the server's HTML has none. It has no role, which
 * would make it one more `status` or `alert` for an app's tests to find. Its effect runs after the app's, as a later
 * sibling of the pages, so it reads the title and headings the new page has set. The first run records the name of
 * the page the document loaded with; a later one, for another path or query, puts the new page's name in the region
 * when it differs from the name of the page before. An intercepted page keeps the title of the page under it, so it
 * is not named.
 */
export function RouteAnnouncer({ location, path }: RouteAnnouncerProps) {
  const rendered = useSyncExternalStore(subscribe, inBrowser, onServer);
  const [message, setMessage] = useState('');
  const named = useRef<string | null>(null);
  useEffect(() => {
    const name = pageName(path);
    if (named.current !== null && name !== named.current) setMessage(name);
    named.current = name;
  }, [location, path]);
  if (!rendered) return null;
  return (
    <div aria-live="polite" aria-atomic="true" data-netjes-route-announcer="" style={VISUALLY_HIDDEN}>
      {message}
    </div>
  );
}
