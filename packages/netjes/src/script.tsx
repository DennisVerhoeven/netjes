import { type ScriptHTMLAttributes, useEffect, useRef, useSyncExternalStore } from 'react';
import { preload } from 'react-dom';
import { htmlSafeJson } from './deferred.ts';
import { useNonce } from './nonce.tsx';

/** When `<Script>` loads its script: before or after the page is interactive, or when the browser is idle. */
export type ScriptStrategy = 'beforeInteractive' | 'afterInteractive' | 'lazyOnload';

/** The props of `<Script>`: a script's own, with a loading strategy and load callbacks. */
export type ScriptProps = Omit<ScriptHTMLAttributes<HTMLScriptElement>, 'onLoad' | 'onError' | 'children' | 'dangerouslySetInnerHTML'> & {
  /** Identifies the script (required for inline scripts): each is added once, however often it is rendered. */
  id?: string;
  /**
   * `afterInteractive` (default): once the page is interactive. `lazyOnload`: when the browser is idle.
   * `beforeInteractive`: in a server-rendered document, inline code runs where it stands, as the parser reaches it,
   * and an external script runs once the shell is parsed, in order with the others, before the page's code and
   * hydration. A component that mounts in the browser, after a client navigation for one, adds it as
   * `afterInteractive` does.
   */
  strategy?: ScriptStrategy;
  onLoad?: (event: Event) => void;
  onError?: (event: Event) => void;
  /** Inline code, instead of `src`. */
  children?: string;
};

/**
 * The name of the `<meta>` a server-rendered `beforeInteractive` script puts in the head, which tells the client entry
 * to run the external ones before it imports the page's modules.
 */
export const BEFORE_INTERACTIVE_MARKER = 'netjes-before-interactive';

/** The attribute that marks a server-rendered `beforeInteractive` script, inline or inert, with its id or src. */
const ENTRY = 'data-netjes-script';

type Entry = { element: HTMLScriptElement; state: 'loading' | 'loaded' | 'failed'; event?: Event };

/** Scripts already added to this document, or run by its parser, by id or src. */
const added = new Map<string, Entry>();

function whenIdle(run: () => void) {
  const schedule = () =>
    typeof window.requestIdleCallback === 'function' ? window.requestIdleCallback(run) : window.setTimeout(run, 1);
  if (document.readyState === 'complete') schedule();
  else window.addEventListener('load', schedule, { once: true });
}

/** A script's own attributes by their HTML names: strings, or true for one without a value. */
type Attributes = Record<string, string | true>;

/** A name `setAttribute` takes, other than an event handler's, which React never renders from a string either. */
const isAttributeName = (name: string) => /^[A-Za-z_:][-\w.:]*$/.test(name) && !/^on/i.test(name);

function attributesOf(props: Record<string, unknown>): Attributes {
  const attributes: Attributes = {};
  for (const [name, value] of Object.entries(props)) {
    if (value === undefined || value === null || value === false || typeof value === 'function' || typeof value === 'object') continue;
    const html = name === 'className' ? 'class' : name === 'crossOrigin' ? 'crossorigin' : name;
    if (isAttributeName(html)) attributes[html] = value === true ? true : String(value);
  }
  return attributes;
}

type Insertion = { src?: string; code?: string; id?: string; nonce?: string; attributes: Attributes; async: boolean; parent: HTMLElement };

/** Adds a script element to the document and records it, with its outcome once it has one. */
function insert(key: string, { src, code, id, nonce, attributes, async, parent }: Insertion): Entry {
  const element = document.createElement('script');
  for (const [name, value] of Object.entries(attributes)) element.setAttribute(name, value === true ? '' : value);
  if (id) element.id = id;
  // The property, not the attribute: browsers hide nonce attributes once a policy applies.
  if (nonce) element.nonce = nonce;
  const entry: Entry = { element, state: 'loading' };
  element.addEventListener('load', (event) => Object.assign(entry, { state: 'loaded', event }));
  element.addEventListener('error', (event) => Object.assign(entry, { state: 'failed', event }));
  if (src) {
    element.src = src;
    element.async = async;
  } else {
    element.textContent = code ?? '';
    // Inline scripts run as they are appended; there is no load event to wait for.
    entry.state = 'loaded';
  }
  added.set(key, entry);
  parent.append(element);
  return entry;
}

