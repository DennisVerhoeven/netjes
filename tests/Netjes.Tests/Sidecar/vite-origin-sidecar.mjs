// A sidecar whose Vite dev server browsers reach at another address than localhost, as server.mjs reports it when
// NETJES_VITE_ORIGIN sets one. It answers health pings, and every render with the same document.
import http from 'node:http';

const token = process.env.NETJES_TOKEN;
const server = http.createServer(async (req, res) => {
  if (req.headers['x-netjes-token'] !== token) return res.writeHead(403).end();
  if (req.method === 'GET' && req.url === '/health') return res.writeHead(200).end('ok');
  for await (const _ of req);
  res.writeHead(200, { 'content-type': 'text/html' }).end('<html>page</html>');
});
server.listen(0, '127.0.0.1', () => {
  const port = /** @type {import('node:net').AddressInfo} */ (server.address()).port;
  process.stdout.write(`NETJES_READY ${JSON.stringify({ port, viteOrigin: 'http://127.0.0.1:5199' })}\n`);
});
process.stdin.on('end', () => process.exit(0));
process.stdin.resume();
