import {
  type ComponentType,
  type ReactNode,
  Suspense,
  createContext,
  startTransition,
  use,
  useCallback,
  useContext,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import { actionRequest, expectActionClient, fetchAction, setActionClient } from './actions.tsx';
import { deferredRegistry, revivePayload } from './deferred.ts';
import type { EncodedForm } from './form-data.ts';
import type { Href } from './href.ts';
import { documentLang as langOf } from './lang.ts';
import { type ErrorPageProps, MissingModuleError, type ModuleLoader, type RouteModules } from './modules.ts';
import { moveFocusAfterNavigation } from './navigation-focus.ts';
import { NavigationGuards, type NavigationGuardOptions } from './navigation-guard.ts';
import {
  type ActionResponse,
  type AntiforgeryToken,
  COOKIES_HEADER,
  DATA_HEADER,
  type NavigationPayload,
  type PagePayload,
  type Params,
  REDIRECTED_HEADER,
  SEGMENTS_HEADER,
  SERVER_TIME_HEADER,
  type SegmentRef,
  type SpecialPageRef,
  parseAntiforgery,
  parseNavigationPayload,
} from './payload.ts';
import { RouteAnnouncer } from './route-announcer.tsx';
import { ErrorBoundary, LayoutDepthContext, RouteTree, asError } from './route-tree.tsx';
import { type StandardSchemaV1, parseLenient, parseOr, parseWith } from './schema.ts';

export type { NavigationAttempt, NavigationAttemptType, NavigationGuardOptions } from './navigation-guard.ts';

/**
 * A navigation, a refresh or a form post that ended without a page, while the page on screen stayed. `useNavigation()`
 * holds it until the next navigation, refresh or form post starts.
 */
export type NavigationError = {
  /**
   * What failed: a navigation to another URL (a link, a GET form, `push`, `replace`, Back or Forward), `router.refresh()`
   * (also the one a server action asks for), or a form post.
   */
  kind: 'navigate' | 'refresh' | 'submit';
  /** The status of an answer that was no page, or `null` when no answer came (the connection failed). */
  status: number | null;
  /** Where it went: the path and query, without the base path. */
  location: string;
};

/** @deprecated The name `NavigationError` had while only form posts reported a failure. */
export type SubmitError = NavigationError;

/** What the router is doing: idle, or on its way to `location` for a navigation (`loading`) or a form post (`submitting`). */
export type NavigationState =
  | { state: 'idle'; error?: NavigationError }
  | { state: 'loading' | 'submitting'; location: string };

/** What `router.push` and `router.replace` take besides the href. */
export type NavigateOptions = {
  /** Scroll to the top after navigating, or to the #fragment's element. Default true. */
  scroll?: boolean;
  /**
   * `useHistoryState` values, by name, that the entry the navigation adds or replaces starts with, over the values of a
   * page that stays on screen. The router keeps a copy (`structuredClone`).
   */
  historyState?: Readonly<Record<string, unknown>>;
};

/** The client router that `useRouter()` returns. */
export type Router = {
  /**
   * Navigates, adding a history entry. The promise resolves once the router has handed the page to React, which can be
   * before React shows it, or once the navigation has ended another way.
   */
  push(href: Href, options?: NavigateOptions): Promise<void>;
  /** Navigates, replacing the history entry. The promise resolves as `push`'s does. */
  replace(href: Href, options?: NavigateOptions): Promise<void>;
  /** Fetches the page payload and modules ahead of a navigation. Links do this on hover, focus and touch. */
  prefetch(href: Href): void;
  /**
   * Runs every loader of the page on screen again, layout loaders included, and renders the page with the new props, in
   * the same history entry and without scrolling. The promise resolves as `push`'s does.
   */
  refresh(): Promise<void>;
  back(): void;
  forward(): void;
};

export type SubmitBody = EncodedForm;

/** Called with every server action response, e.g. by the query shell to apply query updates. */
export type ActionListener = (response: ActionResponse) => void;

/**
 * What `<Link>` and `<Form>` also pass: what had the focus before the app's own `onClick` or `onSubmit` ran. Those run
 * as the navigation starts, so where they move the focus, such as back to the button of a menu that closes, is where
 * the navigation put it.
 */
type FocusAtStart = { focusedAtStart?: Element | null };

export type InternalRouter = Omit<Router, 'push' | 'replace'> & {
  push(href: Href, options?: NavigateOptions & FocusAtStart): Promise<void>;
  replace(href: Href, options?: NavigateOptions & FocusAtStart): Promise<void>;
  submit(action: URL, body: SubmitBody, options?: FocusAtStart): Promise<void>;
  onActionResponse(listener: ActionListener): () => void;
  /**
   * Called as soon as a server action call or form post says it set or deleted a cookie (signed someone in or out),
   * e.g. by the query shell to drop the previous user's queries.
   */
  onCookiesChanged(listener: () => void): () => void;
};

/**
 * A page to render. An intercepted navigation keeps the page it came from (`base`, rendered under its own key) and
 * shows the new URL's intercepting page in a slot.
 */
type Loaded = { payload: PagePayload; modules: RouteModules; url: URL; pageKey?: string; base?: Loaded };
/**
 * A page to render, or a URL to load the old-fashioned way. A form post can also get back an HTML document to show,
 * a file to download, or a failure, none of which may be re-requested (that would repeat or turn it into a GET).
 */
type LoadResult =
  | Loaded
  | { hard: URL }
  | { document: string; url: string }
  | { file: Blob; name: string }
  | { failed: number };
type PageRequest = { method: 'GET' } | ({ method: 'POST' } & SubmitBody);

function isLoaded(result: LoadResult): result is Loaded {
  return 'payload' in result;
}

/**
 * What remounts the page and its templates when it changes: the page's path, or for an intercepted page the key of the
 * page under it, which stays on screen. Two views with one key show the same page.
 */
const pageKeyOf = (loaded: Loaded) => loaded.pageKey ?? loaded.url.pathname;

type ScrollIntent = { kind: 'none' } | { kind: 'top' } | { kind: 'hash'; hash: string } | { kind: 'restore'; x: number; y: number };
type HistoryMode = 'push' | 'replace' | 'none' | 'auto';

const IDLE: NavigationState = { state: 'idle' };
const ANTIFORGERY_URL = '/_netjes/antiforgery';
const NONE: ScrollIntent = { kind: 'none' };
const TOP: ScrollIntent = { kind: 'top' };
const STATE_KEY = '__netjes';
/** Where an entry's `useHistoryState` values are, in `history.state`, beside the entry's key. */
const VALUES_KEY = '__netjesState';
/**
 * Where an entry's place in the tab's history is, in `history.state`: one more than the entry before it, the same as the
 * entry it replaced. After Back or Forward it tells the router how many steps the browser took, and which way.
 */
const INDEX_KEY = '__netjesIndex';
/**
 * On `<html>` while the client router runs in the document, and the public sign that it does: end-to-end tests wait
 * for it before they click. (The history entry's key is no such sign: the entry keeps it when the browser loads the
 * document again.) The effect that sets it must stay after the one that registers the router as the action client,
 * so that once it shows, the router has keyed the entry, listens for back and forward, and runs server action calls.
 */
const HYDRATED_ATTRIBUTE = 'data-netjes-hydrated';
const PREFETCH_TTL_MS = 30_000;
const MAX_REDIRECTS = 10;
const MAX_REMEMBERED_ENTRIES = 50;
/** What a tab posts to the app's other tabs when one of its calls or posts changed cookies. */
const COOKIES_CHANGED_MESSAGE = 'cookies-changed';

/**
 * The channel the tabs of one app share: same origin (BroadcastChannel's own scope) and same base path, as cookie
 * authentication scopes its cookie to the base path by default.
 */
const channelName = (basePath: string) => `netjes:${basePath || '/'}`;

let keyCounter = 0;
const createKey = () => `${Date.now().toString(36)}-${(keyCounter++).toString(36)}`;

function readKey(state: unknown): string | undefined {
  if (typeof state !== 'object' || state === null || !(STATE_KEY in state)) return undefined;
  const value = (state as Record<string, unknown>)[STATE_KEY];
  return typeof value === 'string' ? value : undefined;
}

function readIndex(state: unknown): number | undefined {
  const value = isRecord(state) ? state[INDEX_KEY] : undefined;
  return Number.isSafeInteger(value) ? (value as number) : undefined;
}

/** The values `useHistoryState` keeps with one history entry, by name. */
type HistoryValues = Readonly<Record<string, unknown>>;
const NO_VALUES: HistoryValues = Object.freeze({});

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function readValues(state: unknown): HistoryValues {
  const values = isRecord(state) ? state[VALUES_KEY] : undefined;
  return isRecord(values) ? values : NO_VALUES;
}

/** `values` with the names a navigation carries set over them. */
function withCarried(values: HistoryValues, carried: HistoryValues | undefined): HistoryValues {
  return carried === undefined || Object.keys(carried).length === 0 ? values : { ...values, ...carried };
}

/**
 * The state the router writes for an entry: its key, its place and its values, over `kept`, the state of the entry it
 * replaces, so the keys an app or a library put there stay. A new entry keeps nothing of the one before.
 */
function entryState(key: string, index: number | undefined, values: HistoryValues, kept: unknown = null): Record<string, unknown> {
  const state: Record<string, unknown> = { ...(isRecord(kept) ? kept : {}), [STATE_KEY]: key };
  if (index === undefined) delete state[INDEX_KEY];
  else state[INDEX_KEY] = index;
  if (Object.keys(values).length > 0) state[VALUES_KEY] = values;
  else delete state[VALUES_KEY];
  return state;
}

/**
 * Records how far the browser clock is from the server's, given a server time for "now": the `X-Netjes-Now` header
 * of a data response, or for the initial document the payload's `renderedAt` (so the browser judges seeded data
 * as fresh as the server did when it rendered, and hydration matches).
 */
export function withClockOffset<T extends PagePayload>(payload: T, serverNow: number | undefined, now = Date.now()): T {
  return serverNow === undefined || !Number.isFinite(serverNow) ? payload : { ...payload, clockOffset: now - serverNow };
}

/**
 * Root-relative hrefs (`/about`, not `//host` or `https://…`) are written as if the app were mounted at the root.
 * One that already starts with the base path (built from it on purpose) is left as it is.
 */
export function withBasePath(href: string, basePath: string): string {
  if (!basePath || !href.startsWith('/') || href.startsWith('//') || href.startsWith('/\\')) return href;
  const alreadyThere = href === basePath || href.startsWith(`${basePath}/`) || href.startsWith(`${basePath}?`) || href.startsWith(`${basePath}#`);
  return alreadyThere ? href : basePath + href;
}

function withoutBasePath(pathname: string, basePath: string): string {
  if (!basePath || !pathname.startsWith(basePath)) return pathname;
  const rest = pathname.slice(basePath.length);
  return rest === '' ? '/' : rest.startsWith('/') ? rest : pathname;
}

const samePage = (a: URL, b: URL) => a.pathname === b.pathname && a.search === b.search;

/** Only http(s) URLs are ever navigated to; `javascript:` and friends would run script in the page. */
const isWebUrl = (url: URL) => url.protocol === 'http:' || url.protocol === 'https:';

/**
 * The URL of a page the server sent (`payload.url`, the request's path and query), on the base's origin. That path
 * can start with `//` (a request for `https://app//evil.example/x`), which `new URL(path, base)` reads as a host.
 */
function pageUrl(path: string, base: string | URL): URL {
  return path.startsWith('/') ? new URL(new URL(base).origin + path) : new URL(path, base);
}

/**
 * The first page's URL. Its url is the path the browser requested, so one on another origin is not where the browser
 * is: the router then goes by the browser's location, which refresh() reloads.
 */
function initialUrl(path: string): URL {
  if (typeof window === 'undefined') return pageUrl(path, 'http://localhost');
  const url = pageUrl(path, window.location.href);
  return url.origin === window.location.origin ? url : withHash(new URL(window.location.href), '');
}

function withHash(url: URL, hash: string): URL {
  const copy = new URL(url);
  copy.hash = hash;
  return copy;
}

type FetchResult = NavigationPayload | { html: string; url: string } | { file: Blob; name: string } | { failed: number } | null;

/**
 * No answer came for a request: the connection failed, the browser is offline, or the browser refused to follow a
 * redirect (one to another origin that does not allow this one). `headers` are the request's, to ask again with.
 */
class NoAnswer extends Error {
  override name = 'NoAnswer';
  readonly url: URL;
  readonly headers: Readonly<Record<string, string>>;

  constructor(url: URL, headers: Readonly<Record<string, string>>, cause: unknown) {
    super(`netjes: no answer from ${url.href}`, { cause });
    this.url = url;
    this.headers = headers;
  }
}

/** The browser says it has no network. `navigator.onLine` is true when it cannot tell, so only `false` counts. */
const offline = () => typeof navigator !== 'undefined' && navigator.onLine === false;

/**
 * Whether `failed` answers with a redirect, asked again without following it. A redirect to another origin that does
 * not allow this one, such as an identity provider's sign-in page, fails `fetch` as a lost connection does; the browser
 * follows it itself when it loads the URL as a document.
 */
async function answersWithRedirect(failed: NoAnswer, signal: AbortSignal): Promise<boolean> {
  try {
    const response = await fetch(failed.url.href, { headers: failed.headers, credentials: 'same-origin', redirect: 'manual', signal });
    void response.body?.cancel().catch(() => {});
    // A browser hides the redirect behind an opaque answer; other runtimes hand over the 3xx itself.
    return response.type === 'opaqueredirect' || (response.status >= 300 && response.status < 400);
  } catch {
    return false;
  }
}

function attachmentName(disposition: string): string {
  const encoded = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1];
  if (encoded) {
    try {
      return decodeURIComponent(encoded);
    } catch {
      // Fall through to the plain filename.
    }
  }
  return /filename="?([^";]+)"?/i.exec(disposition)?.[1] ?? 'download';
}

