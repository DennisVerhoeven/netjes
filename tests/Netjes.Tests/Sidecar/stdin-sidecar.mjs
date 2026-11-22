// Speaks the sidecar protocol (see packages/netjes/sidecar/server.mjs) without React or Vite, and prints what the
// parent writes on its stdin: each line as `stdin: <line>`, then `stdin ended` when it closes, before it exits.
import http from 'node:http';
import { createInterface } from 'node:readline';

const token = process.env.NETJES_TOKEN;
const server = http.createServer(async (req, res) => {
  if (req.headers['x-netjes-token'] !== token) return res.writeHead(403).end();
  if (req.method === 'GET' && req.url === '/health') return res.writeHead(200).end('ok');
  for await (const _ of req);
  res.writeHead(200, { 'content-type': 'text/html' }).end('<html>page</html>');
});
server.listen(0, '127.0.0.1', () => {
  process.stdout.write(`NETJES_READY ${JSON.stringify({ port: server.address().port, viteOrigin: null })}\n`);
});
createInterface({ input: process.stdin })
  .on('line', (line) => console.log(`stdin: ${line}`))
  .on('close', () => process.stdout.write('stdin ended\n', () => process.exit(0)));
