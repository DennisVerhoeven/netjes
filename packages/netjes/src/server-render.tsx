import { type Readable, Transform, Writable } from 'node:stream';
import type { ReactNode } from 'react';
import { renderToPipeableStream } from 'react-dom/server';
import { type DeferredSource, deferredScript, revivePayload } from './deferred.ts';
import { type ModuleMap, createModuleLoader } from './modules.ts';
import { type InitialPayload, parseInitialPayload } from './payload.ts';
import { RenderCleanupContext } from './render-cleanup.ts';
import { Root } from './root.tsx';

export type RenderOptions = {
  /** Aborts the render (React stops waiting on suspended content), e.g. when the server stops waiting for it. */
  signal?: AbortSignal;
  /** Promises for the payload's Page.Defer placeholders, by id (the sidecar settles them as .NET resolves them). */
  deferred?: (id: string) => PromiseLike<unknown>;
};

export type StreamOptions = RenderOptions & {
  /** Start sending only once everything has rendered (no Suspense fallbacks): for crawlers and the static export. */
  waitForAll?: boolean;
};

/**
 * React writes a Suspense boundary that is ready when the shell goes out in place, unless what it has written so far
 * (the shell, with the payload and dev styles) and the boundary pass this many bytes (12.8 kB by default): then it
 * sends the boundary after the shell, hidden, for an inline script to reveal, which a browser without JavaScript never
 * runs. No limit: whatever is ready goes in place, and only what is still pending streams.
 */
const progressiveChunkSize = Number.POSITIVE_INFINITY;

/**
 * Streams the document: resolves once the shell (everything outside unresolved Suspense boundaries) is ready, with
 * a stream that carries it and then each boundary as it resolves. Rejects, before anything is sent, if the shell
 * fails to render.
 */
function renderToStream(
  element: ReactNode,
  options: StreamOptions,
  deferredIds: readonly string[],
  source: DeferredSource,
  nonce: string | undefined,
): Promise<Readable> {
  const { signal, waitForAll = false } = options;
  return new Promise((resolve, reject) => {
    signal?.throwIfAborted();
    // Passes React's chunks through, and puts a script for each deferred value between them once the shell is out.
    // React writes a flush as several synchronous chunks, so a chunk can end in the middle of an element: scripts are
    // only ever pushed from a later turn (setImmediate, or a promise callback), when React is between flushes.
    let shellSent = false;
    let draining = false;
    const queued: string[] = [];
    const drain = () => {
      draining = false;
      if (output.writableEnded || output.destroyed) return;
      for (const script of queued.splice(0)) output.push(script);
    };
    const output = new Transform({
      transform(chunk, _encoding, callback) {
        shellSent = true;
        this.push(chunk);
        if (queued.length > 0 && !draining) {
          draining = true;
          setImmediate(drain);
        }
        callback();
      },
      flush(callback) {
        for (const script of queued.splice(0)) this.push(script);
        callback();
      },
    });
    const send = (script: string) => {
      if (shellSent && !draining && !output.writableEnded) output.push(script);
      else queued.push(script);
    };
    for (const id of deferredIds) {
      source(id).then(
        (value) => send(deferredScript(id, value, undefined, nonce)),
        (error: unknown) => send(deferredScript(id, null, error instanceof Error ? error.message : String(error), nonce)),
      );
    }
    let started = false;
    const start = () => {
      started = true;
      stream.pipe(output);
      resolve(output);
    };
    const stream = renderToPipeableStream(element, {
      // React's own inline scripts (Suspense reveals) carry the CSP nonce.
      ...(nonce ? { nonce } : {}),
      progressiveChunkSize,
      onShellReady() {
        if (!waitForAll) start();
      },
      onAllReady() {
        if (waitForAll) start();
      },
      onShellError: reject,
      onError(error) {
        // Errors inside a Suspense boundary: React streams the fallback and retries on the client.
        if (!signal?.aborted) console.error(error);
      },
    });
    signal?.addEventListener(
      'abort',
      () => {
        stream.abort(signal.reason);
        if (started) output.destroy(signal.reason instanceof Error ? signal.reason : new Error(String(signal.reason)));
        else reject(signal.reason);
      },
      { once: true },
    );
  });
}