/** What a request for a page sends besides its method and body. */
type FetchOptions = {
  /** The keys of the layout segments the router holds, whose loaders the server may skip (GETs only). */
  segmentKeys?: readonly string[];
  /** The antiforgery token a post sends as a header. */
  token?: AntiforgeryToken | undefined;
  /** Called as soon as a post's answer says it set or deleted a cookie. */
  onCookiesChanged?: (() => void) | undefined;
  /** A redirect led to this URL: the server answers `{ kind: 'document' }` where no page answers it. */
  redirected?: boolean;
};

/** What `Navigator.load` takes besides the URL, the request and the signal. */
type LoadOptions = {
  /** Send the keys of the layout segments the router holds, whose loaders the server may skip (GETs only). */
  reuseSegments?: boolean | undefined;
  /** Called as soon as a post's answer says it set or deleted a cookie; by default the router's own handling. */
  onCookiesChanged?: (() => void) | undefined;
  /** A redirect led to the URL: one that no page answers loads as a document. */
  redirected?: boolean | undefined;
  /** Called with each URL on this origin a redirect leads the load to, before it asks for that URL. */
  onRedirect?: ((target: URL) => void) | undefined;
};

async function fetchPayload(url: URL, request: PageRequest, signal: AbortSignal, options: FetchOptions = {}): Promise<FetchResult> {
  const { segmentKeys = [], token, onCookiesChanged, redirected = false } = options;
  const headers: Record<string, string> = { [DATA_HEADER]: '1', Accept: 'application/json' };
  if (request.method === 'GET' && redirected) headers[REDIRECTED_HEADER] = '1';
  if (request.method === 'POST' && token?.headerName && token.requestToken) headers[token.headerName] = token.requestToken;
  if (request.method === 'GET' && segmentKeys.length > 0) headers[SEGMENTS_HEADER] = segmentKeys.join(',');
  if (request.method === 'POST' && request.contentType) headers['Content-Type'] = request.contentType;
  const init: RequestInit = { method: request.method, headers, credentials: 'same-origin', signal };
  if (request.method === 'POST' && request.body !== undefined) init.body = request.body;
  let response: Response;
  try {
    response = await fetch(url.href, init);
  } catch (error) {
    if (signal.aborted) throw error;
    throw new NoAnswer(url, headers, error);
  }
  // A form post that signed someone in or out; script cannot read Set-Cookie itself.
  if (request.method === 'POST' && response.headers.get(COOKIES_HEADER) === 'changed') onCookiesChanged?.();
  const type = response.headers.get('content-type') ?? '';
  if (type.includes('application/x-ndjson') && response.body) {
    // Page.Defer: the payload first, then a line per deferred value as the server completes it.
    const registry = deferredRegistry();
    const lines = readLines(response.body, signal);
    const first = await lines.next();
    if (first.done) throw new Error('netjes: the server sent an empty page stream');
    const payload = parseNavigationPayload(JSON.parse(first.value));
    void (async () => {
      try {
        for await (const line of { [Symbol.asyncIterator]: () => lines }) {
          const item = JSON.parse(line) as { id: string; value?: unknown; error?: string };
          if (typeof item.error === 'string') registry.reject(item.id, item.error);
          else registry.resolve(item.id, item.value);
        }
      } catch (error) {
        if (!signal.aborted) console.error('netjes: reading deferred values failed', error);
      } finally {
        // Whatever the stream did not deliver will not come: fail it, so its Suspense boundary does not wait forever.
        registry.rejectPending((id) => `netjes: the page stream ended before deferred value ${id} arrived`);
      }
    })();
    if (payload.kind !== 'page') return payload;
    const serverNow = Number(response.headers.get(SERVER_TIME_HEADER) ?? Number.NaN);
    return withClockOffset(revivePayload(payload, registry.source), Number.isFinite(serverNow) ? serverNow : undefined);
  }
  if (type.includes('application/json')) {
    let payload: NavigationPayload;
    try {
      payload = parseNavigationPayload(await response.json());
    } catch (error) {
      // A server error that is no Netjes page, such as a gateway's JSON error, fails a GET; anything else is a bug.
      if (request.method === 'GET' && response.status >= 500 && !signal.aborted) return { failed: response.status };
      throw error;
    }
    if (payload.kind !== 'page') return payload;
    // Without the header (a cache in front of UseNetjes) there is no fresh server time: take the timestamps as they
    // are. Falling back to renderedAt would make a cached payload look brand new.
    const serverNow = Number(response.headers.get(SERVER_TIME_HEADER) ?? Number.NaN);
    return withClockOffset(payload, Number.isFinite(serverNow) ? serverNow : undefined);
  }
  // Something other than a Netjes page answered. A GET that failed on the server (a gateway's 502 while the app
  // restarts, say) fails; any other the browser loads instead, so the router stops reading it (a file, say).
  if (request.method === 'GET') {
    void response.body?.cancel().catch(() => {});
    return response.status >= 500 ? { failed: response.status } : null;
  }
  // A POST must not be sent again. Show what a native submission would have shown, where that is safe.
  if (!response.ok) return { failed: response.status };
  if (type.includes('text/html')) return { html: await response.text(), url: response.url || url.href };
  const disposition = response.headers.get('content-disposition') ?? '';
  if (/^\s*attachment/i.test(disposition)) return { file: await response.blob(), name: attachmentName(disposition) };
  return { failed: response.status };
}

/** The lines of a streamed body, as they arrive. */
async function* readLines(body: ReadableStream<Uint8Array>, signal: AbortSignal): AsyncGenerator<string, void, undefined> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffered = '';
  try {
    while (!signal.aborted) {
      const { value, done } = await reader.read();
      if (done) break;
      buffered += decoder.decode(value, { stream: true });
      let newline = buffered.indexOf('\n');
      while (newline >= 0) {
        const line = buffered.slice(0, newline).trim();
        buffered = buffered.slice(newline + 1);
        if (line) yield line;
        newline = buffered.indexOf('\n');
      }
    }
    const rest = (buffered + decoder.decode()).trim();
    if (rest) yield rest;
  } finally {
    reader.releaseLock();
  }
}

