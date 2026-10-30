// Must come first: installs the React Fast Refresh runtime before any component module runs (empty in builds).
import '@vitejs/plugin-react/preamble';
import { createRoot, hydrateRoot } from 'react-dom/client';
import { pages } from 'virtual:netjes/pages';
import { deferredRegistry, revivePayload } from './deferred.ts';
import { type RouteModules, createModuleLoader } from './modules.ts';
import { DATA_ELEMENT_ID, parseInitialPayload } from './payload.ts';
import { Root } from './root.tsx';
import { withClockOffset } from './router.tsx';

/**
 * The payload is the body's first element, so it is normally parsed by the time this async script runs. If the
 * script won the race, wait for the element rather than for the whole (streaming) document.
 */
function payloadElement(): Promise<HTMLElement | null> {
  // Complete once the parser has moved past it (a large payload can arrive in several chunks).
  const complete = () => {
    const element = document.getElementById(DATA_ELEMENT_ID);
    return element && (element.nextSibling || document.readyState !== 'loading') ? element : null;
  };
  const found = complete();
  if (found || document.readyState !== 'loading') return Promise.resolve(found ?? document.getElementById(DATA_ELEMENT_ID));
  return new Promise((resolve) => {
    const observer = new MutationObserver(() => {
      const element = complete();
      if (element) {
        observer.disconnect();
        resolve(element);
      }
    });
    observer.observe(document, { childList: true, subtree: true });
    document.addEventListener('DOMContentLoaded', () => {
      observer.disconnect();
      resolve(document.getElementById(DATA_ELEMENT_ID));
    });
  });
}

const dataJson = (await payloadElement())?.textContent;
if (!dataJson) throw new Error(`netjes: #${DATA_ELEMENT_ID} is missing; was this page rendered by Netjes?`);

// Page.Defer values the server streamed in before this script ran, and from now on, the ones still to come.
type DeferredGlobals = { __netjes_v?: unknown; __netjes_d?: (id: string, value: unknown, error?: string) => void };
const globals = self as unknown as DeferredGlobals;
const deferred = deferredRegistry();
const settle = (id: string, value: unknown, error?: string) => (error === undefined || error === null ? deferred.resolve(id, value) : deferred.reject(id, error));

/**
 * The values the head bootstrap collected: its own plain object, `{ id: [value, error] }`. Anything else is page
 * markup standing in for the global (an element with `id="__netjes_v"`), and must not stop the page from hydrating.
 */
function collectedValues(collected: unknown): [string, unknown][] {
  if (typeof collected !== 'object' || collected === null) return [];
  const prototype: unknown = Object.getPrototypeOf(collected);
  return prototype === Object.prototype || prototype === null ? Object.entries(collected) : [];
}
for (const [id, entry] of collectedValues(globals.__netjes_v)) {
  if (Array.isArray(entry)) settle(id, entry[0], typeof entry[1] === 'string' ? entry[1] : undefined);
}
globals.__netjes_d = settle;

const parsed = revivePayload(parseInitialPayload(JSON.parse(dataJson)), deferred.source);
// Judge the server's seeded data as fresh as the server did when it rendered, so hydration matches.
const payload = withClockOffset(parsed, parsed.renderedAt);
const loader = createModuleLoader(pages, { nameFailedImports: true });

/** How long the first page waits before it tries a module that failed to load once more. */
const RETRY_DELAY_MS = 500;

/**
 * The first page's modules. One that fails to load (a dropped request, a deploy that removed its chunk) gets one more
 * try, which the browser fetches again. If that fails too, the page stays as the server sent it, without the client
 * router: its links and forms are plain HTML, and the browser loads each in full.
 */
async function firstPageModules(): Promise<RouteModules | null> {
  try {
    return await loader.loadRoute(payload);
  } catch {
    await new Promise((resolve) => setTimeout(resolve, RETRY_DELAY_MS));
  }
  try {
    return await loader.loadRoute(payload);
  } catch (error) {
    const reason = error instanceof Error ? error.message : `netjes: the page's modules could not be loaded (${String(error)})`;
    const outcome = payload.document.clientRender
      ? 'the page cannot render without it'
      : 'the page stays as the server rendered it, without the client router, and its links and forms load pages in full';
    console.error(`${reason}: ${outcome}`, error);
    return null;
  }
}

const modules = await firstPageModules();
if (modules) {
  const root = <Root payload={payload} modules={modules} loader={loader} dataJson={dataJson} />;
  if (payload.document.clientRender) {
    // No sidecar was ready, so the server sent the document without the page: there is nothing to hydrate. React keeps
    // the scripts and stylesheets already in it and replaces the rest.
    createRoot(document).render(root);
  } else {
    hydrateRoot(document, root);
  }
}
