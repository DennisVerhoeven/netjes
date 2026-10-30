// @ts-check
// The Netjes sidecar: renders pages for the .NET server over loopback HTTP. Started and supervised by NodeSidecar.cs.
//   development: hosts the Vite dev server (HMR for the browser) and renders with fresh modules on every request.
//   production:  renders with the prebuilt dist/server/server-entry.js.
// Protocol: POST /render with the HTML-safe page payload JSON as body -> 200 text/html, streamed from the moment the
// shell is ready (or once everything is, with X-Netjes-Wait-All: 1), or 500 {"error":{message,stack}} in plain text
// (no terminal colours) if the shell fails. Every request must carry the X-Netjes-Token the parent passed in
// NETJES_TOKEN. Renders are aborted when the caller disconnects, when the shell is not ready after
// NETJES_RENDER_TIMEOUT_MS, or when the whole stream takes longer than NETJES_STREAM_TIMEOUT_MS. Exits when stdin
// closes.
// Startup: prints NETJES_READY {"port","viteOrigin"} on stdout once it listens. When the Vite dev server does not start,
// it prints NETJES_FAILED and exits with code 1: {"reason":"vite-port-in-use","port"} when its port is taken, and
// {"reason":"vite-failed","message","code"} for anything else, such as an error in vite.config or a package it imports
// that is not installed (message in plain text; code is the error's when it has one, such as ERR_MODULE_NOT_FOUND, or
// MODULE_NOT_FOUND from a CommonJS config).
import http from 'node:http';
import { pipeline } from 'node:stream/promises';
import { pathToFileURL } from 'node:url';

const mode = process.env.NETJES_MODE === 'development' ? 'development' : 'production';
const root = process.env.NETJES_ROOT ?? process.cwd();
const token = process.env.NETJES_TOKEN ?? '';
// Without a token the guard below would accept a request that sends an empty X-Netjes-Token header.
if (!token) throw new Error('NETJES_TOKEN is required: the sidecar is started by Netjes, which sets it');
// Page code runs in this process; it has no business knowing the token.
delete process.env.NETJES_TOKEN;
const renderTimeoutMs = Number(process.env.NETJES_RENDER_TIMEOUT_MS ?? 15_000);
const streamTimeoutMs = Number(process.env.NETJES_STREAM_TIMEOUT_MS ?? 60_000);
const vitePort = Number(process.env.NETJES_VITE_PORT ?? 5173);
const MAX_BODY_BYTES = 16 * 1024 * 1024;
const CSS_FILE = /\.(css|less|sass|scss|styl|stylus|pcss|postcss|sss)$/;
// Terminal escape sequences (ECMA-48): CSI, as ESC [ or its one-character form (colours, as in Vite's code frames, which
// it colours in a pipe too), strings such as OSC (links), and the other escapes (a cursor save, say). An error page
// shows them as text, and a log file keeps them as such. Not Node's stripVTControlCharacters, which after ESC 7 (that
// cursor save) can take the next letter too. NodeSidecar.cs strips this process's output with the same pattern.
const TERMINAL_ESCAPES = /(?:\u001b\[|\u009b)[0-?]*[ -/]*[@-~]|\u001b[\]PX^_][^\u0007\u001b]*(?:\u0007|\u001b\\)|\u001b[ -/]*[0-~]/g;

/** @param {string} text */
const plainText = (text) => text.replace(TERMINAL_ESCAPES, '');

/** @typedef {{ signal: AbortSignal, waitForAll?: boolean, deferred?: (id: string) => PromiseLike<unknown> }} RenderOptions */
/** @type {(data: string, options: RenderOptions) => Promise<string>} */
let render;
/** @type {((data: string, options: RenderOptions) => Promise<import('node:stream').Readable>) | undefined} */
let renderToStream;
/** @type {import('vite').ViteDevServer | undefined} */
let vite;

/**
 * Says why the Vite dev server did not start, for .NET to report in a line of its own (Node's report of an uncaught
 * error would bury it under its stack trace), and exits.
 * @param {Record<string, unknown>} failed
 * @returns {Promise<never>}
 */
async function failStartup(failed) {
  await new Promise((resolve) => process.stdout.write(`NETJES_FAILED ${JSON.stringify(failed)}\n`, resolve));
  process.exit(1);
}

/** @param {unknown} error */
const viteFailed = (error) =>
  failStartup({
    reason: 'vite-failed',
    message: plainText(error instanceof Error ? error.message : String(error)),
    code: error instanceof Error && 'code' in error && typeof error.code === 'string' ? error.code : undefined,
  });