function renderToHtml(element: ReactNode, signal: AbortSignal | undefined, nonce: string | undefined): Promise<string> {
  return new Promise((resolve, reject) => {
    signal?.throwIfAborted();
    const chunks: Buffer[] = [];
    const sink = new Writable({
      write(chunk: Buffer | string, _encoding, callback) {
        chunks.push(typeof chunk === 'string' ? Buffer.from(chunk) : chunk);
        callback();
      },
      final(callback) {
        resolve(Buffer.concat(chunks).toString('utf8'));
        callback();
      },
    });
    const stream = renderToPipeableStream(element, {
      // For any inline script React itself writes.
      ...(nonce ? { nonce } : {}),
      progressiveChunkSize,
      // Wait for every Suspense boundary: the sidecar answers with one complete document.
      onAllReady() {
        stream.pipe(sink);
      },
      onShellError: reject,
      onError(error) {
        // Errors inside a Suspense boundary: React renders the fallback and retries on the client.
        if (!signal?.aborted) console.error(error);
      },
    });
    signal?.addEventListener(
      'abort',
      () => {
        reject(signal.reason);
        stream.abort(signal.reason);
      },
      { once: true },
    );
  });
}

/** Revives the payload's deferred placeholders, noting which ids it found. */
function withDeferred(payload: InitialPayload, deferred: RenderOptions['deferred']) {
  const ids: string[] = [];
  const promises = new Map<string, Promise<unknown>>();
  const source: DeferredSource = (id) => {
    let promise = promises.get(id);
    if (!promise) {
      ids.push(id);
      // Without a source (e.g. a test render) a deferred value never arrives; its boundary shows the fallback.
      promise = deferred ? Promise.resolve(deferred(id)) : new Promise<unknown>(() => {});
      promise.catch(() => {});
      promises.set(id, promise);
    }
    return promise;
  };
  return { payload: revivePayload(payload, source), ids, source };
}

/** What one render registered through {@link RenderCleanupContext}, run once its document is done. */
function cleanups() {
  const registered: (() => void)[] = [];
  return {
    wrap: (element: ReactNode) => <RenderCleanupContext value={(cleanup) => registered.push(cleanup)}>{element}</RenderCleanupContext>,
    run: () => {
      for (const cleanup of registered.splice(0)) cleanup();
    },
  };
}

export function createServerRenderer(modules: ModuleMap) {
  const loader = createModuleLoader(modules);
  return {
    /** Renders the page described by the (HTML-safe) payload JSON to a complete HTML document. */
    async render(dataJson: string, options: RenderOptions = {}): Promise<string> {
      const { payload } = withDeferred(parseInitialPayload(JSON.parse(dataJson)), options.deferred);
      const routeModules = await loader.loadRoute(payload);
      const done = cleanups();
      try {
        return await renderToHtml(
          done.wrap(<Root payload={payload} modules={routeModules} loader={loader} dataJson={dataJson} />),
          options.signal,
          payload.document.nonce,
        );
      } finally {
        done.run();
      }
    },
    /** Like {@link render}, streamed: see {@link StreamOptions}. */
    async renderToStream(dataJson: string, options: StreamOptions = {}): Promise<Readable> {
      const { payload, ids, source } = withDeferred(parseInitialPayload(JSON.parse(dataJson)), options.deferred);
      const routeModules = await loader.loadRoute(payload);
      const done = cleanups();
      try {
        const stream = await renderToStream(
          done.wrap(<Root payload={payload} modules={routeModules} loader={loader} dataJson={dataJson} />),
          options,
          ids,
          source,
          payload.document.nonce,
        );
        // Ended, failed or abandoned: the document needs nothing more from the render.
        stream.once('close', done.run);
        return stream;
      } catch (error) {
        done.run();
        throw error;
      }
    },
  };
}
