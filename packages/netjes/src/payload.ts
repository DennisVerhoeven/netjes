import type { Metadata } from './metadata.tsx';

// The JSON contract with the .NET side (src/Netjes/Rendering/PagePayload.cs). Keep the two in sync.

export const DATA_HEADER = 'X-Netjes-Data';
export const SERVER_TIME_HEADER = 'X-Netjes-Now';
export const SEGMENTS_HEADER = 'X-Netjes-Segments';
/** `invalid` on an action's 400 when the server refused the call's antiforgery token: the action did not run. */
export const ANTIFORGERY_HEADER = 'X-Netjes-Antiforgery';
/**
 * `changed` on the response to an action call or a form post that set or deleted a cookie (signing in or out): the
 * pages the router keeps for back and forward show the app as it was before.
 */
export const COOKIES_HEADER = 'X-Netjes-Cookies';
export const DATA_ELEMENT_ID = '__NETJES_DATA__';

/** The page's route values by parameter name: a string per single segment, an array per catch-all (empty for an optional one). A single parameter the request has no value for is `null`, as when a page is rendered with `Page.Render` from an endpoint whose route lacks it. */
export type Params = Record<string, string | string[] | null>;

export type DocumentAssets = {
  stylesheets: string[];
  modulePreloads: string[];
  scripts: string[];
};

/**
 * A folder's segment files, outermost first: the client wraps the page in each segment's layout, template,
 * error boundary and loading (Suspense) fallback. `props` come from the folder's C# layout loader.
 */
export type SegmentRef = {
  id: string;
  layout?: string | undefined;
  template?: string | undefined;
  loading?: string | undefined;
  error?: string | undefined;
  props?: Record<string, unknown> | undefined;
  /** Identifies what the segment's loader saw; the client sends the keys it holds on navigations. */
  key?: string | undefined;
  /** The loader was skipped because the client holds this segment (same key); its props are the client's. */
  reused?: boolean | undefined;
  /** Parallel routes: the file each `@slot` renders for this page (its page, else its default), or null. */
  slots?: Record<string, string | null> | undefined;
  /** How many URL segments a layout's folder covers for this URL (a catch-all counts what it matched). */
  depth?: number | undefined;
  /** Each slot page's own route values (its parameter names can differ from the page's). */
  slotParams?: Record<string, Params> | undefined;
};

export type SpecialPageRef = { page: string; segments: SegmentRef[] };

/** An intercepting page that can show this URL in a slot, with its own route values. */
export type InterceptRef = { segment: string; slot: string; page: string; params?: Params | undefined };

/** Why the server rendered a boundary file instead of the route's page. */
export type PageKind = 'notFound' | 'error' | 'forbidden' | 'unauthorized';

/** CSS inlined by the dev sidecar so server-rendered pages are styled before the client script runs. */
export type DevStyle = { id: string; css: string };

export type AntiforgeryToken = {
  formFieldName: string;
  headerName: string | null;
  requestToken: string | null;
};

/** A TanStack Query cache entry seeded by the C# loader (`PageResult.WithQuery`). `updatedAt` is Unix milliseconds. */
export type SeededQuery = { key: readonly unknown[]; data: unknown; updatedAt: number };

/** Left by a server action posted without JavaScript that failed validation; the form's page renders with it. */
export type ActionStateRef = {
  action: string;
  errors: Record<string, string[]>;
  values: Record<string, string | string[]>;
};

/** What a server action call answers with (`ServerActionResult` in C#). */
export type ActionResponse = {
  kind: 'action';
  ok: boolean;
  status: number;
  result?: unknown;
  errors?: Record<string, string[]>;
  /** After a failed form call: the submitted fields the failure state may hand back (declared by the action, not private). */
  fields?: string[];
  redirect?: string;
  revalidate: boolean;
  invalidate?: unknown[][];
  queries?: SeededQuery[];
  message?: string;
};

