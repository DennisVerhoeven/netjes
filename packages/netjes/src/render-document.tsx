// renderDocument and inlineScriptHashes, which netjes/testing exports: a page's whole document, rendered from its
// props as the Node sidecar renders it for a crawler, and the hashes of the inline scripts in it. The server builds
// the payload the sidecar renders; documentPayload builds one here from the pages map and the test's options, by the
// server's rules for the route and the segments (RouteTable and PageRenderer in src/Netjes). The two are checked
// against the same cases, test/fixtures/segments/cases.json, so a change to either rule fails a suite until both agree.
import { htmlSafeJson } from './deferred.ts';
import type { InitialPayload, PageKind, Params, SegmentRef } from './payload.ts';

/** What {@link renderDocument} renders a page with: what the server would send with it. */
export type RenderDocumentOptions = {
  /** The page's props, as its C# loader sends them. A Promise anywhere in them is a `Page.Defer` value. */
  props?: Record<string, unknown>;
  /** The route values, as `useParams()` returns them: a string per segment, an array per catch-all. Default none. */
  params?: Params;
  /** The page's URL, its path and query. Default `/`. */
  url?: string;
  /**
   * The props of each layout's C# layout loader, by the layout's file, such as `app/blog/layout.tsx`. A Promise in
   * them is a `Page.Defer` value. A layout that is not named here gets none.
   */
  layouts?: Record<string, Record<string, unknown>>;
  /** What the server sends as the shared props (`UseSharedProps`). Default `null`. */
  shared?: unknown;
  /**
   * The page's status. Default 200, or the status the server gives the file: 404 for a not-found file, 403 for a
   * forbidden one, 401 for an unauthorized one and 500 for an error or global-error one.
   */
  status?: number;
  /** A Content-Security-Policy nonce, which goes on every script and stylesheet, as for a policy with `{nonce}`. */
  nonce?: string;
  /** The `lang` of `<html>`. Default `en`, as `NetjesOptions.Lang`. */
  lang?: string;
  /** What `usePublicConfig()` reads, as the `Netjes:PublicConfig` section. */
  publicConfig?: Record<string, unknown>;
  /**
   * The page packages the app mounts, by npm name, each at its prefix as `MapPagePackage` takes it:
   * `{ '@acme/help-center': '/(marketing)/help' }`. Their files in the pages map go into the app's folders under the
   * prefix, so an app page in a package's folder renders inside the package's layouts, and the payload's `mounts` gives
   * each one's URL for the page's `params`. A package that `netjes({ packages })` does not list has its pages in `app`.
   */
  mounts?: Record<string, string>;
  /**
   * The page files, keyed `/app/...` as `import.meta.glob` keys them, and a page package's `/<npm name>/<its pages
   * folder>/...`. Default: the app's and the listed packages', from the `netjes()` plugin.
   */
  modules?: Record<string, () => Promise<unknown>>;
  /** Stops the render, which then rejects with the signal's reason: for a page that waits on a Promise that never settles. */
  signal?: AbortSignal;
};

/** The options {@link documentPayload} reads, with any Promise in the props already a placeholder. */
type DocumentOptions = Omit<RenderDocumentOptions, 'modules' | 'signal'>;

/** The files renderDocument renders besides pages, with the kind and the status the server gives them. */
const BOUNDARIES = new Map<string, { kind: PageKind; status: number }>([
  ['not-found', { kind: 'notFound', status: 404 }],
  ['forbidden', { kind: 'forbidden', status: 403 }],
  ['unauthorized', { kind: 'unauthorized', status: 401 }],
  ['error', { kind: 'error', status: 500 }],
  ['global-error', { kind: 'error', status: 500 }],
]);

/** The files that make a folder a segment of the pages below it; the payload names the first four. */
const SEGMENT_FILES = ['layout', 'template', 'loading', 'error', 'not-found', 'forbidden', 'unauthorized'];

const PAGE_EXTENSION = /\.(?:tsx|jsx)$/i;
const isGroup = (folder: string) => folder.length > 2 && folder.startsWith('(') && folder.endsWith(')');
const isSlot = (folder: string) => folder.length > 1 && folder.startsWith('@');
const isPrivate = (folder: string) => folder.startsWith('_');
/** UTF-16 code unit order, as the server's ordinal order. */
const ordinal = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);

