/**
 * Apps register their generated route type here (the file `netjes generate` writes does it), which types the
 * `href` of `<Link>` and `router.push`: `declare module 'netjes' { interface Register { href: Href } }`.
 */
// Empty on purpose: filled in by declaration merging.
export interface Register {}

/** A URL `<Link>` and the router accept: the app's routes once registered, any string before that. */
export type Href = Register extends { href: infer H extends string } ? H : string;

/** One value of a query string: `null` and `undefined` leave the key out. */
export type SearchValue = string | number | boolean | null | undefined;
/** A query string as an object: a value per key, or a list of values for a key that repeats. */
export type SearchParamsInput = Record<string, SearchValue | readonly SearchValue[]>;

function matches(pattern: readonly string[], path: readonly string[]): boolean {
  for (let i = 0; i < pattern.length; i++) {
    const segment = pattern[i]!;
    if (segment.startsWith('[[...')) return true;
    if (segment.startsWith('[...')) return path.length > i;
    if (i >= path.length) return false;
    if (!segment.startsWith('[') && segment !== path[i]) return false;
  }
  return pattern.length === path.length;
}

/**
 * Whether a URL is one of the app's pages (by route pattern), a link within the page (`?…`, `#…`), or another
 * site. The generated `isHref()` and `Href` schema call this to check URLs that come from data.
 */
export function isRouteHref(value: string, patterns: readonly string[]): boolean {
  if (/^(https?:|mailto:|tel:)/i.test(value) || value.startsWith('?') || value.startsWith('#')) return true;
  // "//host" and "/\host" are other hosts as far as browsers are concerned.
  if (!value.startsWith('/') || value.startsWith('//') || value.startsWith('/\\')) return false;
  const path = (value.split(/[?#]/, 1)[0] ?? '').split('/').filter(Boolean);
  return patterns.some((pattern) => matches(pattern.split('/').filter(Boolean), path));
}

/**
 * Builds the URL for a route pattern written in file-name syntax (`/blog/[slug]`, `/docs/[...path]`,
 * `/shop/[[...filters]]`), encoding every segment value. The generated `route()` helper calls this with typed params.
 */
export function buildHref(pattern: string, params: Record<string, unknown>, search?: SearchParamsInput): string {
  const missing = (name: string): never => {
    throw new Error(`netjes: route '${pattern}' needs the parameter '${name}'`);
  };
  const segments = pattern
    .split('/')
    .filter(Boolean)
    .flatMap((segment): string[] => {
      const optional = /^\[\[\.\.\.(\w+)\]\]$/.exec(segment)?.[1];
      if (optional) {
        const values = params[optional];
        return Array.isArray(values) ? values.map((v) => encodeURIComponent(String(v))) : [];
      }
      const catchAll = /^\[\.\.\.(\w+)\]$/.exec(segment)?.[1];
      if (catchAll) {
        const values = params[catchAll];
        if (!Array.isArray(values) || values.length === 0) return missing(catchAll);
        return values.map((v) => encodeURIComponent(String(v)));
      }
      const single = /^\[(\w+)\]$/.exec(segment)?.[1];
      if (single) {
        const value = params[single];
        if (value === undefined || value === null || value === '') return missing(single);
        return [encodeURIComponent(String(value))];
      }
      return [segment];
    });

  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(search ?? {})) {
    for (const item of Array.isArray(value) ? value : [value]) {
      if (item !== undefined && item !== null) query.append(key, String(item));
    }
  }
  const queryString = query.toString();
  return `/${segments.join('/')}${queryString ? `?${queryString}` : ''}`;
}
