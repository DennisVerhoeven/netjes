// A sidecar script of an app's own that knows only renders, and answers anything else with a 404: a health ping too,
// before any body is read. It answers a render with its pid and the number of 404s it has sent.
import http from 'node:http';

const token = process.env.NETJES_TOKEN;
let notFound = 0;
const server = http.createServer(async (req, res) => {
  if (req.headers['x-netjes-token'] !== token) return res.writeHead(403).end();
  if (req.method !== 'POST' || req.url !== '/render') {
    notFound++;
    return res.writeHead(404).end();
  }
  for await (const _ of req);
  res.writeHead(200, { 'content-type': 'text/html' }).end(`<html>${process.pid} ${notFound}</html>`);
});
server.listen(0, '127.0.0.1', () => {
  process.stdout.write(`NETJES_READY ${JSON.stringify({ port: server.address().port, viteOrigin: null })}\n`);
});
process.stdin.on('end', () => process.exit(0));
process.stdin.resume();