if (mode === 'development') {
  const origin = process.env.NETJES_VITE_ORIGIN ?? `http://localhost:${vitePort}`;
  /** @type {import('vite').ViteDevServer} */
  let dev;
  try {
    // Vite is the app's own dependency; it fails here when it is not installed, and on an error in vite.config.
    const { createServer } = await import('vite');
    dev = await createServer({
      root,
      appType: 'custom',
      clearScreen: false,
      server: { port: vitePort, strictPort: true, origin, host: process.env.NETJES_VITE_HOST || undefined },
    });
  } catch (error) {
    // It exits, so dev is set from here on.
    dev = await viteFailed(error);
  }
  vite = dev;
  try {
    await dev.listen();
  } catch (error) {
    // Most often another app's Vite dev server: .NET names the port and how to move it.
    if (error instanceof Error && error.message === `Port ${vitePort} is already in use`) {
      await failStartup({ reason: 'vite-port-in-use', port: vitePort });
    }
    await viteFailed(error);
  }
  /** The netjes plugin's development API (vite/plugin.mjs). */
  const netjes = /** @type {{ rescanPages(): void } | undefined} */ (dev.config.plugins.find((plugin) => plugin.name === 'netjes')?.api);
  /**
   * Runs a render with the server entry. .NET routes a page file as soon as its own watcher sees it, but the page
   * module map changes only when Vite's watcher reports a page file, which for this one can be much later (a file it
   * missed is reported once something else changes in its folder) or never (a folder Vite does not watch). A render
   * that names a file the map lacks has the plugin build the map again from the files on disk, and runs once more.
   * @template T
   * @param {(entry: Awaited<ReturnType<typeof dev.ssrLoadModule>>) => Promise<T>} run
   */
  const withServerEntry = async (run) => {
    try {
      return await run(await dev.ssrLoadModule('netjes/server-entry'));
    } catch (error) {
      if (!netjes || !(error instanceof Error) || error.name !== 'MissingModuleError') throw error;
      netjes.rescanPages();
      return run(await dev.ssrLoadModule('netjes/server-entry'));
    }
  };
  render = async (data, options) => {
    const styled = await withDevStyles(dev, data);
    return withServerEntry((entry) => entry.render(styled, options));
  };
  renderToStream = async (data, options) => {
    const styled = await withDevStyles(dev, data);
    return withServerEntry((entry) => entry.renderToStream(styled, options));
  };
} else {
  const entry = process.env.NETJES_SERVER_ENTRY;
  if (!entry) throw new Error('NETJES_SERVER_ENTRY is not set');
  ({ render, renderToStream } = await import(pathToFileURL(entry).href));
}

/**
 * Inlines the CSS imported by the page and its segment files, so the server-rendered page is styled before the client
 * script runs. Vite's client adopts these <style data-vite-dev-id> tags and keeps them updated through HMR.
 * @param {import('vite').ViteDevServer} server
 * @param {string} data
 */
async function withDevStyles(server, data) {
  const payload = JSON.parse(data);
  /** @type {Array<Record<string, unknown>>} */
  const segments = payload.segments ?? [];
  const files = [...segments.flatMap((s) => [s.layout, s.template, s.loading, s.error]), payload.page].filter(
    (file) => typeof file === 'string',
  );
  const environment = server.environments.ssr;
  /** @type {Map<string, string>} */
  const styles = new Map();
  /** @type {Set<string>} */
  const seen = new Set();

  /** @param {import('vite').EnvironmentModuleNode | undefined} node */
  async function collect(node) {
    if (!node?.id || seen.has(node.id)) return;
    seen.add(node.id);
    if (node.file && CSS_FILE.test(node.file) && !node.id.includes('?')) {
      const css = await server.ssrLoadModule(`${node.url}?inline`);
      styles.set(node.file, String(css.default));
      return;
    }
    for (const imported of node.importedModules) await collect(imported);
  }

  for (const file of files) {
    await server.ssrLoadModule(`/${file}`);
    await collect(await environment.moduleGraph.getModuleByUrl(`/${file}`));
  }
  if (styles.size === 0) return data;
  payload.document.devStyles = [...styles].map(([id, css]) => ({ id, css }));
  return JSON.stringify(payload).replace(/[<>&\u2028\u2029]/g, (c) => `\\u${c.charCodeAt(0).toString(16).padStart(4, '0')}`);
}

/**
 * Page.Defer values per render, as .NET posts them to /resolve. Values may arrive before the render asks for them;
 * entries nobody claims are dropped after the stream timeout.
 * @typedef {{ promise: Promise<unknown>, resolve: (value: unknown) => void, reject: (error: Error) => void }} Pending
 * @type {Map<string, { values: Map<string, Pending>, timer: NodeJS.Timeout }>}
 */
const renders = new Map();

/** @param {string} renderId */
function renderEntry(renderId) {
  let entry = renders.get(renderId);
  if (!entry) {
    const timer = setTimeout(() => renders.delete(renderId), streamTimeoutMs + 5_000);
    timer.unref();
    entry = { values: new Map(), timer };
    renders.set(renderId, entry);
  }
  return entry;
}

/** @param {string} renderId @param {string} id */
function pendingValue(renderId, id) {
  const { values } = renderEntry(renderId);
  let pending = values.get(id);
  if (!pending) {
    /** @type {(value: unknown) => void} */
    let resolve = () => {};
    /** @type {(error: Error) => void} */
    let reject = () => {};
    const promise = new Promise((res, rej) => {
      resolve = res;
      reject = rej;
    });
    promise.catch(() => {});
    pending = { promise, resolve, reject };
    values.set(id, pending);
  }
  return pending;
}

