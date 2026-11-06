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
 * site. The generated `isHref()` and `Href` schema call this to check URLs that come from data. A URL with an ASCII
 * control character, or with a space at either end, is none of these.
 */
export function isRouteHref(value: string, patterns: readonly string[]): boolean {
  // Browsers drop tabs and newlines anywhere in a URL, and control characters and spaces at either end: `/\t/host`
  // would be `//host`. A C# Href refuses the same.
  if (/[\u0000-\u001F\u007F]/.test(value) || value.startsWith(' ') || value.endsWith(' ')) return false;
  if (/^(https?:|mailto:|tel:)/i.test(value) || value.startsWith('?') || value.startsWith('#')) return true;
  // "//host" and "/\host" are other hosts as far as browsers are concerned.
  if (!value.startsWith('/') || value.startsWith('//') || value.startsWith('/\\')) return false;
  const path = (value.split(/[?#]/, 1)[0] ?? '').split('/').filter(Boolean);
  return patterns.some((pattern) => matches(pattern.split('/').filter(Boolean), path));
}

/**
 * Builds the URL for a route pattern written in file-name syntax (`/blog/[slug]`, `/docs/[...path]`,
 * `/shop/[[...filters]]`), encoding every segment value. `options.hash` adds a fragment after the query: the id of an
 * element in the page, with one leading `#` dropped and the rest encoded; with nothing left, no fragment is added. The
 * generated `route()` helper calls this with typed params.
 */
export function buildHref(
  pattern: string,
  params: Record<string, unknown>,
  search?: SearchParamsInput,
  options?: { hash?: string | undefined },
): string {
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
  appendSearch(query, search);
  const queryString = query.toString();
  const fragment = (options?.hash ?? '').replace(/^#/, '');
  return `/${segments.join('/')}${queryString ? `?${queryString}` : ''}${fragment ? `#${encodeURIComponent(fragment)}` : ''}`;
}

/** Appends each value of `search`, a list repeating its key; `null` and `undefined` leave the key out. */
function appendSearch(query: URLSearchParams, search: Readonly<Record<string, unknown>> | undefined): void {
  for (const [key, value] of Object.entries(search ?? {})) {
    for (const item of Array.isArray(value) ? (value as readonly unknown[]) : [value]) {
      if (item !== undefined && item !== null) query.append(key, String(item));
    }
  }
}

/** A missing route value: `undefined`, `null` or `''`. */
function absent(value: unknown): boolean {
  return value === undefined || value === null || value === '';
}

/**
 * Builds the URL of one of the app's endpoints from its route template, as the generated file writes it: the
 * literals as they are (with `%`, `{`, `}`, `#` and `\\` encoded), `{name}`, `{name?}`, `{name=default}` (the default encoded),
 * `{*name}` and `{**name}`, without constraints. Each route value is encoded; a `{**name}` value keeps its slashes,
 * and a `{*name}` value has them encoded. A missing, `null` or empty optional value drops its segment, or its part of
 * a segment with the `.` before it (`{name}.{ext?}`); a missing value with a default is filled in, except at the end,
 * where it is dropped; a missing required value throws `netjes: endpoint '<endpoint>' needs the route value
 * '<name>'`. The values the template does not name are the query string, followed by `search`. The result is
 * root-relative and has no base path: pass it through `useHref` for a `fetch` or an `<a href>`. The generated
 * `endpointUrl()` calls this with typed values.
 */
export function buildEndpointUrl(
  template: string,
  values: Readonly<Record<string, unknown>>,
  search?: SearchParamsInput,
  endpoint?: string,
): string {
  const named = new Set<string>();
  // Each segment's text, and whether it is a lone value that its default filled in (dropped at the end).
  const segments: { text: string; defaulted: boolean }[] = [];
  for (const segment of template.split('/').filter(Boolean)) {
    let text = '';
    let defaulted = false;
    for (const part of segment.split(/(\{[^}]*\})/).filter(Boolean)) {
      const parameter = /^\{(\*\*?)?([^=?]+?)(\?|=([^]*))?\}$/.exec(part);
      if (!parameter) {
        text += part;
        continue;
      }

      const [, star, name = '', marker, fallback] = parameter;
      named.add(name);
      const value = values[name];
      if (!absent(value)) {
        text += star === '**' ? String(value).split('/').map((piece) => encodeURIComponent(piece)).join('/') : encodeURIComponent(String(value));
      } else if (fallback !== undefined) {
        text += fallback;
        defaulted = part === segment;
      } else if (marker === '?' || star) {
        // An optional value after a literal `.` takes the dot with it.
        if (text.endsWith('.')) text = text.slice(0, -1);
      } else {
        throw new Error(`netjes: endpoint '${endpoint ?? template}' needs the route value '${name}'`);
      }
    }
    if (text) segments.push({ text, defaulted });
  }
  while (segments.at(-1)?.defaulted) segments.pop();

  const query = new URLSearchParams();
  appendSearch(query, Object.fromEntries(Object.entries(values).filter(([key]) => !named.has(key))));
  appendSearch(query, search);
  const queryString = query.toString();
  return `/${segments.map((s) => s.text).join('/')}${queryString ? `?${queryString}` : ''}`;
}
