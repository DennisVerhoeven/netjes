// Like packages/netjes/sidecar/server.mjs for /resolve: values that arrive before their render are kept for it.
import http from 'node:http';

const token = process.env.NETJES_TOKEN;
const streamTimeoutMs = Number(process.env.NETJES_STREAM_TIMEOUT_MS);
/** @type {Map<string, { value?: string, waiter?: (v: string) => void }>} */
const values = new Map();
function entry(key) {
  let e = values.get(key);
  if (!e) values.set(key, (e = {}));
  return e;
}
const server = http.createServer(async (req, res) => {
  if (req.headers['x-netjes-token'] !== token) return res.writeHead(403).end();
  if (req.url === '/health') return res.writeHead(200).end('ok');
  let body = '';
  for await (const chunk of req) body += chunk;
  if (req.url === '/resolve') {
    const { renderId, id, value, error } = JSON.parse(body);
    const e = entry(`${renderId}/${id}`);
    e.value = error ?? JSON.stringify(value);
    e.waiter?.(e.value);
    return res.writeHead(204).end();
  }
  const renderId = req.headers['x-netjes-render-id'];
  res.writeHead(200, { 'content-type': 'text/html' });
  res.write('<html>shell ');
  if (!renderId) return res.end('no-deferred</html>');
  const e = entry(`${renderId}/d0`);
  if (e.value !== undefined) return res.end(`d0=${e.value}</html>`);
  const timer = setTimeout(() => res.end('d0=TIMED-OUT</html>'), streamTimeoutMs);
  e.waiter = (v) => { clearTimeout(timer); res.end(`d0=${v}</html>`); };
});
server.listen(0, '127.0.0.1', () => {
  process.stdout.write(`NETJES_READY ${JSON.stringify({ port: server.address().port, viteOrigin: null })}\n`);
});
process.stdin.on('end', () => process.exit(0));
process.stdin.resume();
