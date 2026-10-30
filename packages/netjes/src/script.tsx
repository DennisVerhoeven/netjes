import { type ScriptHTMLAttributes, useEffect, useRef } from 'react';
import { preinit } from 'react-dom';
import { useNonce } from './nonce.tsx';

/** When `<Script>` loads its script: before or after the page is interactive, or when the browser is idle. */
export type ScriptStrategy = 'beforeInteractive' | 'afterInteractive' | 'lazyOnload';

/** The props of `<Script>`: a script's own, with a loading strategy and load callbacks. */
export type ScriptProps = Omit<ScriptHTMLAttributes<HTMLScriptElement>, 'onLoad' | 'onError' | 'children' | 'dangerouslySetInnerHTML'> & {
  /** Identifies the script (required for inline scripts): each is added once, however often it is rendered. */
  id?: string;
  /**
   * `afterInteractive` (default): once the page is interactive. `lazyOnload`: when the browser is idle.
   * `beforeInteractive`: in the server-rendered head, before the page's own scripts (external scripts only).
   */
  strategy?: ScriptStrategy;
  onLoad?: (event: Event) => void;
  onError?: (event: Event) => void;
  /** Inline code, instead of `src`. */
  children?: string;
};

type Entry = { element: HTMLScriptElement; state: 'loading' | 'loaded' | 'failed'; event?: Event };

/** Scripts already added to this document, by id or src. */
const added = new Map<string, Entry>();

function whenIdle(run: () => void) {
  const schedule = () =>
    typeof window.requestIdleCallback === 'function' ? window.requestIdleCallback(run) : window.setTimeout(run, 1);
  if (document.readyState === 'complete') schedule();
  else window.addEventListener('load', schedule, { once: true });
}

function add(key: string, { src, children, strategy: _strategy, id, onLoad: _onLoad, onError: _onError, ...attributes }: ScriptProps): Entry {
  const element = document.createElement('script');
  for (const [name, value] of Object.entries(attributes)) {
    if (value === undefined || value === false || typeof value === 'function' || typeof value === 'object') continue;
    element.setAttribute(name === 'className' ? 'class' : name === 'crossOrigin' ? 'crossorigin' : name, value === true ? '' : String(value));
  }
  if (id) element.id = id;
  // The property, not the attribute: browsers hide nonce attributes once a policy applies.
  if (attributes.nonce) element.nonce = attributes.nonce;
  const entry: Entry = { element, state: 'loading' };
  element.addEventListener('load', (event) => Object.assign(entry, { state: 'loaded', event }));
  element.addEventListener('error', (event) => Object.assign(entry, { state: 'failed', event }));
  if (src) {
    element.src = src;
    element.async = true;
  } else {
    element.textContent = children ?? '';
    // Inline scripts run as they are appended; there is no load event to wait for.
    entry.state = 'loaded';
  }
  added.set(key, entry);
  document.body.append(element);
  return entry;
}

/**
 * Like `next/script`: third-party scripts that should not block the page. Renders nothing in place; scripts are
 * added to the document once, whichever component renders them first.
 */
export function Script(given: ScriptProps) {
  const pageNonce = useNonce();
  const props = given.nonce || !pageNonce ? given : { ...given, nonce: pageNonce };
  const { src, id, strategy = 'afterInteractive', onLoad, onError, nonce, crossOrigin, integrity } = props;
  const callbacks = useRef({ onLoad, onError });
  callbacks.current = { onLoad, onError };
  const key = id ?? src;

  if (strategy === 'beforeInteractive' && src) {
    preinit(src, { as: 'script', ...(nonce ? { nonce } : {}), ...(integrity ? { integrity } : {}), ...(crossOrigin ? { crossOrigin } : {}) });
  }

  useEffect(() => {
    if (!key || (strategy === 'beforeInteractive' && src)) return;
    let cancelled = false;
    const attach = () => {
      if (cancelled) return;
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

  return null;
}
