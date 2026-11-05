// A sidecar script of an app's own that takes every request for a render: a health ping, a GET without a body, makes
// it exit with code 1, as parsing the JSON that is not there would. Renders it answers.
import http from 'node:http';

const token = process.env.NETJES_TOKEN;
const server = http.createServer(async (req, res) => {
  if (req.headers['x-netjes-token'] !== token) return res.writeHead(403).end();
  if (req.method === 'GET') process.exit(1);
  let body = '';
  for await (const chunk of req) body += chunk;
  res.writeHead(200, { 'content-type': 'text/html' }).end(`<html>${JSON.parse(body).page}</html>`);
});
server.listen(0, '127.0.0.1', () => {
  process.stdout.write(`NETJES_READY ${JSON.stringify({ port: server.address().port, viteOrigin: null })}\n`);
});
process.stdin.on('end', () => process.exit(0));
process.stdin.resume();