type NavigatorDeps = {
  loader: ModuleLoader;
  version: string;
  /** `UsePathBase`'s base path ('' at the root): added to the root-relative hrefs the app navigates to. */
  basePath: string;
  /** `letThrough`: a navigation the guards let go on, whose unload they do not ask about again. */
  hardNavigate(href: string, letThrough: boolean): void;
  showDocument(html: string, url: string): void;
  download(file: Blob, name: string): void;
  setPending(state: NavigationState): void;
  /**
   * `focusedAtStart`: what had the focus when the navigation started, which decides where the focus goes after it.
   * `entry`: the history entry the page is shown in, with its values, and whether Back or Forward put the page back
   * from memory.
   */
  commit(loaded: Loaded, scroll: ScrollIntent, focusedAtStart: Element | null, entry: CommittedEntry): void;
  /** The values of the entry on screen changed without a navigation: a page set one, or the router read them at start. */
  setEntry(entry: HistoryEntry): void;
  /** The guards that hold Back and Forward. */
  guards: NavigationGuards;
};

/**
 * The history entry on screen, as `useHistoryState` reads it: its values, and the lineage of the page that holds them,
 * to which a setter is bound.
 */
type HistoryEntry = { lineage: number; values: HistoryValues };
type CommittedEntry = HistoryEntry & { restored: boolean };

/** How `Navigator.navigate` goes. */
type NavigateCall = {
  /** Scroll to the top, or to the fragment's element. Default true. */
  scroll?: boolean | undefined;
  /** What had the focus as the navigation started: by default what has it as `navigate` is called. */
  focusedAtStart?: Element | null | undefined;
  /** `useHistoryState` values the new entry starts with. */
  historyState?: HistoryValues | undefined;
  /** The guards let it go on: a full page load it ends in does not have the browser ask again. */
  letThrough?: boolean | undefined;
  /** A server action's redirect: a URL that no page answers loads as a document. */
  redirected?: boolean | undefined;
};

type GoOptions = {
  history: HistoryMode;
  scroll: ScrollIntent | 'auto';
  pending: 'loading' | 'submitting';
  key?: string;
  usePrefetch: boolean;
  /** Let the server skip loaders of layout segments this page already has. Off for refreshes and form posts. */
  reuseSegments: boolean;
  /** Refreshing an intercepted page: load this page under it again too, and show the result in its slot again. */
  interceptOver?: URL;
  /** Back or Forward to an entry the router does not remember: the values that entry holds. */
  entryValues?: HistoryValues;
  /** The values a navigation carries into the entry it adds or replaces. */
  historyState?: HistoryValues;
  /** What had the focus as the navigation started, when the caller read it: else go() reads it as it starts. */
  focusedAtStart?: Element | null;
  /** The guards let it go on: a full page load it ends in does not have the browser ask again. */
  letThrough?: boolean;
  /** `router.refresh()`: a failure is reported as the refresh's. */
  refresh?: boolean;
  /** A server action's redirect led to the URL: one that no page answers loads as a document. */
  redirected?: boolean;
  /**
   * Back or Forward from the entry at this place: when the page does not come, the router steps back to that entry,
   * whose page stays on screen.
   */
  putBack?: number | undefined;
};

/**
 * A navigation on its way. `again` starts it over (GETs only: a form post is never sent twice), and `restarted` is that
 * new attempt, which the navigation finishes with. `stale` says another response changed cookies while it ran, so what
 * it loads shows whoever was signed in before; `changedCookies` says its own response did.
 */
type ActiveNavigation = {
  controller: AbortController;
  again: (() => void) | null;
  restarted?: Promise<void>;
  stale: boolean;
  changedCookies: boolean;
};

type Prefetch = { at: number; result: Promise<LoadResult>; controller: AbortController; loading: boolean };

/** Client-side navigation: fetches page payloads from the server, keeps history in sync, restores scroll. */
class Navigator {
  private active: ActiveNavigation | null = null;
  private readonly prefetched = new Map<string, Prefetch>();
  private readonly entries = new Map<string, Loaded>();
  private readonly scrollPositions = new Map<string, { x: number; y: number }>();
  private currentKey = createKey();
  /** The `useHistoryState` values of the entry on screen, as its `history.state` holds them. */
  private values: HistoryValues = NO_VALUES;
  /**
   * The lineage of the values on screen, to which `useHistoryState`'s setters are bound. A navigation that keeps the
   * page on screen gives the entry a new key but carries the page's values over, as the page keeps its React state, so
   * it keeps the lineage too: a setter from before it still writes. A new one starts when the values start over
   * (another page) or Back or Forward switch the entry, and a setter of the lineage before writes nothing.
   */
  private lineage = 0;
  /**
   * The browser is on another entry than the page on screen: Back or Forward went to one whose page is still loading,
   * or the router is stepping back to the page's entry (a guard held Back, or the page did not come). The page must not
   * write its values into that entry.
   */
  private betweenEntries = false;
  /** The page on screen is from before cookies changed: kept for no history entry until one comes from the server. */
  private currentIsStale = false;
  /**
   * The place of the entry on screen in the tab's history (`INDEX_KEY`), or undefined on an entry the router did not
   * make (one an app or a library pushed itself), whose place it does not know.
   */
  private index: number | undefined;
  /** A guard held Back or Forward and the router is stepping back to the entry at `index`: once there, it `ask`s. */
  private puttingBack: { index: number; ask(): void } | null = null;
  /** The place a guard let Back or Forward go to: the step there is not held again. */
  private letThrough: number | null = null;
  /** The URL of the entry on screen as the browser shows it, fragment included (the first page's payload has none). */
  private href = '';
  /** Shared with the app's other tabs, where the browser has BroadcastChannel; null until started, and once stopped. */
  private channel: BroadcastChannel | null = null;

  private readonly deps: NavigatorDeps;
  private current: Loaded;

  constructor(deps: NavigatorDeps, current: Loaded) {
    this.deps = deps;
    this.current = current;
    // The entry the document was loaded into keeps its key and values when the browser loads it again (a reload).
    // They are read here, so a page that sets a value before start() writes into that entry, and shown from start().
    if (typeof window !== 'undefined') {
      const existing = readKey(window.history.state);
      if (existing) this.currentKey = existing;
      this.values = readValues(window.history.state);
      // A new document's entry is the last one of the tab's history. Only the steps between the entries matter, so a
      // place that is off (the browser keeps 50 entries or so) changes nothing.
      this.index = readIndex(window.history.state) ?? Math.max(window.history.length - 1, 0);
    }
  }

  /** The lineage of the values on screen. */
  get entryLineage(): number {
    return this.lineage;
  }

  start(): () => void {
    window.history.scrollRestoration = 'manual';
    this.href = window.location.href;
    if (readKey(window.history.state) !== this.currentKey || readIndex(window.history.state) !== this.index) {
      window.history.replaceState(entryState(this.currentKey, this.index, this.values, window.history.state), '');
    }
    this.remember(this.currentKey, this.current);
    // The commit that hydrated the page rendered every value as its initial one, as the server did. An entry that holds
    // values (after a reload) shows them now, before the browser paints; one with none renders nothing again.
    if (this.values !== NO_VALUES) this.deps.setEntry({ lineage: this.lineage, values: this.values });

    let frame = 0;
    const onScroll = () => {
      if (frame) return;
      frame = requestAnimationFrame(() => {
        frame = 0;
        this.rememberScroll();
      });
    };
    const onPopState = (event: PopStateEvent) => this.onPopState(event);
    window.addEventListener('popstate', onPopState);
    window.addEventListener('scroll', onScroll, { passive: true });
    const channel = typeof BroadcastChannel === 'function' ? new BroadcastChannel(channelName(this.deps.basePath)) : null;
    if (channel) {
      channel.onmessage = (event: MessageEvent<unknown>) => {
        if (event.data === COOKIES_CHANGED_MESSAGE) this.cookiesChangedInAnotherTab();
      };
    }
    this.channel = channel;
    return () => {
      window.removeEventListener('popstate', onPopState);
      window.removeEventListener('scroll', onScroll);
      cancelAnimationFrame(frame);
      this.stopListeningToOtherTabs();
    };
  }

  private stopListeningToOtherTabs() {
    this.channel?.close();
    this.channel = null;
  }

  /**
   * Sets a `useHistoryState` value of the entry on screen, computed from the entry's values, in its `history.state`,
   * for a setter of the values' `lineage`. A setter of a page that has left (setting a value as it unmounts), or of the
   * entry before Back or Forward, changes nothing, and so does any setter while the browser is on another entry.
   */
  setValue(lineage: number, name: string, compute: (values: HistoryValues) => unknown) {
    if (lineage !== this.lineage || this.betweenEntries) return;
    const values = { ...this.values, [name]: compute(this.values) };
    // First, so a value the browser cannot keep throws before anything changed.
    window.history.replaceState(entryState(this.currentKey, this.index, values, window.history.state), '');
    this.values = values;
    this.deps.setEntry({ lineage, values });
  }

  async navigate(href: string, history: 'push' | 'replace', options: NavigateCall = {}): Promise<void> {
    const { scroll = true, focusedAtStart = document.activeElement, historyState, letThrough = false, redirected = false } = options;
    // A copy, as the browser keeps one: what the caller changes later does not reach the entry, and a value the
    // browser cannot keep throws here, before the navigation starts.
    const carried = historyState === undefined ? undefined : (structuredClone(historyState) as HistoryValues);
    const url = new URL(withBasePath(href, this.deps.basePath), window.location.href);
    if (!isWebUrl(url)) {
      console.error(`netjes: refusing to navigate to ${url.protocol} URL`);
      return;
    }
    if (url.origin !== window.location.origin) {
      this.hard(url);
      return;
    }

    if (url.hash && samePage(url, this.current.url)) {
      // Same document, different fragment: no fetch, just history and scroll. It supersedes any pending navigation.
      this.cancelActive();
      this.rememberScroll();
      const key = createKey();
      // The page stays, and so do its values.
      const values = withCarried(this.values, carried);
      this.index = history === 'push' ? this.nextIndex() : this.index;
      window.history[history === 'push' ? 'pushState' : 'replaceState'](
        entryState(key, this.index, values, history === 'replace' ? window.history.state : null),
        '',
        url.href,
      );
      this.setCurrent(key, { ...this.current, url }, values, true);
      this.deps.commit(this.current, scroll ? { kind: 'hash', hash: url.hash } : NONE, focusedAtStart, {
        lineage: this.lineage,
        values,
        restored: false,
      });
      return;
    }

    await this.go(url, { method: 'GET' }, {
      history,
      scroll: !scroll ? NONE : url.hash ? { kind: 'hash', hash: url.hash } : TOP,
      pending: 'loading',
      usePrefetch: true,
      reuseSegments: true,
      focusedAtStart,
      letThrough,
      redirected,
      ...(carried ? { historyState: carried } : {}),
    });
  }

