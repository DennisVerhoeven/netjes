// A stand-in for dist/server/server-entry.js, driven by the payload's `page` field.

/** How often 'missing-module' has been rendered. */
let missingModuleRenders = 0;

/** What Vite throws in development for a page with a syntax error, terminal colours and all (it adds them in a pipe too). */
export const COLOURED_SYNTAX_ERROR =
  'Transform failed with 1 error:\n\n\u001b[31m[PARSE_ERROR] \u001b[0mExpected `>` but found `}`\n' +
  '   \u001b[38;5;246m╭\u001b[0m\u001b[38;5;246m─\u001b[0m\u001b[38;5;246m[\u001b[0m app/about/page.tsx:3:1 \u001b[38;5;246m]\u001b[0m\n' +
  '   \u001b[38;5;246m│\u001b[0m\n \u001b[38;5;246m3 │\u001b[0m }\n \u001b[38;5;240m  │\u001b[0m ┬  \n' +
  ' \u001b[38;5;240m  │\u001b[0m ╰── `>` expected\n\u001b[38;5;246m───╯\u001b[0m\n';

/**
 * @param {string} data
 * @param {{ signal?: AbortSignal }} [options]
 */
export async function render(data, { signal } = {}) {
  const { page } = JSON.parse(data);
  switch (page) {
    case 'fail':
      throw new Error('boom');
    case 'coloured-fail':
      throw new Error(COLOURED_SYNTAX_ERROR);
    case 'escapes-fail':
      // The single-byte (C1) form of a colour, a link, and a cursor save and restore.
      throw new Error('\u009b1mbold\u009b0m, \u001b]8;;https://example.com/a?b=c\u001b\\a link\u001b]8;;\u001b\\, \u001b7copied\u001b8');
    case 'hang':
      return new Promise((_, reject) =>
        signal?.addEventListener('abort', () => {
          console.log('RENDER_ABORTED');
          reject(signal.reason);
        }),
      );
    case 'stray-rejection':
      Promise.reject(new Error('nobody handles this'));
      return '<html>ok</html>';
    case 'env':
      return `<html>${process.env.NETJES_TOKEN ?? 'no token'}</html>`;
    case 'missing-module-renders':
      return `<html>${missingModuleRenders}</html>`;
    case 'missing-module': {
      // What modules.ts throws for a file the build does not have.
      missingModuleRenders++;
      const error = new Error('netjes: no module for app/missing/page.tsx in this build');
      error.name = 'MissingModuleError';
      throw error;
    }
    default:
      return `<html>${page}</html>`;
  }
}

/**
 * The streaming entry point the sidecar prefers. `deferred` reads Page.Defer values the parent resolves. `stream` sends a shell, then the rest after 150ms.
 * @param {string} data
 * @param {{ signal?: AbortSignal, waitForAll?: boolean, deferred?: (id: string) => PromiseLike<unknown> }} [options]
 */
export async function renderToStream(data, { signal, waitForAll = false, deferred } = {}) {
  const { page } = JSON.parse(data);
  const { PassThrough } = await import('node:stream');
  if (page === 'stream-fail') throw new Error('shell failed');
  if (page === 'slow-all') {
    // Everything takes 300ms to render, as when a crawler waits for a slow deferred value.
    await new Promise((resolve, reject) => {
      const timer = setTimeout(resolve, 300);
      signal?.addEventListener('abort', () => {
        clearTimeout(timer);
        reject(signal.reason);
      });
    });
    const done = new PassThrough();
    done.end(`<html>all ready waitForAll=${waitForAll}</html>`);
    return done;
  }
  const output = new PassThrough();
  if (page === 'deferred') {
    output.write('<html>shell ');
    deferred?.('d0').then(
      (value) => output.end(`${JSON.stringify(value)}</html>`),
      (error) => output.end(`error: ${error.message}</html>`),
    );
    return output;
  }
  if (page === 'stream') {
    output.write(`<html>shell waitForAll=${waitForAll}`);
    const timer = setTimeout(() => output.end(' rest</html>'), 150);
    signal?.addEventListener('abort', () => {
      clearTimeout(timer);
      console.log('STREAM_ABORTED');
      output.destroy(new Error('aborted'));
    });
    return output;
  }
  output.end(await render(data, { signal }));
  return output;
}
