import {
  type ComponentType,
  type ReactNode,
  Suspense,
  createContext,
  startTransition,
  use,
  useContext,
  useEffect,
  useLayoutEffect,
  useMemo,
  useState,
} from 'react';
import { actionRequest, expectActionClient, fetchAction, setActionClient } from './actions.tsx';
import { deferredRegistry, revivePayload } from './deferred.ts';
import type { EncodedForm } from './form-data.ts';
import type { Href } from './href.ts';
import { MissingModuleError, type ModuleLoader, type RouteModules } from './modules.ts';
import {
  type ActionResponse,
  type AntiforgeryToken,
  COOKIES_HEADER,
  DATA_HEADER,
  type NavigationPayload,
  type PagePayload,
  type Params,
  SEGMENTS_HEADER,
  SERVER_TIME_HEADER,
  type SegmentRef,
  type SpecialPageRef,
  parseAntiforgery,
  parseNavigationPayload,
} from './payload.ts';
import { ErrorBoundary, LayoutDepthContext, RouteTree } from './route-tree.tsx';
import { type StandardSchemaV1, parseLenient, parseWith } from './schema.ts';

/** A form post that did not produce a page: a non-page error response (`status`), or no response at all (`null`). */
export type SubmitError = { status: number | null; action: string };

/** What the router is doing: idle, or on its way to `location` for a navigation (`loading`) or a form post (`submitting`). */
export type NavigationState =
  | { state: 'idle'; error?: SubmitError }
  | { state: 'loading' | 'submitting'; location: string };

/** The client router that `useRouter()` returns. */
export type Router = {
  push(href: Href, options?: { scroll?: boolean }): Promise<void>;
  replace(href: Href, options?: { scroll?: boolean }): Promise<void>;
  /** Fetches the page payload and modules ahead of a navigation. Links do this on hover and focus. */
  prefetch(href: Href): void;
  /** Re-runs the current page's loader and re-renders with the new props. */
  refresh(): Promise<void>;
  back(): void;
  forward(): void;
};

export type SubmitBody = EncodedForm;

/** Called with every server action response, e.g. by the query shell to apply query updates. */
export type ActionListener = (response: ActionResponse) => void;