  /**
   * Where a push or replace to `href` goes, as a guard is told (without the base path), or null for one no guard holds:
   * to another origin or scheme, which the browser loads (a guard's `beforeunload` asks then), or to a fragment of the
   * page on screen, which loads nothing and keeps the page.
   */
  heldTarget(href: string): string | null {
    let url: URL;
    try {
      url = new URL(withBasePath(href, this.deps.basePath), window.location.href);
    } catch {
      // navigate() fails on it as it would without a guard.
      return null;
    }
    if (!isWebUrl(url) || url.origin !== window.location.origin) return null;
    if (url.hash && samePage(url, this.current.url)) return null;
    return withoutBasePath(url.pathname, this.deps.basePath) + url.search + url.hash;
  }

  submit(action: URL, body: SubmitBody, focusedAtStart = document.activeElement): Promise<void> {
    return this.go(
      action,
      { method: 'POST', ...body },
      { history: 'auto', scroll: 'auto', pending: 'submitting', usePrefetch: false, reuseSegments: false, focusedAtStart },
    );
  }

  /**
   * Loads the page on screen again, in place: same history entry, fragment and scroll position. `keepPrefetched` after
   * cookies changed: the prefetched pages left are all from after the change.
   */
  refresh({ keepPrefetched = false } = {}): Promise<void> {
    if (!keepPrefetched) this.prefetched.clear();
    // In a modal (an intercepted page), as in Next.js: the modal stays open over the page it opened on.
    const base = this.current.base;
    return this.go(this.current.url, { method: 'GET' }, {
      history: 'replace',
      scroll: NONE,
      pending: 'loading',
      usePrefetch: false,
      reuseSegments: false,
      refresh: true,
      ...(base ? { interceptOver: withHash(base.url, '') } : {}),
    });
  }

  /** The current page: actions send its token and URL, and refresh it when they revalidate. */
  get page(): Loaded {
    return this.current;
  }

  private tokenRequest: Promise<AntiforgeryToken | undefined> | null = null;

  /**
   * A token for pages that carry none (output-cached pages leave it out so they can be shared): fetched once from
   * the server, which also sets the antiforgery cookie, then reused.
   */
  fetchedToken(): Promise<AntiforgeryToken | undefined> {
    this.tokenRequest ??= fetch(withBasePath(ANTIFORGERY_URL, this.deps.basePath), { credentials: 'same-origin', headers: { Accept: 'application/json' } })
      .then(async (response) => (response.ok ? (parseAntiforgery(await response.json()) as AntiforgeryToken) : undefined))
      .catch(() => {
        this.tokenRequest = null;
        return undefined;
      });
    return this.tokenRequest;
  }

  /** Whether a navigation is on its way (fetching or about to commit). */
  get navigating(): boolean {
    return this.active !== null;
  }

  /** Drops the fetched token; the next action or post fetches a new one (an action may have signed the user in or out). */
  forgetToken() {
    this.tokenRequest = null;
  }

  /** After a server action: the data behind any page may have changed, so prefetched pages are dropped. */
  forgetPrefetched() {
    this.prefetched.clear();
  }

  /** The page on screen is from before cookies last changed, and no page has come from the server since. */
  get fromBeforeCookieChange(): boolean {
    return this.currentIsStale;
  }

  /**
   * An action or post of this tab changed cookies (signed someone in or out). The app's other tabs share the cookies,
   * so they are told, and forget the previous user's pages too.
   */
  cookiesChanged(from?: ActiveNavigation) {
    this.forgetPagesBeforeCookieChange(from);
    this.channel?.postMessage(COOKIES_CHANGED_MESSAGE);
  }

  /**
   * Another tab of the app changed cookies: this tab forgets the same as that one did, and loads the page on screen
   * from the server, as that tab does after its call, unless a navigation on its way (started over) shows a page
   * soon. It posts nothing back, so tabs never answer each other's messages.
   */
  private cookiesChangedInAnotherTab() {
    this.forgetToken();
    this.forgetPagesBeforeCookieChange();
    if (!this.navigating) void this.refresh({ keepPrefetched: true });
  }

  /**
   * After cookies changed (signing in or out, like Next.js's rule for cookies set by an action): the pages kept for
   * back and forward, and prefetched ones, show the app as it was before, so they are loaded from the server again.
   * Requests still on their way went out with the old cookies: a navigation and the prefetches in flight start over.
   * A form post (`from`, when its own response said so) is never sent twice. The page on screen stays until a page
   * comes from the server, and is kept for no history entry, not even one a fragment link on it adds.
   */
  private forgetPagesBeforeCookieChange(from?: ActiveNavigation) {
    this.entries.clear();
    const loading = [...this.prefetched].filter(([, prefetch]) => prefetch.loading);
    this.prefetched.clear();
    for (const [href, prefetch] of loading) {
      prefetch.controller.abort();
      this.startPrefetch(new URL(href));
    }
    this.currentIsStale = true;
    const active = this.active;
    if (active === from) {
      if (from) from.changedCookies = true;
    } else if (active?.again) {
      active.again();
    } else if (active) {
      active.stale = true;
    }
    for (const listener of this.cookieListeners) listener();
  }

  private readonly cookieListeners = new Set<() => void>();

  onCookiesChanged(listener: () => void): () => void {
    this.cookieListeners.add(listener);
    return () => this.cookieListeners.delete(listener);
  }

  prefetch(href: string): void {
    const url = new URL(withBasePath(href, this.deps.basePath), window.location.href);
    if (url.origin !== window.location.origin) return;
    url.hash = '';
    const now = Date.now();
    for (const [key, entry] of this.prefetched) if (now - entry.at >= PREFETCH_TTL_MS) this.prefetched.delete(key);
    if (this.prefetched.has(url.href)) return;
    this.startPrefetch(url);
  }

  private startPrefetch(url: URL) {
    const controller = new AbortController();
    const result = this.load(url, { method: 'GET' }, controller.signal, { reuseSegments: true });
    const prefetch: Prefetch = { at: Date.now(), result, controller, loading: true };
    prefetch.result.then(
      () => {
        prefetch.loading = false;
      },
      () => {
        prefetch.loading = false;
        // A prefetch started over has taken its place.
        if (this.prefetched.get(url.href) === prefetch) this.prefetched.delete(url.href);
      },
    );
    this.prefetched.set(url.href, prefetch);
  }

  /** The place of an entry pushed after the one on screen. */
  private nextIndex(): number | undefined {
    return this.index === undefined ? undefined : this.index + 1;
  }

  private onPopState(event: PopStateEvent) {
    const index = readIndex(event.state);
    const puttingBack = this.puttingBack;
    this.puttingBack = null;
    // The step back to the entry on screen, after a guard held Back or Forward: the browser shows its URL again.
    if (puttingBack && index === puttingBack.index) {
      this.betweenEntries = false;
      puttingBack.ask();
      return;
    }
    const url = new URL(window.location.href);
    const letThrough = this.letThrough !== null && index === this.letThrough;
    this.letThrough = null;
    // A step to another page takes the place of an attempt still waiting for its answer: it is asked about in its
    // place, or it goes on. A step that keeps the page leaves the question open.
    if (!samePage(url, this.current.url)) this.deps.guards.dropAttempt();
    if (!letThrough && this.holdTraversal(url, index)) return;
    // An entry with no place on the page on screen: a fragment the browser moved to itself (a plain `<a href="#…">`, a
    // fragment typed in the address bar, `location.hash`), which fires `popstate` with no state. It adds an entry after
    // the one on screen, which gets the next place, or replaces that entry when its URL is the one on screen (the same
    // link followed again), which keeps its place. A place counted short only puts a held step back short of the
    // entry on screen; one counted long could send it past the end of the history. Either way the entry gets a key,
    // so the steps to it, from it and from the entries pushed after it are counted as any others.
    const fragment = index === undefined && samePage(url, this.current.url);
    const from = this.index;
    this.index = !fragment ? index : url.href === this.href ? this.index : this.nextIndex();
    const key = readKey(event.state) ?? createKey();
    const saved = this.scrollPositions.get(key);
    const scroll: ScrollIntent = saved ? { kind: 'restore', ...saved } : url.hash ? { kind: 'hash', hash: url.hash } : TOP;
    const cached = this.entries.get(key);
    const known = cached && samePage(cached.url, url) ? cached : samePage(url, this.current.url) ? this.current : null;
    // A fragment the browser moved to itself keeps the page, and so its values, as a fragment link does; the entry
    // starts with none of its own. Back or Forward shows the values of the entry it went to.
    const values = fragment ? withCarried(this.values, readValues(event.state)) : readValues(event.state);
    if (fragment) window.history.replaceState(entryState(key, this.index, values, event.state), '');
    if (known) {
      this.active?.controller.abort();
      // From memory, unless the page on screen simply stays (Back over a fragment link).
      const restored = known.payload !== this.current.payload;
      this.setCurrent(key, { ...known, url }, values, fragment);
      this.deps.commit(this.current, scroll, document.activeElement, { lineage: this.lineage, values, restored });
      return;
    }
    this.betweenEntries = true;
    void this.go(url, { method: 'GET' }, {
      history: 'none',
      scroll,
      pending: 'loading',
      key,
      usePrefetch: false,
      reuseSegments: true,
      entryValues: values,
      letThrough,
      putBack: from,
    });
  }

  /**
   * Holds Back or Forward to `url`, the entry at `index`, while a guard holds: steps back to the entry on screen, then
   * asks the guards, and steps to the entry the reader chose when they let it, from wherever the page is by then (a
   * fragment link may have added an entry meanwhile). Back over a fragment link keeps the page and is not held.
   * Neither is a step to an entry whose place the router does not know: it cannot tell the way back, and guessing could
   * take the reader somewhere else, so it lets it go.
   */
  private holdTraversal(url: URL, index: number | undefined): boolean {
    if (!this.deps.guards.holding || samePage(url, this.current.url)) return false;
    const from = this.index;
    if (index === undefined || from === undefined || index === from) return false;
    const steps = index - from;
    const to = withoutBasePath(url.pathname, this.deps.basePath) + url.search + url.hash;
    this.puttingBack = {
      index: from,
      ask: () =>
        void this.deps.guards.attempt(to, steps < 0 ? 'back' : 'forward', async () => {
          const now = this.index;
          if (now === undefined || now === index) return;
          this.letThrough = index;
          window.history.go(index - now);
        }),
    };
    this.betweenEntries = true;
    window.history.go(-steps);
    return true;
  }