export type PagePayload = {
  kind: 'page';
  /** The page's URL pattern, e.g. `/blog/[slug]`; null for the 404 and error pages. */
  route: string | null;
  /** Module path of the page, e.g. `app/blog/[slug]/page.tsx`; null renders the built-in 404/error page. */
  page: string | null;
  /** Set when `page` is a not-found, error, forbidden or unauthorized file rather than the route's page. */
  pageKind?: PageKind;
  segments: SegmentRef[];
  props: Record<string, unknown>;
  params: Params;
  shared: unknown;
  status: number;
  url: string;
  version: string;
  /** The server's clock (Unix ms) when it built this payload. */
  renderedAt?: number;
  antiforgery?: AntiforgeryToken;
  queries?: SeededQuery[];
  actionState?: ActionStateRef;
  /** Intercepting routes: slots that may show this page instead, on a client navigation from inside their segment. */
  intercepts?: InterceptRef[];
  /** From the C# loader (`.WithMetadata(...)`), merged over the files' `export const metadata`. */
  metadata?: Metadata;
  /** The page was rendered in draft mode. */
  draftMode?: boolean;
  /** `UsePathBase`'s base path, when the app is not mounted at the root; `url` includes it. */
  basePath?: string;
  /** The request culture, with `AddNetjesLocalization`. */
  locale?: string;
  /**
   * Client only, never sent: how far the browser clock is ahead of the server's, measured when the payload arrived.
   * Server timestamps plus this offset are browser timestamps.
   */
  clockOffset?: number;
};

/** The `<Image>` optimizer: its path and the widths it serves (Netjes:Images). */
export type ImageConfigInfo = { path: string; widths: number[]; qualities?: number[]; basePath?: string };

export type DocumentInfo = {
  lang: string;
  assets: DocumentAssets;
  errorPage: SpecialPageRef | null;
  devStyles?: DevStyle[];
  images?: ImageConfigInfo;
  /** The `Netjes:PublicConfig` configuration section. */
  publicConfig?: Record<string, unknown>;
  /** The Content-Security-Policy nonce for this document's scripts and styles. */
  nonce?: string;
  /** Where `<WebVitals />` reports to, when the server collects web vitals. */
  vitalsEndpoint?: string;
  /**
   * No sidecar was ready to render this document, so the server sent it without the page: the client entry renders
   * it instead of hydrating.
   */
  clientRender?: boolean;
};

export type InitialPayload = PagePayload & { document: DocumentInfo };

export type RedirectPayload = { kind: 'redirect'; location: string; version: string };

export type NavigationPayload = PagePayload | RedirectPayload;

export class PayloadError extends Error {
  override name = 'PayloadError';
}

type Json = Record<string, unknown>;

function fail(message: string): never {
  throw new PayloadError(`Invalid netjes payload: ${message}`);
}

function object(value: unknown, what: string): Json {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) fail(`${what} must be an object`);
  return value as Json;
}

function string(value: unknown, what: string): string {
  if (typeof value !== 'string') fail(`${what} must be a string`);
  return value;
}

function nullableString(value: unknown, what: string): string | null {
  return value === null ? null : string(value, what);
}

function number(value: unknown, what: string): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) fail(`${what} must be a number`);
  return value;
}

function strings(value: unknown, what: string): string[] {
  if (!Array.isArray(value)) fail(`${what} must be an array`);
  return value.map((item, i) => string(item, `${what}[${i}]`));
}

function params(value: unknown): Params {
  const result: Params = {};
  for (const [key, item] of Object.entries(object(value, 'params'))) {
    result[key] = Array.isArray(item) ? strings(item, `params.${key}`) : nullableString(item, `params.${key}`);
  }
  return result;
}

export function parseAntiforgery(value: unknown): AntiforgeryToken {
  return antiforgery(value);
}

function antiforgery(value: unknown): AntiforgeryToken {
  const json = object(value, 'antiforgery');
  return {
    formFieldName: string(json.formFieldName, 'antiforgery.formFieldName'),
    headerName: nullableString(json.headerName ?? null, 'antiforgery.headerName'),
    requestToken: nullableString(json.requestToken ?? null, 'antiforgery.requestToken'),
  };
}

function optionalString(value: unknown, what: string): string | undefined {
  return value === undefined || value === null ? undefined : string(value, what);
}

