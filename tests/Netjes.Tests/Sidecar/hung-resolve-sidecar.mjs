// A sidecar whose /resolve never answers (a hung process); renders wait for d0 until their own stream timeout.
import http from 'node:http';

const token = process.env.NETJES_TOKEN;
const streamTimeoutMs = Number(process.env.NETJES_STREAM_TIMEOUT_MS);
const server = http.createServer(async (req, res) => {
  if (req.headers['x-netjes-token'] !== token) return res.writeHead(403).end();
  // Only /resolve hangs: the process answers health pings, before any body is read, as server.mjs does.
  if (req.method === 'GET' && req.url === '/health') return res.writeHead(200).end('ok');
  for await (const _ of req);
  if (req.url === '/resolve') return;
  res.writeHead(200, { 'content-type': 'text/html' });
  res.write('<html>shell ');
  setTimeout(() => res.end('d0=TIMED-OUT</html>'), streamTimeoutMs);
});
server.listen(0, '127.0.0.1', () => {
  process.stdout.write(`NETJES_READY ${JSON.stringify({ port: server.address().port, viteOrigin: null })}\n`);
});
process.stdin.on('end', () => process.exit(0));
process.stdin.resume();