  private async go(url: URL, request: PageRequest, options: GoOptions): Promise<void> {
    // Read before anything this navigation starts can move it: inside the click (or the keypress) that started it,
    // before React has handled that event's own state updates, such as a menu that closes. navigate() and submit()
    // pass what they read, or what a link or a form read before the app's own handler ran.
    const focusedAtStart = options.focusedAtStart === undefined ? document.activeElement : options.focusedAtStart;
    if (url.origin !== window.location.origin) {
      // navigate() and Form hand other origins to the browser, and every page URL the router keeps is on this one.
      // Refuse one that gets here all the same: a failed load would send the browser to it.
      console.error(`netjes: refusing to load ${url.origin} in the client router`);
      return;
    }
    this.active?.controller.abort();
    const controller = new AbortController();
    const navigation: ActiveNavigation = {
      controller,
      again:
        request.method === 'GET'
          ? () => {
              navigation.restarted = this.go(url, request, options);
            }
          : null,
      stale: false,
      changedCookies: false,
    };
    this.active = navigation;
    this.deps.setPending({ state: options.pending, location: withoutBasePath(url.pathname, this.deps.basePath) + url.search });
    let committed = false;
    let failure: NavigationError | undefined;
    // The request a failure belongs to. Once a form post is answered with a redirect, the post went through: from there
    // on the router is on its way to that URL, and a failure is that navigation's, not the post's, so the form does not
    // offer to send it again.
    const at: { url: URL; request: PageRequest } = { url, request };
    const onRedirect =
      request.method === 'POST'
        ? (target: URL) => {
            at.url = target;
            at.request = { method: 'GET' };
          }
        : undefined;
    try {
      const prefetched = options.usePrefetch ? this.takePrefetched(url, controller.signal) : undefined;
      const under = options.interceptOver ? this.load(options.interceptOver, { method: 'GET' }, controller.signal) : undefined;
      under?.catch(() => {});
      const result = await (prefetched ??
        this.load(url, request, controller.signal, {
          reuseSegments: options.reuseSegments,
          onCookiesChanged: () => this.cookiesChanged(navigation),
          redirected: options.redirected,
          onRedirect,
        }));
      // Superseded by another navigation, or started over because cookies changed: then it ends with the new attempt.
      if (controller.signal.aborted) return navigation.restarted;
      // A post may have changed what other pages show; do not serve them from before it. One that changed cookies has
      // dropped them already, and started those in flight over.
      if (request.method === 'POST' && !navigation.changedCookies) this.prefetched.clear();
      if ('hard' in result) {
        this.hard(result.hard, options.letThrough);
        return;
      }
      if ('document' in result) {
        // The tab belongs to that document: another tab's sign-in or sign-out must not load a page into it.
        this.stopListeningToOtherTabs();
        this.deps.showDocument(result.document, result.url);
        return;
      }
      if ('file' in result) {
        this.deps.download(result.file, result.name);
        return;
      }
      if ('failed' in result) {
        // Keep the page, and what the user typed, as it is; useNavigation() tells the page.
        console.error(
          at.request.method === 'POST'
            ? `netjes: the form submission failed with status ${result.failed}`
            : `netjes: loading ${at.url.pathname}${at.url.search} failed with status ${result.failed}, keeping the page`,
        );
        if (!this.keepPage(at.url, at.request, options)) return;
        failure = this.failure(at.url, at.request, options, result.failed);
        return;
      }

      const base = under ? await under.catch(() => null) : null;
      const shown =
        options.reuseSegments && request.method === 'GET'
          ? await this.intercepted(result)
          : base && isLoaded(base)
            ? await this.intercepted(result, base)
            : result;
      if (controller.signal.aborted) return navigation.restarted;
      const previous = this.current.url;
      const history = options.history === 'auto' ? (samePage(shown.url, previous) ? 'replace' : 'push') : options.history;
      // Back or Forward: the entry's own values. Else the page's values go with it while it stays on screen (a refresh,
      // another query or fragment), as its React state does; another page starts over. The values carried go on top.
      const carries = history !== 'none' && pageKeyOf(shown) === pageKeyOf(this.current);
      const values = history === 'none' ? (options.entryValues ?? NO_VALUES) : withCarried(carries ? this.values : NO_VALUES, options.historyState);
      let key = options.key ?? createKey();
      if (history === 'none') {
        if (!options.key) window.history.replaceState(entryState(key, this.index, values, window.history.state), '');
      } else {
        this.rememberScroll();
        key = createKey();
        const kept = history === 'replace' ? window.history.state : null;
        this.index = history === 'push' ? this.nextIndex() : this.index;
        window.history[history === 'push' ? 'pushState' : 'replaceState'](entryState(key, this.index, values, kept), '', shown.url.href);
      }

      this.currentIsStale = navigation.stale;
      this.setCurrent(key, shown, values, carries);
      this.forgetToken();
      committed = true;
      const scroll = shown.base ? NONE : options.scroll === 'auto' ? (shown.url.pathname === previous.pathname ? NONE : TOP) : options.scroll;
      this.deps.commit(shown, scroll, focusedAtStart, { lineage: this.lineage, values, restored: false });
    } catch (error) {
      if (controller.signal.aborted) return navigation.restarted;
      if (at.request.method === 'POST') {
        // Never turn a failed submission into a GET: keep the page, and what the user typed, as it is.
        console.error('netjes: the form could not be submitted', error);
        failure = this.failure(at.url, at.request, options, null);
        return;
      }
      // No answer while the browser is online: keep the page, unless the URL redirects somewhere fetch cannot follow.
      // An offline browser loads the URL as a document, as any other failure does.
      if (error instanceof NoAnswer && !offline()) {
        const redirects = await answersWithRedirect(error, controller.signal);
        if (controller.signal.aborted) return navigation.restarted;
        if (!redirects) {
          console.error(`netjes: no answer for ${at.url.pathname}${at.url.search}, keeping the page`, error.cause);
          if (!this.keepPage(at.url, at.request, options)) return;
          failure = this.failure(at.url, at.request, options, null);
          return;
        }
      }
      console.error('netjes: navigation failed, loading the page from the server instead', error);
      this.hard(at.url, options.letThrough);
    } finally {
      if (this.active === navigation) {
        this.active = null;
        if (!committed) this.deps.setPending(failure ? { state: 'idle', error: failure } : IDLE);
      }
    }
  }

  /**
   * Keeps the page on screen after a navigation failed. Back or Forward has moved the browser to the entry it went to
   * already: the router steps back to the entry of the page on screen, so the address is the page's again, and a
   * page's history values and a refresh go to that entry. Where it cannot tell that entry's place (a step from or to an
   * entry an app or a library added itself), it loads the URL as a document instead and returns false.
   */
  private keepPage(url: URL, request: PageRequest, options: GoOptions): boolean {
    if (request.method !== 'GET' || options.history !== 'none') return true;
    const now = this.index;
    const back = options.putBack;
    if (now === undefined || back === undefined || now === back) {
      this.hard(url, options.letThrough);
      return false;
    }
    this.index = back;
    // The step back is the router's own: once there, nothing more happens. Until then the browser is on the entry Back
    // went to.
    this.betweenEntries = true;
    this.puttingBack = { index: back, ask: () => {} };
    window.history.go(back - now);
    return true;
  }

  /** What `useNavigation()` reports for a navigation that kept the page on screen. */
  private failure(url: URL, request: PageRequest, options: GoOptions, status: number | null): NavigationError {
    const kind = request.method === 'POST' ? 'submit' : options.refresh ? 'refresh' : 'navigate';
    return { kind, status, location: withoutBasePath(url.pathname, this.deps.basePath) + url.search };
  }

  private async load(url: URL, request: PageRequest, signal: AbortSignal, options: LoadOptions = {}): Promise<LoadResult> {
    const { reuseSegments = false, onCookiesChanged = () => this.cookiesChanged() } = options;
    let target = url;
    let current = request;
    let redirected = options.redirected ?? false;
    // After a form post everything is loaded again: the post may have changed what any layout shows.
    let keys = reuseSegments && request.method === 'GET' ? this.heldSegmentKeys() : [];
    // Output-cached pages carry no token; a post from one needs a fetched one (as a header).
    const token = request.method === 'POST' && !this.current.payload.antiforgery ? await this.fetchedToken() : undefined;
    for (let hop = 0; hop <= MAX_REDIRECTS; hop++) {
      let payload = await fetchPayload(target, current, signal, {
        segmentKeys: keys,
        token: current.method === 'POST' ? token : undefined,
        onCookiesChanged,
        redirected,
      });
      if (payload !== null && 'kind' in payload && payload.kind === 'page' && keys.length > 0) {
        const resolved = this.withReusedSegments(payload);
        // The server reused a segment this page no longer has (it changed meanwhile): load it in full.
        payload = resolved ?? (await fetchPayload(target, current, signal, { redirected }));
      }
      if (payload === null) return { hard: target };
      if ('html' in payload) return { document: payload.html, url: payload.url };
      if ('file' in payload || 'failed' in payload) return payload;
      if (payload.kind === 'redirect') {
        target = new URL(payload.location, target);
        if (!isWebUrl(target)) throw new Error(`netjes: refusing to follow a redirect to a ${target.protocol} URL`);
        if (target.origin !== window.location.origin) return { hard: target };
        if (current.method !== 'GET') keys = [];
        current = { method: 'GET' };
        redirected = true;
        options.onRedirect?.(target);
        continue;
      }
      // A URL no page answers, such as a file: the browser loads it, and its endpoint runs once.
      if (payload.kind === 'document') return { hard: target };

      const finalUrl = pageUrl(payload.url, target);
      if (finalUrl.origin !== window.location.origin) throw new Error(`netjes: the page payload names another origin (${finalUrl.origin})`);
      if (hop === 0) finalUrl.hash = url.hash;
      if (payload.version !== this.deps.version) return { hard: finalUrl };
      try {
        return { payload, modules: await this.deps.loader.loadRoute(payload), url: finalUrl };
      } catch (error) {
        if (error instanceof MissingModuleError) return { hard: finalUrl };
        throw error;
      }
    }
    throw new Error('netjes: too many redirects');
  }

