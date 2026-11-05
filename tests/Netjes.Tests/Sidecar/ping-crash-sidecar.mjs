// A sidecar script of an app's own that takes every request for a render: a health ping, a GET without a body, makes
// it exit with code 1, as parsing the JSON that is not there would. It answers every render with its pid.
import http from 'node:http';

const token = process.env.NETJES_TOKEN;
const server = http.createServer(async (req, res) => {
  if (req.headers['x-netjes-token'] !== token) return res.writeHead(403).end();
  if (req.method === 'GET') process.exit(1);
  for await (const _ of req);
  res.writeHead(200, { 'content-type': 'text/html' }).end(`<html>${process.pid}</html>`);
});
server.listen(0, '127.0.0.1', () => {
  process.stdout.write(`NETJES_READY ${JSON.stringify({ port: server.address().port, viteOrigin: null })}\n`);
});
process.stdin.on('end', () => process.exit(0));
process.stdin.resume();