/** @param {string} renderId */
function forgetRender(renderId) {
  const entry = renders.get(renderId);
  if (!entry) return;
  clearTimeout(entry.timer);
  renders.delete(renderId);
}

/** @param {http.IncomingMessage} req */
async function readBody(req) {
  /** @type {Buffer[]} */
  const chunks = [];
  let size = 0;
  for await (const chunk of req) {
    size += chunk.length;
    if (size > MAX_BODY_BYTES) throw new Error('payload too large');
    chunks.push(chunk);
  }
  return Buffer.concat(chunks).toString('utf8');
}

const server = http.createServer(async (req, res) => {
  if (req.headers['x-netjes-token'] !== token) {
    res.writeHead(403).end();
    return;
  }
  if (req.method === 'GET' && req.url === '/health') {
    res.writeHead(200, { 'content-type': 'text/plain' }).end('ok');
    return;
  }
  if (req.method === 'POST' && req.url === '/resolve') {
    try {
      const { renderId, id, value, error } = JSON.parse(await readBody(req));
      if (typeof renderId !== 'string' || typeof id !== 'string') throw new Error('renderId and id are required');
      const pending = pendingValue(renderId, id);
      if (typeof error === 'string') pending.reject(new Error(error));
      else pending.resolve(value);
      res.writeHead(204).end();
    } catch (error) {
      res.writeHead(400, { 'content-type': 'text/plain' }).end(error instanceof Error ? error.message : String(error));
    }
    return;
  }
  if (req.method !== 'POST' || req.url !== '/render') {
    res.writeHead(404).end();
    return;
  }
  // Stop rendering once nobody is waiting for the result: the caller went away or the time is up.
  const controller = new AbortController();
  // Waiting for everything (crawlers) includes the slow Suspense boundaries, so it gets the stream's time limit.
  const waitForAll = req.headers['x-netjes-wait-all'] === '1';
  const shellTimeoutMs = waitForAll ? streamTimeoutMs : renderTimeoutMs;
  let timer = setTimeout(() => controller.abort(new Error(`Rendering timed out after ${shellTimeoutMs}ms`)), shellTimeoutMs);
  res.on('close', () => {
    if (!res.writableFinished) controller.abort(new Error('The caller stopped waiting for the render'));
  });
  const renderId = typeof req.headers['x-netjes-render-id'] === 'string' ? req.headers['x-netjes-render-id'] : undefined;
  /** @type {RenderOptions} */
  const options = { signal: controller.signal, waitForAll };
  if (renderId) {
    options.deferred = (id) => pendingValue(renderId, id).promise;
    res.on('close', () => forgetRender(renderId));
  }
  try {
    const data = await readBody(req);
    if (!renderToStream) {
      const html = await render(data, options);
      res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' }).end(html);
      return;
    }
    const stream = await renderToStream(data, options);
    // The shell is out; what is left may take longer (Suspense boundaries), up to the stream timeout.
    clearTimeout(timer);
    timer = setTimeout(() => controller.abort(new Error(`Streaming timed out after ${streamTimeoutMs}ms`)), streamTimeoutMs);
    res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'x-accel-buffering': 'no' });
    try {
      await pipeline(stream, res);
    } catch (error) {
      if (!controller.signal.aborted) console.error('netjes: streaming failed', error);
      res.destroy();
    }
  } catch (error) {
    const err = error instanceof Error ? error : new Error(String(error));
    vite?.ssrFixStacktrace(err);
    const message = plainText(err.message);
    const stack = err.stack === undefined ? undefined : plainText(err.stack);
    // The .NET span that asked for this render (W3C trace context), to find it next to the request's trace.
    const trace = req.headers.traceparent ? ` traceparent=${req.headers.traceparent}` : '';
    console.error(`netjes: render failed${trace}\n${stack ?? message}`);
    if (!res.destroyed) {
      res.writeHead(500, { 'content-type': 'application/json' }).end(JSON.stringify({ error: { message, stack } }));
    }
  } finally {
    clearTimeout(timer);
  }
});

server.listen(0, '127.0.0.1', () => {
  const address = server.address();
  const port = typeof address === 'object' && address ? address.port : 0;
  const viteOrigin = vite ? (vite.config.server.origin ?? `http://localhost:${vitePort}`) : null;
  process.stdout.write(`NETJES_READY ${JSON.stringify({ port, viteOrigin })}\n`);
});

async function shutdown() {
  server.close();
  await vite?.close();
  process.exit(0);
}

// A promise rejected by page code without a handler would otherwise kill every render in flight.
process.on('unhandledRejection', (reason) => console.error('netjes: unhandled promise rejection', reason));

// The parent keeps our stdin open for as long as it lives; EOF means it is gone.
process.stdin.on('end', shutdown);
process.stdin.resume();
process.on('SIGTERM', shutdown);
process.on('SIGINT', shutdown);