  /**
   * Intercepting routes: when the new page can be shown in a slot of a segment the current page is inside (a photo
   * opening in a modal over the feed), keep the current page and render the intercepting page in that slot.
   */
  private async intercepted(loaded: Loaded, base: Loaded = this.current.base ?? this.current): Promise<Loaded> {
    const intercepts = loaded.payload.intercepts;
    if (!intercepts?.length) return loaded;
    const intercept = intercepts.find((i) => base.payload.segments.some((s) => s.id === i.segment));
    if (!intercept) return loaded;
    const Page = (await this.deps.loader.load(intercept.page)) as ComponentType<Record<string, unknown>>;
    const { props } = loaded.payload;
    // The intercepting page's own route values: its parameters can be named differently from the page's.
    const params = intercept.params ?? loaded.payload.params;
    function InterceptedSlot() {
      return <Page {...props} params={params} />;
    }
    const segments = base.modules.segments.map((segment) =>
      segment.id === intercept.segment ? { ...segment, slots: { ...segment.slots, [intercept.slot]: InterceptedSlot } } : segment,
    );
    return {
      payload: { ...base.payload, url: loaded.payload.url },
      modules: { ...base.modules, segments },
      url: loaded.url,
      pageKey: base.pageKey ?? base.url.pathname,
      base,
    };
  }

  /** Keys of the current page's layout segments whose props the client holds. */
  private heldSegmentKeys(): string[] {
    return this.current.payload.segments.flatMap((segment) => (segment.key && segment.props && !segment.reused ? [segment.key] : []));
  }

  /** Fills in the props and lang of segments the server marked reused from the current page, or null if one is missing. */
  private withReusedSegments(payload: PagePayload): PagePayload | null {
    if (!payload.segments.some((segment) => segment.reused)) return payload;
    const held = this.current.payload.segments;
    const segments: SegmentRef[] = [];
    for (const segment of payload.segments) {
      if (!segment.reused) {
        segments.push(segment);
        continue;
      }
      const known = held.find((h) => h.id === segment.id && h.key === segment.key && h.props);
      if (!known) return null;
      segments.push({ ...segment, props: known.props, lang: known.lang, reused: undefined });
    }
    return { ...payload, segments };
  }

  private hard(url: URL, letThrough = false) {
    if (isWebUrl(url)) this.deps.hardNavigate(url.href, letThrough);
    else console.error(`netjes: refusing to navigate to ${url.protocol} URL`);
  }

  private cancelActive() {
    if (!this.active) return;
    this.active.controller.abort();
    this.active = null;
    this.deps.setPending(IDLE);
  }

  private takePrefetched(url: URL, signal: AbortSignal): Promise<LoadResult> | undefined {
    const key = withHash(url, '').href;
    const hit = this.prefetched.get(key);
    if (!hit) return undefined;
    this.prefetched.delete(key);
    if (Date.now() - hit.at >= PREFETCH_TTL_MS) return undefined;
    return hit.result.then(
      (result) =>
        // A page that failed on the server is asked for again: it may answer now.
        'failed' in result
          ? this.load(url, { method: 'GET' }, signal, { reuseSegments: true })
          : 'payload' in result
            ? { ...result, url: samePage(result.url, url) ? withHash(result.url, url.hash) : result.url }
            : result,
      () => this.load(url, { method: 'GET' }, signal, { reuseSegments: true }),
    );
  }

  /** `carries`: the page on screen stays and its values went with it, so its setters keep writing. */
  private setCurrent(key: string, loaded: Loaded, values: HistoryValues, carries: boolean) {
    this.currentKey = key;
    this.current = loaded;
    this.href = loaded.url.href;
    this.values = values;
    if (!carries) this.lineage += 1;
    this.betweenEntries = false;
    if (!this.currentIsStale) this.remember(key, loaded);
  }

  private remember(key: string, loaded: Loaded) {
    this.entries.delete(key);
    this.entries.set(key, loaded);
    while (this.entries.size > MAX_REMEMBERED_ENTRIES) {
      const oldest = this.entries.keys().next().value;
      if (oldest === undefined) break;
      this.entries.delete(oldest);
    }
  }

  private rememberScroll() {
    this.scrollPositions.set(this.currentKey, { x: window.scrollX, y: window.scrollY });
  }
}

function elementForFragment(fragment: string): HTMLElement | null {
  let decoded = fragment;
  try {
    decoded = decodeURIComponent(fragment);
  } catch {
    // Not valid percent-encoding (e.g. "#100%"): look the fragment up as written.
  }
  return document.getElementById(decoded) ?? document.getElementById(fragment);
}

/** Scrolls as the intent says, and returns the element a fragment names, which then takes the focus. */
function applyScroll(intent: ScrollIntent): HTMLElement | null {
  switch (intent.kind) {
    case 'none':
      return null;
    case 'top':
      window.scrollTo(0, 0);
      return null;
    case 'restore':
      window.scrollTo(intent.x, intent.y);
      return null;
    case 'hash': {
      const target = elementForFragment(intent.hash.slice(1));
      if (target) target.scrollIntoView();
      else window.scrollTo(0, 0);
      return target;
    }
    default: {
      const exhaustive: never = intent;
      return exhaustive;
    }
  }
}

type View = { loaded: Loaded; id: number; scroll: ScrollIntent; focusedAtStart: Element | null; restored: boolean };
type ViewValue = { payload: PagePayload; url: URL };

/** What `useHistoryState` and `useHistoryRestore` read: the entry on screen, a way to write it, and the restores. */
export type HistoryContextValue = {
  /** The lineage of the values on screen: kept while navigations carry the page's values over, new when they start over. */
  lineage: number;
  values: HistoryValues;
  /**
   * Sets the value `name` of the entry on screen to what `compute` makes of the entry's values, while the values on
   * screen are of `lineage`; else it does nothing. Stable.
   */
  set(lineage: number, name: string, compute: (values: HistoryValues) => unknown): void;
  /** Calls `listener` whenever the page on screen was shown from memory after Back or Forward. Stable. */
  onRestore(listener: () => void): () => void;
};

const RouterContext = createContext<InternalRouter | null>(null);
const ViewContext = createContext<ViewValue | null>(null);
const NavigationContext = createContext<NavigationState>(IDLE);
const HistoryContext = createContext<HistoryContextValue | null>(null);
const GuardsContext = createContext<NavigationGuards | null>(null);

export type RouterProviderProps = {
  initial: PagePayload;
  initialModules: RouteModules;
  loader: ModuleLoader;
  errorPage: SpecialPageRef | null;
  hardNavigate?: (href: string) => void;
  showDocument?: (html: string, url: string) => void;
  download?: (file: Blob, name: string) => void;
  /** Wraps every page and the error page, inside the router: where app-wide providers such as the QueryClient go. */
  shell?: ComponentType<{ children: ReactNode }> | undefined;
  /**
   * The `lang` the app's root gave the document (`DocumentInfo.lang`: the locale it was rendered in, or
   * `NetjesOptions.Lang`). With it, the router keeps `<html lang>` with the page on screen: the page loader's lang,
   * else the innermost layout loader's, else the page's locale, else this. Without it, it leaves `<html lang>` alone.
   */
  documentLang?: string | undefined;
};

function PassThrough({ children }: { children: ReactNode }) {
  return children;
}

const defaultHardNavigate = (href: string) => window.location.assign(href);

/** Shows an HTML response to a form post the way the browser would have after a native submission. */
function defaultShowDocument(html: string, url: string) {
  window.history.pushState(null, '', url);
  document.open();
  document.write(html);
  document.close();
  // document.open() removed the router's listeners; let the browser load whatever back/forward lands on.
  window.addEventListener('popstate', () => window.location.reload());
}

function defaultDownload(file: Blob, name: string) {
  const href = URL.createObjectURL(file);
  const link = document.createElement('a');
  link.href = href;
  link.download = name;
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(href), 10_000);
}