/** "a", "a and b", "a, b and c". */
function list(items: readonly string[]): string {
  return items.length < 2 ? (items[0] ?? '') : `${items.slice(0, -1).join(', ')} and ${items.at(-1) ?? ''}`;
}

/** A page package mounted at a prefix: the prefix's folders below the app directory (`''` for `/`), and its pages folder. */
type Mount = { name: string; folder: string; pages: string };

/**
 * A file of the pages map the server routes: its id, its path in the app directory (a package's under its mount), and
 * its source, `app` or the package's name.
 */
type RoutedFile = { id: string; path: string; source: string };

const PREFIX_SEGMENT = /^(?:[\p{L}\p{N}._~-]+|\[[A-Za-z_][A-Za-z0-9_]*\]|\([\p{L}\p{N}_-]+\))$/u;

/** The mounts of `options.mounts`, with their pages folders from the plugin's list; throws for a prefix MapPagePackage refuses. */
function mountsOf(mounts: Readonly<Record<string, string>> | undefined, packages: readonly { name: string; pages: string }[]): Mount[] {
  return Object.entries(mounts ?? {}).map(([name, prefix]) => {
    const folders = prefix === '/' ? [] : prefix.split('/').slice(1);
    const refused = (folder: string) => folder === '.' || folder === '..' || isPrivate(folder) || !PREFIX_SEGMENT.test(folder);
    // A parameter used twice, in any case, as MapPagePackage refuses it.
    const parameters = folders.filter((folder) => folder.startsWith('[')).map((folder) => folder.toLowerCase());
    if (!prefix.startsWith('/') || folders.some(refused) || new Set(parameters).size < parameters.length) {
      throw new Error(
        `netjes: renderDocument cannot mount '${name}' at '${prefix}': write the prefix as MapPagePackage takes it, ` +
          "such as '/help', '/(marketing)/help' or '/[tenant]/help'.",
      );
    }
    return { name, folder: folders.join('/'), pages: packages.find((p) => p.name === name)?.pages ?? 'app' };
  });
}

/** The files the server routes: the app's where they are, and a mounted package's under its prefix's folders. */
function routedFiles(files: readonly string[], appDir: string, mounts: readonly Mount[]): RoutedFile[] {
  return files.flatMap((id): RoutedFile[] => {
    if (id.startsWith(`${appDir}/`)) return [{ id, path: id, source: 'app' }];
    const mount = mounts.find((m) => id.startsWith(`${m.name}/${m.pages}/`));
    if (!mount) return [];
    const below = id.slice(mount.name.length + mount.pages.length + 2);
    return [{ id, path: [appDir, mount.folder, below].filter(Boolean).join('/'), source: mount.name }];
  });
}

/**
 * Each mount's URL for these route values, as the server sends it: the literal segments as written and each value
 * encoded as one segment, or null when a value is missing, a list, empty, `.` or `..`.
 */