function add(key: string, { src, children, strategy: _strategy, id, onLoad: _onLoad, onError: _onError, ...rest }: ScriptProps): Entry {
  return insert(key, {
    ...(src ? { src } : { code: children ?? '' }),
    ...(id ? { id } : {}),
    ...(rest.nonce ? { nonce: rest.nonce } : {}),
    attributes: attributesOf(rest),
    async: true,
    parent: document.body,
  });
}

/** What an inert entry holds, the script's src and its attributes, or null when it holds anything else. */
function externalEntry(text: string | null): { src: string; attributes: Attributes } | null {
  let value: unknown;
  try {
    value = JSON.parse(text ?? '');
  } catch {
    return null;
  }
  if (typeof value !== 'object' || value === null) return null;
  const { src, attributes } = value as { src?: unknown; attributes?: unknown };
  if (typeof src !== 'string' || src === '') return null;
  const checked: Attributes = {};
  if (typeof attributes === 'object' && attributes !== null) {
    for (const [name, attribute] of Object.entries(attributes)) {
      if ((typeof attribute === 'string' || attribute === true) && isAttributeName(name) && name.toLowerCase() !== 'nonce') checked[name] = attribute;
    }
  }
  return { src, attributes: checked };
}

/** The types besides none that browsers run as JavaScript: the HTML standard's JavaScript MIME types. */
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

/**
 * Whether a browser runs a script with these attributes, by the HTML standard's rules, and so fires its load or error
 * event. One that runs the client entry, a module, skips a script with `nomodule` and one whose type is neither
 * JavaScript nor `module`, such as the `text/plain` of a script a consent manager turns on once the visitor agrees, and
 * fires neither event for it.
 */
function runs(attributes: Attributes): boolean {
  const named: Partial<Record<string, string>> = {};
  for (const [name, value] of Object.entries(attributes)) named[name.toLowerCase()] = value === true ? '' : value;
  const type = named.type ?? (named.language ? `text/${named.language}` : '');
  if (type === '') return named.nomodule === undefined;
  const essence = type.replace(/^[\t\n\f\r ]+|[\t\n\f\r ]+$/g, '').toLowerCase();
  return essence === 'module' || (JAVASCRIPT_TYPES.has(essence) && named.nomodule === undefined);
}

/** Resolves once the script has loaded or failed. */
function outcome(entry: Entry): Promise<void> {
  if (entry.state !== 'loading') return Promise.resolve();
  return new Promise((resolve) => {
    entry.element.addEventListener('load', () => resolve(), { once: true });
    entry.element.addEventListener('error', () => resolve(), { once: true });
  });
}

/**
 * Takes over the `beforeInteractive` scripts the server rendered into the document, in document order, those not yet
 * taken over. Inline code has run where it stands: it is recorded, so that a component that mounts again (after a
 * hydration error) does not add it once more. Each external script's inert entry becomes a script at the end of the
 * head, not async, so the browser runs them in this order as they arrive, with the page's nonce. Under a nonce, only
 * entries with that nonce count: markup that reached the page some other way does not know it. Resolves once every
 * external script it added has loaded or failed.
 */
export function adoptServerScripts(nonce: string | undefined): Promise<void> {
  const waits: Promise<void>[] = [];
  for (const element of document.querySelectorAll<HTMLScriptElement>(`script[${ENTRY}]`)) {
    const key = element.getAttribute(ENTRY);
    if (!key || added.has(key) || (nonce && element.nonce !== nonce)) continue;
    const external = element.type === 'application/json' ? externalEntry(element.textContent) : null;
    if (external) {
      const entry = insert(key, { ...external, ...(nonce ? { nonce } : {}), async: false, parent: document.head });
      if (runs(external.attributes)) waits.push(outcome(entry));
    } else {
      added.set(key, { element, state: 'loaded' });
    }
  }
  return Promise.all(waits).then(() => undefined);
}

/**
 * React's own rule for the text of an inline script: `</script` would close the element and `<script` would make the
 * parser look for a second closing tag, so their `s` is written `s` (or `S`), which JavaScript reads as the
 * same letter in strings, template literals, regular expressions and names.
 */
const SCRIPT_TAG = /(<\/|<)(s)(cript)/gi;
const escapeScriptText = (code: string) =>
  code.replace(SCRIPT_TAG, (_match, open: string, s: string, rest: string) => `${open}${s === 's' ? '\\u0073' : '\\u0053'}${rest}`);