export function RouterProvider({
  initial,
  initialModules,
  loader,
  errorPage,
  hardNavigate = defaultHardNavigate,
  showDocument = defaultShowDocument,
  download = defaultDownload,
  shell: Shell = PassThrough,
  documentLang,
}: RouterProviderProps) {
  const [view, setView] = useState<View>(() => ({
    loaded: {
      payload: initial,
      modules: initialModules,
      url: initialUrl(initial.url),
    },
    id: 0,
    scroll: NONE,
    focusedAtStart: null,
    restored: false,
  }));
  const [navigation, setNavigation] = useState<NavigationState>(IDLE);
  const [guards] = useState(() => new NavigationGuards());
  const [navigator] = useState(() => {
    // Server actions called before the effect below registers this router wait for it (the server runs no effects).
    if (typeof document !== 'undefined') expectActionClient();
    return new Navigator(
      {
        loader,
        version: initial.version,
        basePath: initial.basePath ?? '',
        hardNavigate: (href, letThrough) => {
          if (letThrough) guards.letUnloadThrough();
          hardNavigate(href);
        },
        showDocument,
        download,
        setPending: setNavigation,
        commit: (loaded, scroll, focusedAtStart, { lineage, values, restored }) =>
          startTransition(() => {
            setView((previous) => ({ loaded, id: previous.id + 1, scroll, focusedAtStart, restored }));
            setNavigation(IDLE);
            // In the same transition as the page: the page that leaves never renders with the next entry's values.
            setEntry({ lineage, values });
          }),
        setEntry: (entry) => setEntry(entry),
        guards,
      },
      view.loaded,
    );
  });
  // Every value as its initial one until the router has started, as on the server, so hydration matches.
  const [entry, setEntry] = useState<HistoryEntry>(() => ({ lineage: navigator.entryLineage, values: NO_VALUES }));
  const [restoreListeners] = useState(() => new Set<() => void>());
  const [historyWriters] = useState(() => ({
    set: (lineage: number, name: string, compute: (values: HistoryValues) => unknown) => navigator.setValue(lineage, name, compute),
    onRestore: (listener: () => void) => {
      restoreListeners.add(listener);
      return () => {
        restoreListeners.delete(listener);
      };
    },
  }));
  const history = useMemo<HistoryContextValue>(() => ({ ...entry, ...historyWriters }), [entry, historyWriters]);

  // In the commit that shows the page, before the browser dispatches the clicks that came in while React hydrated it:
  // a fragment link followed then must find this history entry keyed, to come back to.
  useLayoutEffect(() => navigator.start(), [navigator]);
  const [actionListeners] = useState(() => new Set<ActionListener>());
  useEffect(
    () =>
      setActionClient({
        tokenField: () => navigator.page.payload.antiforgery?.formFieldName ?? '__RequestVerificationToken',
        async invoke(name, url, input) {
          const page = navigator.page;
          const returnUrl = page.url.pathname + page.url.search;
          const token = page.payload.antiforgery ?? (await navigator.fetchedToken());
          const actionUrl = withBasePath(url, page.payload.basePath ?? '');
          let cookiesChanged = false;
          const onCookiesChanged = () => {
            cookiesChanged = true;
            navigator.cookiesChanged();
          };
          let call = await fetchAction(name, actionUrl, actionRequest(input, token, returnUrl), onCookiesChanged);
          navigator.forgetToken();
          // Tokens belong to the signed-in user: after signing in or out (on any tab) the page's one is stale. Only a
          // call the server marks as refused for its token did not run, so only that one is sent again, once, with a
          // fresh token. Any other 400 may come from an action that changed data first.
          if (call.tokenRefused) {
            const fresh = await navigator.fetchedToken();
            if (fresh?.requestToken && fresh.requestToken !== token?.requestToken) {
              call = await fetchAction(name, actionUrl, actionRequest(input, fresh, returnUrl), onCookiesChanged);
            }
            navigator.forgetToken();
          }
          const { response } = call;
          // Changed cookies dropped the prefetched pages already, and started those in flight over.
          if (!cookiesChanged) navigator.forgetPrefetched();
          for (const listener of actionListeners) listener(response);
          // The user may have moved on while the action ran: then neither pull them back (redirect) nor reload the
          // page they left (refresh, which would also cancel their navigation).
          const movedOn = navigator.page !== page || navigator.navigating;
          if (!movedOn && response.redirect) await navigator.navigate(response.redirect, 'push', { redirected: true });
          else if (!movedOn && response.revalidate) await navigator.refresh();
          // After signing in or out (an action that set or deleted a cookie), the page on screen shows whoever was
          // signed in before until a page comes from the server: refresh it, like Next.js. That is the action's page
          // when it neither redirected nor revalidated, or redirected to a fragment of itself (which loads nothing),
          // or one the user moved on to that loaded with the old cookies while the action ran. A navigation still on
          // its way has started over already. A redirect that left the page as it was went to another site (to sign
          // in there, say), which the browser loads instead, or ended in a download: neither refreshes it.
          const handedOver = !movedOn && Boolean(response.redirect) && navigator.page === page;
          if (navigator.fromBeforeCookieChange && !navigator.navigating && !handedOver) {
            await navigator.refresh({ keepPrefetched: cookiesChanged });
          }
          return response;
        },
      }),
    [navigator, actionListeners],
  );
  // Directly after the registration above, and in a passive effect, never a layout one: React runs a component's
  // effects in order, a commit's layout effects before any passive one, and the mark must not show while server
  // action calls still wait for the router.
  useEffect(() => {
    const html = document.documentElement;
    html.setAttribute(HYDRATED_ATTRIBUTE, '');
    return () => html.removeAttribute(HYDRATED_ATTRIBUTE);
  }, []);
  // After the effects of the page that Back or Forward put back, which subscribe as it mounts: a parent's effects run
  // after its children's.
  useEffect(() => {
    if (view.restored) for (const listener of [...restoreListeners]) listener();
  }, [view, restoreListeners]);
  useEffect(() => {
    const onPageShow = (event: PageTransitionEvent) => {
      if (event.persisted) for (const listener of [...restoreListeners]) listener();
    };
    window.addEventListener('pageshow', onPageShow);
    return () => window.removeEventListener('pageshow', onPageShow);
  }, [restoreListeners]);
  // The key of the page on screen before this commit.
  const committedPageKey = useRef(pageKeyOf(view.loaded));
  // In the commit that shows the page, before the browser paints it, and after the layout effects of the page and its
  // layouts, which have focused an autoFocus element by then. Every focus call keeps the scroll position applyScroll
  // sets, the one Back restores included.
  useLayoutEffect(() => {
    if (view.id === 0) return;
    const target = applyScroll(view.scroll);
    const key = pageKeyOf(view.loaded);
    const replaced = key !== committedPageKey.current;
    committedPageKey.current = key;
    moveFocusAfterNavigation({ target, replaced, keep: view.scroll.kind === 'none', focusedAtStart: view.focusedAtStart });
  }, [view]);

  const router = useMemo<InternalRouter>(() => {
    /**
     * A push or replace, asked of the guards that hold first. What had the focus and what the entry carries are read as
     * it is called, as without a guard: a value the browser cannot copy rejects it before any guard is asked.
     */
    const guarded = (href: string, history: 'push' | 'replace', options: NavigateOptions & FocusAtStart = {}): Promise<void> => {
      const focusedAtStart = options.focusedAtStart === undefined ? document.activeElement : options.focusedAtStart;
      const to = navigator.heldTarget(href);
      if (to === null) return navigator.navigate(href, history, { ...options, focusedAtStart });
      // No guard holds: it goes at once, and takes the place of an attempt still waiting for its answer, as after a save.
      if (!guards.holding) return guards.attempt(to, history, () => navigator.navigate(href, history, { ...options, focusedAtStart }));
      let historyState: HistoryValues | undefined;
      try {
        historyState = options.historyState === undefined ? undefined : (structuredClone(options.historyState) as HistoryValues);
      } catch (error) {
        return Promise.reject(error);
      }
      return guards.attempt(to, history, () => navigator.navigate(href, history, { ...options, focusedAtStart, historyState, letThrough: true }));
    };
    return {
      push: (href, options) => guarded(href, 'push', options),
      replace: (href, options) => guarded(href, 'replace', options),
      prefetch: (href) => navigator.prefetch(href),
      refresh: () => navigator.refresh(),
      back: () => window.history.back(),
      forward: () => window.history.forward(),
      submit: (action, body, options) => navigator.submit(action, body, options?.focusedAtStart),
      onActionResponse: (listener) => {
        actionListeners.add(listener);
        return () => actionListeners.delete(listener);
      },
      onCookiesChanged: (listener) => navigator.onCookiesChanged(listener),
    };
  }, [navigator, actionListeners, guards]);
  const { payload, url } = view.loaded;
  const viewValue = useMemo<ViewValue>(() => ({ payload, url }), [payload, url]);
  // <html lang> for the page on screen. The server wrote the first page's, and Document keeps it as written, so the
  // attribute changes only when the lang does: an app that sets it itself keeps its value until then (a page whose
  // loader gives a lang, or another locale), and a page without one after that has the root's. In the commit that
  // shows the page, before it is painted.
  const lang = documentLang === undefined ? undefined : langOf(payload, documentLang);
  const shownLang = useRef(lang);
  useLayoutEffect(() => {
    if (lang === undefined || lang === shownLang.current) return;
    shownLang.current = lang;
    document.documentElement.lang = lang;
  }, [lang]);
  // The same element until another page is shown: a change of the router's state alone (a navigation on its way, a
  // value of the history entry) re-renders only the components that read it.
  const page = useMemo(
    () => (
      <Shell>
        <ErrorBoundary
          resetKey={view.id}
          fallback={(error) => <ErrorFallback error={error} errorPage={errorPage} loader={loader} payload={view.loaded.payload} />}
        >
          <RouteTree
            modules={view.loaded.modules}
            segments={view.loaded.payload.segments}
            props={view.loaded.payload.props}
            params={view.loaded.payload.params}
            status={view.loaded.payload.status}
            pageKind={view.loaded.payload.pageKind}
            pageKey={pageKeyOf(view.loaded)}
            retry={() => void router.refresh()}
            metadata={view.loaded.payload.metadata}
          />
        </ErrorBoundary>
      </Shell>
    ),
    [Shell, view, errorPage, loader, router],
  );

  return (
    <RouterContext value={router}>
      <GuardsContext value={guards}>
        <ViewContext value={viewValue}>
          <NavigationContext value={navigation}>
            <HistoryContext value={history}>{page}</HistoryContext>
          {/* Outside the app's shell and error boundaries, which cannot remove it, and after them, so it reads what the
              new page has set. */}
            <RouteAnnouncer location={url.pathname + url.search} path={withoutBasePath(url.pathname, payload.basePath ?? '')} />
          </NavigationContext>
        </ViewContext>
      </GuardsContext>
    </RouterContext>
  );
}

const errorPageModules = new WeakMap<ModuleLoader, Promise<RouteModules>>();

/**
 * What the router shows for an error no segment's boundary caught: the app's root error page, handed the error as it
 * was thrown (its class, its stack, a server action's status), or the built-in page, which says 500.
 */
function ErrorFallback({ error, errorPage, loader, payload }: { error: unknown; errorPage: SpecialPageRef | null; loader: ModuleLoader; payload: PagePayload }) {
  const caught = asError(error);
  const props = { status: 500, message: caught.message };
  const builtIn = <RouteTree modules={{ page: null, segments: [] }} props={props} params={payload.params} status={500} pageKey="error" />;
  if (!errorPage) return builtIn;
  let modules = errorPageModules.get(loader);
  if (!modules) {
    modules = loader.loadRoute(errorPage);
    errorPageModules.set(loader, modules);
  }
  return (
    // If the error page (or a layout around it) throws as well, fall back to the built-in page.
    <ErrorBoundary resetKey={0} fallback={() => builtIn}>
      <Suspense fallback={null}>
        <LoadedErrorPage modules={modules} props={props} error={caught} payload={payload} />
      </Suspense>
    </ErrorBoundary>
  );
}

function LoadedErrorPage({
  modules,
  props,
  error,
  payload,
}: {
  modules: Promise<RouteModules>;
  props: Record<string, unknown>;
  error: ErrorPageProps['error'];
  payload: PagePayload;
}) {
  return <RouteTree modules={use(modules)} props={props} params={payload.params} status={500} pageKind="error" pageKey="error" error={error} />;
}