export type InternalRouter = Router & {
  submit(action: URL, body: SubmitBody): Promise<void>;
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
type ScrollIntent = { kind: 'none' } | { kind: 'top' } | { kind: 'hash'; hash: string } | { kind: 'restore'; x: number; y: number };
type HistoryMode = 'push' | 'replace' | 'none' | 'auto';

const IDLE: NavigationState = { state: 'idle' };
const ANTIFORGERY_URL = '/_netjes/antiforgery';
const NONE: ScrollIntent = { kind: 'none' };
const TOP: ScrollIntent = { kind: 'top' };
const STATE_KEY = '__netjes';
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

async function fetchPayload(
  url: URL,
  request: PageRequest,
  signal: AbortSignal,
  segmentKeys: readonly string[] = [],
  token?: AntiforgeryToken,
  onCookiesChanged?: () => void,
): Promise<FetchResult> {
  const headers: Record<string, string> = { [DATA_HEADER]: '1', Accept: 'application/json' };
  if (request.method === 'POST' && token?.headerName && token.requestToken) headers[token.headerName] = token.requestToken;
  if (request.method === 'GET' && segmentKeys.length > 0) headers[SEGMENTS_HEADER] = segmentKeys.join(',');
  if (request.method === 'POST' && request.contentType) headers['Content-Type'] = request.contentType;
  const init: RequestInit = { method: request.method, headers, credentials: 'same-origin', signal };
  if (request.method === 'POST' && request.body !== undefined) init.body = request.body;
  const response = await fetch(url.href, init);
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
    const payload = parseNavigationPayload(await response.json());
    if (payload.kind !== 'page') return payload;
    // Without the header (a cache in front of UseNetjes) there is no fresh server time: take the timestamps as they
    // are. Falling back to renderedAt would make a cached payload look brand new.
    const serverNow = Number(response.headers.get(SERVER_TIME_HEADER) ?? Number.NaN);
    return withClockOffset(payload, Number.isFinite(serverNow) ? serverNow : undefined);
  }
  // Something other than a Netjes page answered. A GET can simply be loaded by the browser instead.
  if (request.method === 'GET') return null;
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
  hardNavigate(href: string): void;
  showDocument(html: string, url: string): void;
  download(file: Blob, name: string): void;
  setPending(state: NavigationState): void;
  commit(loaded: Loaded, scroll: ScrollIntent): void;
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
  /** The page on screen is from before cookies changed: kept for no history entry until one comes from the server. */
  private currentIsStale = false;
  /** Shared with the app's other tabs, where the browser has BroadcastChannel; null until started, and once stopped. */
  private channel: BroadcastChannel | null = null;

  private readonly deps: NavigatorDeps;
  private current: Loaded;

  constructor(deps: NavigatorDeps, current: Loaded) {
    this.deps = deps;
    this.current = current;
  }

  start(): () => void {
    window.history.scrollRestoration = 'manual';
    const existing = readKey(window.history.state);
    if (existing) this.currentKey = existing;
    else window.history.replaceState({ ...(window.history.state ?? {}), [STATE_KEY]: this.currentKey }, '');
    this.remember(this.currentKey, this.current);

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

  async navigate(href: string, history: 'push' | 'replace', scroll: boolean): Promise<void> {
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
      window.history[history === 'push' ? 'pushState' : 'replaceState']({ [STATE_KEY]: key }, '', url.href);
      this.setCurrent(key, { ...this.current, url });
      this.deps.commit(this.current, scroll ? { kind: 'hash', hash: url.hash } : NONE);
      return;
    }

    await this.go(url, { method: 'GET' }, {
      history,
      scroll: !scroll ? NONE : url.hash ? { kind: 'hash', hash: url.hash } : TOP,
      pending: 'loading',
      usePrefetch: true,
      reuseSegments: true,
    });
  }

  submit(action: URL, body: SubmitBody): Promise<void> {
    return this.go(action, { method: 'POST', ...body }, { history: 'auto', scroll: 'auto', pending: 'submitting', usePrefetch: false, reuseSegments: false });
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
    const prefetch: Prefetch = { at: Date.now(), result: this.load(url, { method: 'GET' }, controller.signal, true), controller, loading: true };
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

  private onPopState(event: PopStateEvent) {
    const url = new URL(window.location.href);
    const key = readKey(event.state) ?? createKey();
    const saved = this.scrollPositions.get(key);
    const scroll: ScrollIntent = saved ? { kind: 'restore', ...saved } : url.hash ? { kind: 'hash', hash: url.hash } : TOP;
    const cached = this.entries.get(key);
    const known = cached && samePage(cached.url, url) ? cached : samePage(url, this.current.url) ? this.current : null;
    if (known) {
      this.active?.controller.abort();
      this.setCurrent(key, { ...known, url });
      this.deps.commit(this.current, scroll);
      return;
    }
    void this.go(url, { method: 'GET' }, { history: 'none', scroll, pending: 'loading', key, usePrefetch: false, reuseSegments: true });
  }

  private async go(url: URL, request: PageRequest, options: GoOptions): Promise<void> {
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
    let failure: SubmitError | undefined;
    try {
      const prefetched = options.usePrefetch ? this.takePrefetched(url, controller.signal) : undefined;
      const under = options.interceptOver ? this.load(options.interceptOver, { method: 'GET' }, controller.signal, false) : undefined;
      under?.catch(() => {});
      const result = await (prefetched ?? this.load(url, request, controller.signal, options.reuseSegments, () => this.cookiesChanged(navigation)));
      // Superseded by another navigation, or started over because cookies changed: then it ends with the new attempt.
      if (controller.signal.aborted) return navigation.restarted;
      // A post may have changed what other pages show; do not serve them from before it. One that changed cookies has
      // dropped them already, and started those in flight over.
      if (request.method === 'POST' && !navigation.changedCookies) this.prefetched.clear();
      if ('hard' in result) {
        this.hard(result.hard);
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
        console.error(`netjes: the form submission failed with status ${result.failed}`);
        failure = { status: result.failed, action: withoutBasePath(url.pathname, this.deps.basePath) + url.search };
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
      let key = options.key ?? createKey();
      if (history === 'none') {
        if (!options.key) window.history.replaceState({ [STATE_KEY]: key }, '');
      } else {
        this.rememberScroll();
        key = createKey();
        window.history[history === 'push' ? 'pushState' : 'replaceState']({ [STATE_KEY]: key }, '', shown.url.href);
      }

      this.currentIsStale = navigation.stale;
      this.setCurrent(key, shown);
      this.forgetToken();
      committed = true;
      const scroll = shown.base ? NONE : options.scroll === 'auto' ? (shown.url.pathname === previous.pathname ? NONE : TOP) : options.scroll;
      this.deps.commit(shown, scroll);
    } catch (error) {
      if (controller.signal.aborted) return navigation.restarted;
      if (request.method === 'POST') {
        // Never turn a failed submission into a GET: keep the page, and what the user typed, as it is.
        console.error('netjes: the form could not be submitted', error);
        failure = { status: null, action: withoutBasePath(url.pathname, this.deps.basePath) + url.search };
        return;
      }
      console.error('netjes: navigation failed, loading the page from the server instead', error);
      this.hard(url);
    } finally {
      if (this.active === navigation) {
        this.active = null;
        if (!committed) this.deps.setPending(failure ? { state: 'idle', error: failure } : IDLE);
      }
    }
  }

  private async load(
    url: URL,
    request: PageRequest,
    signal: AbortSignal,
    reuseSegments: boolean,
    onCookiesChanged = () => this.cookiesChanged(),
  ): Promise<LoadResult> {
    let target = url;
    let current = request;
    // After a form post everything is loaded again: the post may have changed what any layout shows.
    let keys = reuseSegments && request.method === 'GET' ? this.heldSegmentKeys() : [];
    // Output-cached pages carry no token; a post from one needs a fetched one (as a header).
    const token = request.method === 'POST' && !this.current.payload.antiforgery ? await this.fetchedToken() : undefined;
    for (let hop = 0; hop <= MAX_REDIRECTS; hop++) {
      let payload = await fetchPayload(target, current, signal, keys, current.method === 'POST' ? token : undefined, onCookiesChanged);
      if (payload !== null && 'kind' in payload && payload.kind === 'page' && keys.length > 0) {
        const resolved = this.withReusedSegments(payload);
        // The server reused a segment this page no longer has (it changed meanwhile): load it in full.
        payload = resolved ?? (await fetchPayload(target, current, signal));
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
        continue;
      }

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

  /** Fills in the props of segments the server marked reused from the current page, or null if one is missing. */
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
      segments.push({ ...segment, props: known.props, reused: undefined });
    }
    return { ...payload, segments };
  }

  private hard(url: URL) {
    if (isWebUrl(url)) this.deps.hardNavigate(url.href);
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
        'payload' in result ? { ...result, url: samePage(result.url, url) ? withHash(result.url, url.hash) : result.url } : result,
      () => this.load(url, { method: 'GET' }, signal, true),
    );
  }

  private setCurrent(key: string, loaded: Loaded) {
    this.currentKey = key;
    this.current = loaded;
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

function applyScroll(intent: ScrollIntent) {
  switch (intent.kind) {
    case 'none':
      return;
    case 'top':
      window.scrollTo(0, 0);
      return;
    case 'restore':
      window.scrollTo(intent.x, intent.y);
      return;
    case 'hash': {
      const target = elementForFragment(intent.hash.slice(1));
      if (target) target.scrollIntoView();
      else window.scrollTo(0, 0);
      return;
    }
    default: {
      const exhaustive: never = intent;
      return exhaustive;
    }
  }
}

type View = { loaded: Loaded; id: number; scroll: ScrollIntent };
type ViewValue = { payload: PagePayload; url: URL };

const RouterContext = createContext<InternalRouter | null>(null);
const ViewContext = createContext<ViewValue | null>(null);
const NavigationContext = createContext<NavigationState>(IDLE);

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
}: RouterProviderProps) {
  const [view, setView] = useState<View>(() => ({
    loaded: {
      payload: initial,
      modules: initialModules,
      url: initialUrl(initial.url),
    },
    id: 0,
    scroll: NONE,
  }));
  const [navigation, setNavigation] = useState<NavigationState>(IDLE);
  const [navigator] = useState(() => {
    // Server actions called before the effect below registers this router wait for it (the server runs no effects).
    if (typeof document !== 'undefined') expectActionClient();
    return new Navigator(
      {
        loader,
        version: initial.version,
        basePath: initial.basePath ?? '',
        hardNavigate,
        showDocument,
        download,
        setPending: setNavigation,
        commit: (loaded, scroll) =>
          startTransition(() => {
            setView((previous) => ({ loaded, id: previous.id + 1, scroll }));
            setNavigation(IDLE);
          }),
      },
      view.loaded,
    );
  });

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
          if (!movedOn && response.redirect) await navigator.navigate(response.redirect, 'push', true);
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
  useLayoutEffect(() => {
    if (view.id > 0) applyScroll(view.scroll);
  }, [view]);

  const router = useMemo<InternalRouter>(
    () => ({
      push: (href, options) => navigator.navigate(href, 'push', options?.scroll ?? true),
      replace: (href, options) => navigator.navigate(href, 'replace', options?.scroll ?? true),
      prefetch: (href) => navigator.prefetch(href),
      refresh: () => navigator.refresh(),
      back: () => window.history.back(),
      forward: () => window.history.forward(),
      submit: (action, body) => navigator.submit(action, body),
      onActionResponse: (listener) => {
        actionListeners.add(listener);
        return () => actionListeners.delete(listener);
      },
      onCookiesChanged: (listener) => navigator.onCookiesChanged(listener),
    }),
    [navigator, actionListeners],
  );
  const { payload, modules, url } = view.loaded;
  const viewValue = useMemo<ViewValue>(() => ({ payload, url }), [payload, url]);

  return (
    <RouterContext value={router}>
      <ViewContext value={viewValue}>
        <NavigationContext value={navigation}>
          <Shell>
            <ErrorBoundary
              resetKey={view.id}
              fallback={(error) => <ErrorFallback error={error} errorPage={errorPage} loader={loader} payload={payload} />}
            >
              <RouteTree
                modules={modules}
                segments={payload.segments}
                props={payload.props}
                params={payload.params}
                status={payload.status}
                pageKind={payload.pageKind}
                pageKey={view.loaded.pageKey ?? url.pathname}
                retry={() => void router.refresh()}
                metadata={payload.metadata}
              />
            </ErrorBoundary>
          </Shell>
        </NavigationContext>
      </ViewContext>
    </RouterContext>
  );
}

const errorPageModules = new WeakMap<ModuleLoader, Promise<RouteModules>>();

function ErrorFallback({ error, errorPage, loader, payload }: { error: unknown; errorPage: SpecialPageRef | null; loader: ModuleLoader; payload: PagePayload }) {
  const props = { status: 500, message: error instanceof Error ? error.message : 'Something went wrong' };
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
        <LoadedErrorPage modules={modules} props={props} payload={payload} />
      </Suspense>
    </ErrorBoundary>
  );
}

function LoadedErrorPage({ modules, props, payload }: { modules: Promise<RouteModules>; props: Record<string, unknown>; payload: PagePayload }) {
  return <RouteTree modules={use(modules)} props={props} params={payload.params} status={500} pageKind="error" pageKey="error" />;
}

/** The router's contexts without the router: for the test harness (netjes/testing), which fakes navigation. */
export function RouterContexts({
  router,
  payload,
  url,
  navigation,
  children,
}: {
  router: InternalRouter;
  payload: PagePayload;
  url: URL;
  navigation: NavigationState;
  children: ReactNode;
}) {
  const view = useMemo<ViewValue>(() => ({ payload, url }), [payload, url]);
  return (
    <RouterContext value={router}>
      <ViewContext value={view}>
        <NavigationContext value={navigation}>{children}</NavigationContext>
      </ViewContext>
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

/** An href for a plain `<a>` or attribute, under the base path: what `<Link>` does for its own. */
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