const subscribeToNothing = () => () => {};

/** Scripts warned about already: each is warned about once. */
const warned = new Set<string>();

function warnAboutTiming(key: string, { async, defer, type }: ScriptProps) {
  if (process.env.NODE_ENV === 'production' || warned.has(key)) return;
  const found = [async && 'async', defer && 'defer', type === 'module' && 'type="module"'].filter(Boolean);
  if (found.length === 0) return;
  warned.add(key);
  console.warn(
    `netjes: <Script strategy="beforeInteractive"> "${key}" has ${found.join(', ')}. A beforeInteractive script runs ` +
      "in order, before the page's code, whatever async and defer say, and type=\"module\" makes inline code wait until " +
      'the whole document is parsed. Leave them out, or use strategy="afterInteractive".',
  );
}

/**
 * Like `next/script`: third-party scripts that should not block the page. Scripts are added to the document once,
 * whichever component renders them first. Only `beforeInteractive` renders anything where it stands, and only into a
 * server-rendered document: the inline script, or an inert entry for an external one, and a marker in the head.
 */
export function Script(given: ScriptProps) {
  const pageNonce = useNonce();
  const props = given.nonce || !pageNonce ? given : { ...given, nonce: pageNonce };
  const { src, id, strategy = 'afterInteractive', onLoad, onError, nonce, crossOrigin, integrity, children } = props;
  const callbacks = useRef({ onLoad, onError });
  callbacks.current = { onLoad, onError };
  const key = id ?? src;
  // True on the server and while this component hydrates, and for as long as it stays mounted after that, so that it
  // keeps the markup it hydrated. False when it mounts in the browser.
  const fromServer = useRef(useSyncExternalStore(subscribeToNothing, () => false, () => true)).current;
  const inDocument = strategy === 'beforeInteractive' && fromServer && key !== undefined;

  useEffect(() => {
    if (!key) return;
    let cancelled = false;
    const attach = () => {
      if (cancelled) return;
      // An entry that streamed in after the client entry took the others over is taken over now.
      if (inDocument && !added.has(key)) void adoptServerScripts(pageNonce);
      const entry = added.get(key) ?? add(key, props);
      const notify = () => {
        if (cancelled) return;
        if (entry.state === 'loaded') callbacks.current.onLoad?.(entry.event ?? new Event('load'));
        else if (entry.state === 'failed') callbacks.current.onError?.(entry.event ?? new Event('error'));
      };
      if (entry.state !== 'loading') {
        notify();
        return;
      }
      entry.element.addEventListener('load', notify, { once: true });
      entry.element.addEventListener('error', notify, { once: true });
    };
    if (strategy === 'lazyOnload') whenIdle(attach);
    else attach();
    return () => {
      cancelled = true;
    };
    // The script is identified by its key; changing other props does not add it again.
  }, [key, strategy]);

  if (!inDocument) return null;
  warnAboutTiming(key, props);
  const { strategy: _strategy, onLoad: _onLoad, onError: _onError, children: _children, src: _src, id: _id, nonce: _nonce, ...rest } = props;
  // In the head, where the client entry looks for it once it has the payload.
  const marker = <meta name={BEFORE_INTERACTIVE_MARKER} />;
  if (!src) {
    // Written as it is (React would escape text children, and they would not match the server's); escaped as React
    // escapes its own scripts, so the text cannot end the element. A hash-based policy lists the hash of that text.
    return (
      <>
        {marker}
        <script {...rest} id={id} nonce={nonce} data-netjes-script={key} dangerouslySetInnerHTML={{ __html: escapeScriptText(children ?? '') }} />
      </>
    );
  }
  // The id goes to the script the entry becomes, not to the entry.
  const attributes = attributesOf(id ? { ...rest, id } : rest);
  // The browser fetches it while it parses; adoptServerScripts runs it from the entry. A script the browser will not run
  // is not fetched: a consent manager's would reach its host before the visitor agreed.
  if (runs(attributes)) {
    preload(src, { as: 'script', ...(nonce ? { nonce } : {}), ...(integrity ? { integrity } : {}), ...(crossOrigin ? { crossOrigin } : {}) });
  }
  return (
    <>
      {marker}
      <script type="application/json" nonce={nonce} data-netjes-script={key} dangerouslySetInnerHTML={{ __html: htmlSafeJson({ src, attributes }) }} />
    </>
  );
}
