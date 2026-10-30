import http from 'node:http';

/**
 * Starts a test server on 127.0.0.1. BENCH_TEST_PORT_BASE puts each test's server on base + `offset` (offsets are
 * unique across the test files, which run in parallel); without it the system picks a free port.
 * @param {http.RequestListener} handler
 * @param {number} offset
 * @returns {Promise<{ server: http.Server, url: URL, close: () => Promise<void> }>}
 */
export async function startServer(handler, offset) {
  const base = Number(process.env.BENCH_TEST_PORT_BASE ?? '');
  const port = Number.isInteger(base) && base > 0 ? base + offset : 0;
  const server = http.createServer(handler);
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, '127.0.0.1', () => resolve(undefined));
  });
  const address = /** @type {import('node:net').AddressInfo} */ (server.address());
  return {
    server,
    url: new URL(`http://127.0.0.1:${address.port}/`),
    close: () =>
      new Promise((resolve) => {
        server.closeAllConnections();
        server.close(() => resolve());
      }),
  };
}
