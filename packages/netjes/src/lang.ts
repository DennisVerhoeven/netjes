// The `lang` of `<html>`: a page loader's or a layout loader's `PageMetadata.Lang`, else the root's.
import type { PagePayload } from './payload.ts';

/**
 * The shape of a BCP 47 language tag (RFC 5646), as `PageMetadata.Lang` checks it in C#: a primary subtag of two to
 * eight letters, or `x` (private use) or `i` (grandfathered) with at least one subtag after it, then subtags of one to
 * eight letters and digits, each after one hyphen. ASCII only, so no quote, angle bracket or space reaches `<html>`.
 */
const LANGUAGE_TAG = /^(?:[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8})*|[xXiI](?:-[A-Za-z0-9]{1,8})+)$/;

export function isLanguageTag(value: string): boolean {
  return LANGUAGE_TAG.test(value);
}

/**
 * The document's `lang` for a page: the page loader's, else the innermost layout loader's, else the locale the page
 * was rendered in (`AddNetjesLocalization`), else `root`, the lang the app's root gave the document (the locale it was
 * first rendered in, or `NetjesOptions.Lang`). It is what the server writes for a fresh load of the page's URL
 * (`DocumentLang` in PageRenderer.cs). A reused segment holds the lang its loader gave it when it last ran.
 */
export function documentLang(payload: Pick<PagePayload, 'lang' | 'segments' | 'locale'>, root: string): string {
  if (payload.lang !== undefined) return payload.lang;
  for (let i = payload.segments.length - 1; i >= 0; i--) {
    const lang = payload.segments[i]?.lang;
    if (lang !== undefined) return lang;
  }
  return payload.locale ?? root;
}