/** The router's contexts without the router: for the test harness (netjes/testing), which fakes navigation. */
export function RouterContexts({
  router,
  payload,
  url,
  navigation,
  history,
  guards,
  children,
}: {
  router: InternalRouter;
  payload: PagePayload;
  url: URL;
  navigation: NavigationState;
  history: HistoryContextValue;
  guards: NavigationGuards;
  children: ReactNode;
}) {
  const view = useMemo<ViewValue>(() => ({ payload, url }), [payload, url]);
  return (
    <RouterContext value={router}>
      <GuardsContext value={guards}>
        <ViewContext value={view}>
          <NavigationContext value={navigation}>
            <HistoryContext value={history}>{children}</HistoryContext>
          </NavigationContext>
        </ViewContext>
      </GuardsContext>
    </RouterContext>
  );
}

function useView(): ViewValue {
  const view = useContext(ViewContext);
  if (!view) throw new Error('netjes: this hook must be used inside a Netjes page');
  return view;
}

export function useInternalRouter(): InternalRouter {
  const router = useContext(RouterContext);
  if (!router) throw new Error('netjes: this hook must be used inside a Netjes page');
  return router;
}

/** The client router: `push`, `replace`, `prefetch`, `refresh`, `back` and `forward`. Throws outside a Netjes page. */
export function useRouter(): Router {
  return useInternalRouter();
}

/** The path without the base path, as the app's routes see it (like Next.js's `usePathname` with `basePath`). */
export function usePathname(): string {
  const { url, payload } = useView();
  return withoutBasePath(url.pathname, payload.basePath ?? '');
}

/** An href for a plain `<a>`, another attribute or a `fetch`, under the base path: what `<Link>` does for its own. */
export function useHref(href: string): string {
  return withBasePath(href, useBasePath());
}

/** `UsePathBase`'s base path for this page, or '' when the app is mounted at the root. */
export function useBasePath(): string {
  return useView().payload.basePath ?? '';
}

/** Search values as Next.js passes them: a string, or an array of strings when the key repeats. */
function searchObject(search: URLSearchParams): Record<string, string | string[]> {
  const result: Record<string, string | string[]> = {};
  for (const key of new Set(search.keys())) {
    const values = search.getAll(key);
    result[key] = values.length === 1 ? values[0]! : values;
  }
  return result;
}

/**
 * The query string; with a schema, parsed from `{ key: string | string[] }` (repeated keys become arrays).
 * Visitors control the query string, so values the schema rejects are dropped (defaults then apply) rather than
 * failing the page. Define the schema at module level: a new schema object each render parses again.
 */
export function useSearchParams(): URLSearchParams;
export function useSearchParams<Schema extends StandardSchemaV1>(schema: Schema): StandardSchemaV1.InferOutput<Schema>;
export function useSearchParams(schema?: StandardSchemaV1): unknown {
  const { search } = useView().url;
  return useMemo(() => {
    const params = new URLSearchParams(search);
    return schema ? parseLenient(schema, searchObject(params), 'search params') : params;
  }, [search, schema]);
}

/**
 * The route parameters (catch-all parameters are arrays); with a schema, parsed by it. A rejected value throws to
 * the error boundary, so prefer C# route constraints (or a loader returning NotFound) for URLs visitors can mistype.
 */
export function useParams(): Params;
export function useParams<Schema extends StandardSchemaV1>(schema: Schema): StandardSchemaV1.InferOutput<Schema>;
export function useParams(schema?: StandardSchemaV1): unknown {
  const { params } = useView().payload;
  return useMemo(() => (schema ? parseWith(schema, params, 'route params') : params), [params, schema]);
}

/**
 * The props configured with `NetjesOptions.SharedProps` on the server, available to every page and layout.
 * Pass a schema to get them parsed and typed; without one they are `unknown`.
 */
export function useSharedProps(): unknown;
export function useSharedProps<Schema extends StandardSchemaV1>(schema: Schema): StandardSchemaV1.InferOutput<Schema>;
export function useSharedProps(schema?: StandardSchemaV1): unknown {
  const { shared } = useView().payload;
  return useMemo(() => (schema ? parseWith(schema, shared, 'shared props') : shared), [shared, schema]);
}

function decodeSegment(segment: string): string {
  try {
    return decodeURIComponent(segment);
  } catch {
    return segment;
  }
}

/**
 * In a layout: the URL segments below it, decoded (route groups do not appear in URLs). Next.js
 * `useSelectedLayoutSegments`. Outside layouts, the whole path.
 */
export function useSelectedLayoutSegments(): string[] {
  const depth = useContext(LayoutDepthContext);
  const pathname = usePathname();
  return useMemo(() => pathname.split('/').filter(Boolean).slice(depth).map(decodeSegment), [pathname, depth]);
}

/** In a layout: the active segment one level below it (e.g. the selected tab), or null at the layout's own page. */
export function useSelectedLayoutSegment(): string | null {
  return useSelectedLayoutSegments()[0] ?? null;
}

export function usePagePayload(): PagePayload {
  return useView().payload;
}

/** The router's state: `loading` during a navigation, `submitting` during a form post, else `idle` (with `error` after a post that produced no page). */
export function useNavigation(): NavigationState {
  return useContext(NavigationContext);
}

const NOT_SET = Symbol('not set');

/** Sets a value of `useHistoryState`: the value itself, or a function of the value before. */
export type SetHistoryState<T> = (next: T | ((previous: T) => T)) => void;

function useHistory(): HistoryContextValue {
  const history = useContext(HistoryContext);
  if (!history) throw new Error('netjes: this hook must be used inside a Netjes page');
  return history;
}

/**
 * A value kept with the history entry on screen, by `name`, like `useState`: Back, Forward and a reload bring the
 * entry's value back. A new entry starts from `initial`, or from the value its navigation carried (`historyState` on
 * `<Link>`, `router.push` and `router.replace`); a navigation that keeps the page on screen (another query, a fragment,
 * a refresh) keeps its values. The value is `initial` on the server and in the render that hydrates the page. Keep it
 * small and to what the browser can copy (`structuredClone`): ids, names and numbers, not rows.
 *
 * The setter writes into the entry on screen, and like `useState`'s, keeps working and stays the same function through
 * a navigation that keeps the page and its values: `await action(); setTicked([])` clears the ticks after an action
 * that refreshes the page. Once the page has left, or Back or Forward has gone to another entry, it changes nothing.
 *
 * With a schema, the entry's value is parsed by it, and one it rejects gives `initial`: an entry can hold what an older
 * build of the app, or another page, put there. Define the schema at module level.
 */
export function useHistoryState<T>(name: string, initial: T): [T, SetHistoryState<T>];
export function useHistoryState<Schema extends StandardSchemaV1>(
  name: string,
  initial: StandardSchemaV1.InferOutput<Schema>,
  schema: Schema,
): [StandardSchemaV1.InferOutput<Schema>, SetHistoryState<StandardSchemaV1.InferOutput<Schema>>];
export function useHistoryState(name: string, initial: unknown, schema?: StandardSchemaV1): [unknown, SetHistoryState<unknown>] {
  const { lineage, values, set } = useHistory();
  // The first render's, as useState keeps its initial value: an array written inline is not a new value each render.
  const [fallback] = useState(() => initial);
  const resolve = useCallback(
    (entry: HistoryValues) => {
      if (!Object.hasOwn(entry, name)) return fallback;
      return schema ? parseOr(schema, entry[name], fallback, `history state '${name}'`) : entry[name];
    },
    [name, schema, fallback],
  );
  const stored = Object.hasOwn(values, name) ? values[name] : NOT_SET;
  // Parsed again only when the entry's value changes, not when another name's does.
  const value = useMemo(() => (stored === NOT_SET ? fallback : resolve({ [name]: stored })), [resolve, name, stored, fallback]);
  // Bound to the page's lineage, not to one entry: like a useState setter, it keeps working, and stays the same
  // function, through a navigation that keeps the page on screen with its values (a refresh, another query).
  const setValue = useCallback<SetHistoryState<unknown>>(
    (next) => set(lineage, name, (current) => (typeof next === 'function' ? (next as (previous: unknown) => unknown)(resolve(current)) : next)),
    [set, lineage, name, resolve],
  );
  return [value, setValue];
}

/**
 * Calls `callback` each time the page on screen was shown from memory after Back or Forward: a page the router kept
 * (it asks the server nothing), or the whole document the browser kept in its back-forward cache. What the page shows
 * is as old as the visit it is from, so the page can refresh itself (`router.refresh()`) or a part of it. A page the
 * router loads from the server, and Back over a fragment link of the page on screen, do not call it.
 */
export function useHistoryRestore(callback: () => void): void {
  const { onRestore } = useHistory();
  const latest = useRef(callback);
  useEffect(() => {
    latest.current = callback;
  });
  useEffect(() => onRestore(() => latest.current()), [onRestore]);
}

/**
 * Holds every client navigation while `when` is true, and asks `onAttempt` first: links, `<Form method="get">`,
 * `router.push`, `router.replace`, `router.back`, `router.forward` and the browser's Back and Forward. The navigation
 * waits until the attempt's `proceed()` lets it go on, or its `stay()` keeps the page; for Back and Forward the router
 * puts the entry back first, so the address bar shows the page on screen while the question is open. `proceed()` works
 * whenever it is called, also once `when` is false (after a save), and a navigation that comes while one waits takes its
 * place. While it holds, `beforeunload` has the browser ask before the document unloads: a reload, closing the tab, a
 * link to another site. A link to a fragment of the page on screen, which keeps the page, is not held, and neither is a
 * form post, a server action or `router.refresh()`, nor the page the server's redirect in their answer leads to (the
 * sign-in page after the session expired). With several guards holding, each is asked in turn, the one that
 * started to hold last first, and the first `stay()` ends it. `onAttempt` is the latest render's. Throws outside a
 * Netjes page.
 */
export function useNavigationGuard(options: NavigationGuardOptions): void {
  const guards = useContext(GuardsContext);
  if (!guards) throw new Error('netjes: this hook must be used inside a Netjes page');
  const latest = useRef(options);
  useEffect(() => {
    latest.current = options;
  });
  const { when } = options;
  useEffect(() => (when ? guards.register(() => latest.current) : undefined), [guards, when]);
}