function segments(value: unknown, what: string): SegmentRef[] {
  if (!Array.isArray(value)) fail(`${what} must be an array`);
  return value.map((item, i) => {
    const json = object(item, `${what}[${i}]`);
    const segment: SegmentRef = { id: string(json.id, `${what}[${i}].id`) };
    for (const key of ['layout', 'template', 'loading', 'error'] as const) {
      const file = optionalString(json[key], `${what}[${i}].${key}`);
      if (file !== undefined) segment[key] = file;
    }
    if (json.props !== undefined && json.props !== null) segment.props = object(json.props, `${what}[${i}].props`);
    const key = optionalString(json.key, `${what}[${i}].key`);
    if (key !== undefined) segment.key = key;
    if (json.reused === true) segment.reused = true;
    if (json.depth !== undefined && json.depth !== null) {
      if (typeof json.depth !== 'number' || !Number.isInteger(json.depth) || json.depth < 0) {
        throw new PayloadError(`${what}[${i}].depth must be a whole number`);
      }
      segment.depth = json.depth;
    }
    if (json.slotParams !== undefined && json.slotParams !== null) {
      segment.slotParams = Object.fromEntries(
        Object.entries(object(json.slotParams, `${what}[${i}].slotParams`)).map(([name, values]) => [name, params(values)]),
      );
    }
    if (json.slots !== undefined && json.slots !== null) {
      const slots: Record<string, string | null> = {};
      for (const [name, file] of Object.entries(object(json.slots, `${what}[${i}].slots`))) {
        slots[name] = nullableString(file, `${what}[${i}].slots.${name}`);
      }
      segment.slots = slots;
    }
    return segment;
  });
}

function errorMap(value: unknown, what: string): Record<string, string[]> {
  const result: Record<string, string[]> = {};
  for (const [key, messages] of Object.entries(object(value, what))) result[key] = strings(messages, `${what}.${key}`);
  return result;
}

function actionState(value: unknown): ActionStateRef {
  const json = object(value, 'actionState');
  const values: Record<string, string | string[]> = {};
  for (const [key, item] of Object.entries(object(json.values ?? {}, 'actionState.values'))) {
    values[key] = Array.isArray(item) ? strings(item, `actionState.values.${key}`) : string(item, `actionState.values.${key}`);
  }
  return { action: string(json.action, 'actionState.action'), errors: errorMap(json.errors ?? {}, 'actionState.errors'), values };
}

export function parseActionResponse(value: unknown): ActionResponse {
  const json = object(value, 'action response');
  if (json.kind !== 'action') fail(`expected an action response, got kind ${JSON.stringify(json.kind)}`);
  const response: ActionResponse = {
    kind: 'action',
    ok: json.ok === true,
    status: number(json.status, 'status'),
    revalidate: json.revalidate === true,
  };
  if ('result' in json) response.result = json.result;
  if (json.errors !== undefined && json.errors !== null) response.errors = errorMap(json.errors, 'errors');
  if (json.fields !== undefined && json.fields !== null) response.fields = strings(json.fields, 'fields');
  if (json.redirect !== undefined && json.redirect !== null) response.redirect = string(json.redirect, 'redirect');
  if (json.invalidate !== undefined && json.invalidate !== null) {
    if (!Array.isArray(json.invalidate) || !json.invalidate.every(Array.isArray)) fail('invalidate must be an array of query keys');
    response.invalidate = json.invalidate as unknown[][];
  }
  if (json.queries !== undefined && json.queries !== null) response.queries = queries(json.queries);
  if (typeof json.message === 'string') response.message = json.message;
  return response;
}

const PAGE_KINDS: readonly PageKind[] = ['notFound', 'error', 'forbidden', 'unauthorized'];

function queries(value: unknown): SeededQuery[] {
  if (!Array.isArray(value)) fail('queries must be an array');
  return value.map((item, i) => {
    const query = object(item, `queries[${i}]`);
    if (!Array.isArray(query.key) || query.key.length === 0) fail(`queries[${i}].key must be a non-empty array`);
    return { key: query.key, data: query.data ?? null, updatedAt: number(query.updatedAt, `queries[${i}].updatedAt`) };
  });
}