function mountUrls(mounts: readonly Mount[], params: Params): Record<string, string | null> {
  // As the server encodes a value (Uri.EscapeDataString), which encodeURIComponent leaves these five unencoded.
  const encode = (value: string) => encodeURIComponent(value).replace(/[!'()*]/g, (c) => `%${c.charCodeAt(0).toString(16).toUpperCase()}`);
  return Object.fromEntries(
    mounts.map((mount) => {
      const segments: string[] = [];
      for (const folder of mount.folder.split('/').filter((f) => f && !isGroup(f))) {
        const value = folder.startsWith('[') ? params[folder.slice(1, -1)] : folder;
        if (typeof value !== 'string' || value === '' || value === '.' || value === '..') return [mount.name, null];
        segments.push(folder.startsWith('[') ? encode(value) : value);
      }
      return [mount.name, `/${segments.join('/')}`];
    }),
  );
}

/** A file in the app directory: the folders below the directory and the file's name without its extension; null for any other file. */
function parse(file: string, appDir: string): { folders: string[]; name: string } | null {
  if (!file.startsWith(`${appDir}/`) || !PAGE_EXTENSION.test(file)) return null;
  const folders = file.slice(appDir.length + 1).split('/');
  const name = (folders.pop() ?? '').replace(PAGE_EXTENSION, '');
  return { folders, name };
}

type AppFiles = {
  /** Each folder's convention files by name, by its path below the app directory (`''` for the directory itself). */
  folders: Map<string, Map<string, RoutedFile>>;
  /** The folders with parallel route slots, each with the first of its slots. */
  slots: Map<string, string>;
};

/** The convention files the server routes: none in private folders, and the slots that pages and defaults make. */
function appFiles(files: readonly RoutedFile[], appDir: string): AppFiles {
  const folders = new Map<string, Map<string, RoutedFile>>();
  const slots = new Map<string, string>();
  for (const file of [...files].sort((a, b) => ordinal(a.id, b.id))) {
    const parsed = parse(file.path, appDir);
    if (!parsed || parsed.folders.some(isPrivate)) continue;
    const slot = parsed.folders.findIndex(isSlot);
    if (slot >= 0) {
      const parent = parsed.folders.slice(0, slot).join('/');
      if ((parsed.name === 'page' || parsed.name === 'default') && !slots.has(parent)) {
        slots.set(parent, [appDir, ...parsed.folders.slice(0, slot + 1)].join('/'));
      }
      continue;
    }
    const folder = parsed.folders.join('/');
    let conventions = folders.get(folder);
    if (!conventions) folders.set(folder, (conventions = new Map()));
    const other = conventions.get(parsed.name);
    // As the server, which refuses two sources' segment files in one folder.
    if (other && other.source !== file.source && parsed.name !== 'page') {
      throw new Error(`netjes: renderDocument has two ${parsed.name} files for ${[appDir, folder].filter(Boolean).join('/')}: ${other.id} and ${file.id}.`);
    }
    if (!other) conventions.set(parsed.name, file);
  }
  return { folders, slots };
}

/**
 * How many URL segments a layout's folders cover for these route values, as the server counts them: one per folder,
 * none for a route group, and a catch-all as many as its value has.
 */
function urlDepth(folders: readonly string[], params: Params): number {
  let depth = 0;
  for (const folder of folders.filter((name) => !isGroup(name))) {
    if (!folder.startsWith('[...') && !folder.startsWith('[[...')) {
      depth++;
      continue;
    }
    const value = params[folder.replace(/^[[\]]+|[[\]]+$/g, '').slice(3)];
    depth += (Array.isArray(value) ? value.join('/') : (value ?? '')).split('/').filter(Boolean).length;
  }
  return depth;
}

function slotError(page: string, where: string, slot: string): Error {
  return new Error(
    `netjes: renderDocument cannot render ${page}, which ${where} the parallel route slot ${slot}: the server chooses what a slot renders.`,
  );
}

/**
 * The folders above `folders` and the folder itself that are segments, outermost first, as the server sends them:
 * each folder with a segment file, its layout's props from `layouts` and, for a layout, the URL depth it covers.
 */
function segmentsFor(page: string, app: AppFiles, folders: readonly string[], appDir: string, options: DocumentOptions): SegmentRef[] {
  const segments: SegmentRef[] = [];
  for (let end = 0; end <= folders.length; end++) {
    const path = folders.slice(0, end);
    const folder = path.join('/');
    const slot = app.slots.get(folder);
    if (slot) throw slotError(page, 'renders under', slot);
    const conventions = app.folders.get(folder);
    if (!conventions || !SEGMENT_FILES.some((name) => conventions.has(name))) continue;
    const segment: SegmentRef = { id: folder ? `${appDir}/${folder}` : appDir };
    for (const name of ['layout', 'template', 'loading', 'error'] as const) {
      const file = conventions.get(name)?.id;
      if (file) segment[name] = file;
    }
    if (segment.layout) {
      if (options.layouts && Object.hasOwn(options.layouts, segment.layout)) segment.props = options.layouts[segment.layout];
      segment.depth = urlDepth(path, options.params ?? {});
    }
    segments.push(segment);
  }
  return segments;
}

/** Every file renderDocument could render from these: the pages and the boundary files, in ordinal order. */
function renderable(files: readonly RoutedFile[], appDir: string): string[] {
  return files
    .filter((file) => {
      const parsed = parse(file.path, appDir);
      if (!parsed || parsed.folders.some((folder) => isPrivate(folder) || isSlot(folder))) return false;
      return parsed.name === 'page' || (BOUNDARIES.has(parsed.name) && (parsed.name !== 'global-error' || isRootGlobalError(file, parsed)));
    })
    .map(({ id }) => id)
    .sort(ordinal);
}

/** The global error page is the app's own, in the app directory itself; the server refuses one in a package. */
const isRootGlobalError = (file: RoutedFile, parsed: { folders: string[] }) => file.source === 'app' && parsed.folders.length === 0;

/**
 * The payload the server sends with the document of `page`, a file of the pages map named without its leading slash,
 * for the values in `options`: the route, kind, status and segments by the server's rules, and a build with no
 * stylesheets or scripts. The files of the page packages `options.mounts` names go under their prefixes, with their
 * pages folders from `packages`. Throws for a file it cannot render: one not in `files`, a layout, a page in a private
 * folder or under a parallel route slot, or a file of a package it does not mount.
 */
export function documentPayload(
  page: string,
  options: DocumentOptions,
  files: readonly string[],
  appDir: string,
  packages: readonly { name: string; pages: string }[] = [],
): InitialPayload {
  const paths = files.map((file) => file.replace(/^\//, ''));
  const mounts = mountsOf(options.mounts, packages);
  const routed = routedFiles(paths, appDir, mounts);
  if (!paths.includes(page)) {
    const hint = page.startsWith('/') && paths.includes(page.slice(1))
      ? `; name the file without the leading slash: ${page.slice(1)}.`
      : `. The files it can render are ${list(renderable(routed, appDir))}.`;
    throw new Error(`netjes: renderDocument has no ${page} in the pages map${hint}`);
  }
  const file = routed.find((routedFile) => routedFile.id === page);
  const parsed = file && parse(file.path, appDir);
  if (!parsed) {
    const unmounted = packages.find((p) => page.startsWith(`${p.name}/`))?.name;
    throw new Error(
      unmounted
        ? `netjes: renderDocument cannot render ${page}, a file of the page package '${unmounted}', which it does not mount: ` +
            `pass its prefix as MapPagePackage takes it, as in mounts: { '${unmounted}': '/help' }.`
        : `netjes: renderDocument cannot render ${page}, which is not in the app's directory, ${appDir}, or in a mounted page package's pages.`,
    );
  }
  if (parsed.folders.some(isPrivate)) throw new Error(`netjes: renderDocument cannot render ${page}: a private folder is not routed.`);
  const slot = parsed.folders.findIndex(isSlot);
  if (slot >= 0) throw slotError(page, 'is in', [appDir, ...parsed.folders.slice(0, slot + 1)].join('/'));
  const boundary = BOUNDARIES.get(parsed.name);
  if ((parsed.name !== 'page' && !boundary) || (parsed.name === 'global-error' && !isRootGlobalError(file, parsed))) {
    throw new Error(
      `netjes: renderDocument renders a page or a not-found, error, forbidden, unauthorized or global-error file, not ${page}.`,
    );
  }

  // The global error page renders without any layout, the others inside the segments down to their own folder.
  const segments = parsed.name === 'global-error' ? [] : segmentsFor(page, appFiles(routed, appDir), parsed.folders, appDir, options);
  for (const file of Object.keys(options.layouts ?? {})) {
    if (!segments.some((segment) => segment.layout === file)) {
      const layouts = segments.flatMap((segment) => (segment.layout ? [segment.layout] : []));
      throw new Error(
        `netjes: renderDocument has props for ${file}, which is not a layout of ${page}. ` +
          (layouts.length > 0 ? `Its layouts are ${list(layouts)}.` : 'It has no layouts.'),
      );
    }
  }
  // An error boundary renders in place of the pages below its folder, so it is not wrapped in itself.
  for (const segment of segments) if (segment.error === page) delete segment.error;

  return {
    kind: 'page',
    route: parsed.name === 'page' ? `/${parsed.folders.filter((folder) => !isGroup(folder)).join('/')}` : null,
    page,
    ...(boundary ? { pageKind: boundary.kind } : {}),
    segments,
    props: options.props ?? {},
    params: options.params ?? {},
    shared: options.shared ?? null,
    status: options.status ?? boundary?.status ?? 200,
    url: options.url ?? '/',
    version: 'test',
    ...(mounts.length > 0 ? { mounts: mountUrls(mounts, options.params ?? {}) } : {}),
    document: {
      lang: options.lang ?? 'en',
      assets: { stylesheets: [], modulePreloads: [], scripts: [] },
      errorPage: null,
      ...(options.publicConfig ? { publicConfig: options.publicConfig } : {}),
      ...(options.nonce ? { nonce: options.nonce } : {}),
    },
  };
}

function isThenable(value: unknown): value is PromiseLike<unknown> {
  if ((typeof value !== 'object' && typeof value !== 'function') || value === null) return false;
  return typeof (value as { then?: unknown }).then === 'function';
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  if (typeof value !== 'object' || value === null) return false;
  const prototype: unknown = Object.getPrototypeOf(value);
  return prototype === Object.prototype || prototype === null;
}

/** A JSON value with each Promise in it, at any depth, replaced by a `Page.Defer` placeholder; `values` keeps the Promises by id. */
function withPlaceholders(value: unknown, values: Map<string, PromiseLike<unknown>>): unknown {
  if (isThenable(value)) {
    const id = `d${values.size}`;
    values.set(id, value);
    // Read once the document renders; until then a rejection is not an unhandled one.
    Promise.resolve(value).catch(() => {});
    return { $netjes: 'deferred', id };
  }
  if (Array.isArray(value)) return value.map((item: unknown) => withPlaceholders(item, values));
  return isPlainObject(value) ? placeholdersIn(value, values) : value;
}

function placeholdersIn(props: Record<string, unknown>, values: Map<string, PromiseLike<unknown>>): Record<string, unknown> {
  // Keys are defined, not assigned, so a "__proto__" key stays a key.
  return Object.fromEntries(Object.entries(props).map(([key, item]) => [key, withPlaceholders(item, values)]));
}

async function text(stream: AsyncIterable<unknown>): Promise<string> {
  const decoder = new TextDecoder();
  let html = '';
  for await (const chunk of stream) {
    html += chunk instanceof Uint8Array ? decoder.decode(chunk, { stream: true }) : decoder.decode() + String(chunk);
  }
  return html + decoder.decode();
}

/**
 * Renders a page's whole document, from `<!DOCTYPE html>` to `</html>`, as the Node sidecar renders it for a crawler:
 * the page from `page`, a file of the pages map such as `app/blog/[slug]/page.tsx`, inside the layouts the server
 * would wrap it in, with the head, Netjes's inline scripts and the payload. It waits for every Suspense boundary, and
 * a Promise in the props or in a layout's props is a `Page.Defer` value: rendered in place once it resolves, left as its
 * Suspense fallback when it rejects. The build's stylesheets and scripts, development styles and an antiforgery token
 * are left out, and no C# runs: the props come from `options`. Runs in Vitest's node environment, with the `netjes()`
 * plugin, which provides the pages map, the app's folder and the query shell, also when `modules` is given.
 */
export async function renderDocument(page: string, options: RenderDocumentOptions = {}): Promise<string> {
  const { modules, signal, props, layouts, ...rest } = options;
  // Before the first await, so that a Promise that has failed already gets its handler before Node looks for one.
  const values = new Map<string, PromiseLike<unknown>>();
  const sent: DocumentOptions = {
    ...rest,
    props: placeholdersIn(props ?? {}, values),
    ...(layouts
      ? { layouts: Object.fromEntries(Object.entries(layouts).map(([file, layoutProps]) => [file, placeholdersIn(layoutProps, values)])) }
      : {}),
  };
  // A form's server action, the query client and the router's first URL take their browser path where these exist.
  if (typeof window !== 'undefined' || typeof document !== 'undefined') {
    throw new Error(
      'netjes: renderDocument renders a page as the Node sidecar does, without a DOM. ' +
        "Put '// @vitest-environment node' at the top of the test file.",
    );
  }
  signal?.throwIfAborted();
  const app = await import('virtual:netjes/pages');
  const pages = modules ?? app.pages;
  const payload = documentPayload(page, sent, Object.keys(pages), app.appDir, app.packages);
  // Loaded here, so that component tests, which import netjes/testing too, never load react-dom/server.
  const { createServerRenderer } = await import('./server-render.tsx');
  const stream = await createServerRenderer(pages).renderToStream(htmlSafeJson(payload), {
    waitForAll: true,
    deferred: (id) => values.get(id) ?? new Promise<never>(() => {}),
    ...(signal ? { signal } : {}),
  });
  return text(stream);
}

/** JavaScript MIME type essences: a script element whose type is one of them, in any case, runs as a classic script. */
const JAVASCRIPT_TYPES = new Set([
  'application/ecmascript',
  'application/javascript',
  'application/x-ecmascript',
  'application/x-javascript',
  'text/ecmascript',
  'text/javascript',
  'text/javascript1.0',
  'text/javascript1.1',
  'text/javascript1.2',
  'text/javascript1.3',
  'text/javascript1.4',
  'text/javascript1.5',
  'text/jscript',
  'text/livescript',
  'text/x-ecmascript',
  'text/x-javascript',
]);

/** The other script types whose inline text a browser checks against a policy's script sources. */
const OTHER_SCRIPT_TYPES = new Set(['module', 'importmap', 'speculationrules']);

/** A script element: its start tag's attributes (quoted values can hold a `>`) and its text, up to `</script`. */
const SCRIPT = /<script(?=[\s/>])((?:"[^"]*"|'[^']*'|[^'">])*)>([\s\S]*?)<\/script(?=[\s/>])[^>]*>/gi;
const ATTRIBUTE = /([^\s"'>/=]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'=<>`]+)))?/g;

const asciiLowerCase = (text: string) => text.replace(/[A-Z]/g, (letter) => letter.toLowerCase());

function attributesOf(source: string): Map<string, string> {
  const attributes = new Map<string, string>();
  for (const [, name = '', double, single, bare] of source.matchAll(ATTRIBUTE)) {
    const key = asciiLowerCase(name);
    // As in HTML, the first of two attributes with one name counts.
    if (!attributes.has(key)) attributes.set(key, double ?? single ?? bare ?? '');
  }
  return attributes;
}

/** Whether a browser runs an inline script element with these attributes, by HTML's rules for its type. */
function runs(attributes: Map<string, string>): boolean {
  const type = attributes.get('type');
  const language = attributes.get('language');
  const typeString =
    type === '' || (type === undefined && !language)
      ? 'text/javascript'
      : type === undefined
        ? `text/${language ?? ''}`
        : type.replace(/^[\t\n\f\r ]+|[\t\n\f\r ]+$/g, '');
  const kind = asciiLowerCase(typeString);
  if (JAVASCRIPT_TYPES.has(kind)) return !attributes.has('nomodule');
  return OTHER_SCRIPT_TYPES.has(kind);
}

async function hashSource(script: string): Promise<string> {
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(script)));
  let bytes = '';
  for (const byte of digest) bytes += String.fromCharCode(byte);
  return `'sha256-${btoa(bytes)}'`;
}

/**
 * The hash-sources, `'sha256-…'` in their quotes, of the inline scripts in `html` that a browser runs and checks
 * against a Content-Security-Policy's script sources: each script element without `src` whose type a browser runs
 * (none, a JavaScript type, `module`, `importmap` or `speculationrules`), with text. Each hash is listed once, in the
 * order of the first script with it. Data blocks such as the page payload (`application/json`) are left out.
 */
export async function inlineScriptHashes(html: string): Promise<string[]> {
  const scripts: string[] = [];
  for (const [, tag = '', body = ''] of html.matchAll(SCRIPT)) {
    const attributes = attributesOf(tag);
    if (attributes.has('src') || !runs(attributes)) continue;
    // The HTML parser turns each CR LF, and each CR on its own, into LF before the script sees its text.
    const script = body.replace(/\r\n?/g, '\n');
    // A browser checks an empty script against no policy, since it does not run it.
    if (script !== '') scripts.push(script);
  }
  return [...new Set(await Promise.all(scripts.map(hashSource)))];
}