function pagePayload(json: Json): PagePayload {
  const payload: PagePayload = {
    kind: 'page',
    route: nullableString(json.route ?? null, 'route'),
    page: nullableString(json.page ?? null, 'page'),
    segments: segments(json.segments, 'segments'),
    props: object(json.props, 'props'),
    params: params(json.params),
    shared: json.shared ?? null,
    status: number(json.status, 'status'),
    url: string(json.url, 'url'),
    version: string(json.version, 'version'),
  };
  if (json.antiforgery !== undefined && json.antiforgery !== null) payload.antiforgery = antiforgery(json.antiforgery);
  if (json.queries !== undefined && json.queries !== null) payload.queries = queries(json.queries);
  if (json.actionState !== undefined && json.actionState !== null) payload.actionState = actionState(json.actionState);
  if (Array.isArray(json.intercepts)) {
    payload.intercepts = json.intercepts.map((item, i) => {
      const intercept = object(item, `intercepts[${i}]`);
      return {
        segment: string(intercept.segment, `intercepts[${i}].segment`),
        slot: string(intercept.slot, `intercepts[${i}].slot`),
        page: string(intercept.page, `intercepts[${i}].page`),
        ...(intercept.params !== undefined && intercept.params !== null ? { params: params(intercept.params) } : {}),
      };
    });
  }
  if (json.metadata !== undefined && json.metadata !== null) payload.metadata = object(json.metadata, 'metadata') as Metadata;
  if (json.pageKind !== undefined && json.pageKind !== null) {
    if (!PAGE_KINDS.includes(json.pageKind as PageKind)) fail(`unknown pageKind ${JSON.stringify(json.pageKind)}`);
    payload.pageKind = json.pageKind as PageKind;
  }
  if (json.renderedAt !== undefined && json.renderedAt !== null) payload.renderedAt = number(json.renderedAt, 'renderedAt');
  if (json.basePath !== undefined && json.basePath !== null) payload.basePath = string(json.basePath, 'basePath');
  if (json.locale !== undefined && json.locale !== null) payload.locale = string(json.locale, 'locale');
  if (json.draftMode !== undefined && json.draftMode !== null) {
    if (typeof json.draftMode !== 'boolean') fail('draftMode must be a boolean');
    payload.draftMode = json.draftMode;
  }
  return payload;
}

export function parseNavigationPayload(value: unknown): NavigationPayload {
  const json = object(value, 'payload');
  switch (json.kind) {
    case 'page':
      return pagePayload(json);
    case 'redirect':
      return { kind: 'redirect', location: string(json.location, 'location'), version: string(json.version, 'version') };
    default:
      return fail(`unknown kind ${JSON.stringify(json.kind)}`);
  }
}

export function parseInitialPayload(value: unknown): InitialPayload {
  const json = object(value, 'payload');
  if (json.kind !== 'page') fail('the initial payload must be a page');
  const document = object(json.document, 'document');
  const assets = object(document.assets, 'document.assets');
  const errorPage = document.errorPage === null || document.errorPage === undefined
    ? null
    : object(document.errorPage, 'document.errorPage');
  const info: DocumentInfo = {
    lang: string(document.lang, 'document.lang'),
    assets: {
      stylesheets: strings(assets.stylesheets, 'document.assets.stylesheets'),
      modulePreloads: strings(assets.modulePreloads, 'document.assets.modulePreloads'),
      scripts: strings(assets.scripts, 'document.assets.scripts'),
    },
    errorPage: errorPage && {
      page: string(errorPage.page, 'document.errorPage.page'),
      segments: segments(errorPage.segments, 'document.errorPage.segments'),
    },
  };
  if (Array.isArray(document.devStyles)) {
    info.devStyles = document.devStyles.map((style, i) => {
      const item = object(style, `document.devStyles[${i}]`);
      return { id: string(item.id, 'devStyles.id'), css: string(item.css, 'devStyles.css') };
    });
  }
  if (document.images !== undefined && document.images !== null) {
    const images = object(document.images, 'document.images');
    if (!Array.isArray(images.widths)) fail('document.images.widths must be an array');
    info.images = {
      path: string(images.path, 'document.images.path'),
      widths: images.widths.map((w, i) => number(w, `document.images.widths[${i}]`)),
      ...(typeof images.basePath === 'string' && images.basePath ? { basePath: images.basePath } : {}),
      ...(Array.isArray(images.qualities)
        ? { qualities: images.qualities.map((q, i) => number(q, `document.images.qualities[${i}]`)) }
        : {}),
    };
  }
  if (document.nonce !== undefined && document.nonce !== null) info.nonce = string(document.nonce, 'document.nonce');
  if (document.vitalsEndpoint !== undefined && document.vitalsEndpoint !== null) {
    info.vitalsEndpoint = string(document.vitalsEndpoint, 'document.vitalsEndpoint');
  }
  if (document.publicConfig !== undefined && document.publicConfig !== null) {
    info.publicConfig = object(document.publicConfig, 'document.publicConfig');
  }
  if (document.clientRender !== undefined && document.clientRender !== null) {
    if (typeof document.clientRender !== 'boolean') fail('document.clientRender must be a boolean');
    if (document.clientRender) info.clientRender = true;
  }
  return { ...pagePayload(json), document: info };
}
