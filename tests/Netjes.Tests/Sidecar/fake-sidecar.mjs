// Speaks the sidecar protocol (see packages/netjes/sidecar/server.mjs) without React or Vite.
import { spawn } from 'node:child_process';
import { existsSync, writeFileSync } from 'node:fs';
import http from 'node:http';

const token = process.env.NETJES_TOKEN;
/** @type {Map<string, (value: string) => void>} */
const waiting = new Map();
// Like server.mjs: values that arrive before their render are kept for it.
/** @type {Map<string, string>} */
const early = new Map();
const server = http.createServer(async (req, res) => {
  if (req.headers['x-netjes-token'] !== token) {
    res.writeHead(403).end();
    return;
  }
  let body = '';
  for await (const chunk of req) body += chunk;
  if (req.url === '/resolve') {
    const { renderId, id, value } = JSON.parse(body);
    const text = `${id}=${JSON.stringify(value)}`;
    if (waiting.has(renderId)) waiting.get(renderId)(text);
    else early.set(renderId, text);
    res.writeHead(204).end();
    return;
  }
  const payload = JSON.parse(body);
  if (payload.page === 'deferred') {
    res.writeHead(200, { 'content-type': 'text/html' });
    res.write('<html>shell ');
    const renderId = req.headers['x-netjes-render-id'];
    if (early.has(renderId)) res.end(`${early.get(renderId)}</html>`);
    else waiting.set(renderId, (value) => res.end(`${value}</html>`));
    return;
  }
  switch (payload.page) {
    case 'crash':
      process.exit(1);
    case 'exit-slowly':
      // A crash as .NET can see it under load: the port is gone at once, the exit is noticed later. With a marker the
      // page does this once, so a restarted process answers a render that was sent again.
      if (payload.marker && existsSync(payload.marker)) {
        res.writeHead(200, { 'content-type': 'text/html' }).end('<html>sent again</html>');
        return;
      }
      if (payload.marker) writeFileSync(payload.marker, '');
      server.close();
      req.socket.destroy();
      setTimeout(() => process.exit(1), 500);
      return;
    case 'exit-leaving-child':
      // A crash .NET sees at once but handles late: the exit handler waits for the output to end, and the child keeps
      // it open until its stdin (this process's) closes, when .NET lets go of the process. Inherited stdio, so it does
      // the same on Windows.
      spawn(process.execPath, ['-e', 'process.stdin.on("end", () => process.exit(0)); process.stdin.resume();'], { stdio: 'inherit' });
      process.exit(1);
    case 'pause-listening': {
      // Refuses connections for a moment and carries on, as a process with a full accept queue does on Windows. The
      // port closes before the answer goes out, so no new connection can be waiting in its queue when it does.
      const { port } = server.address();
      server.close();
      setTimeout(() => server.listen(port, '127.0.0.1'), 300);
      res.writeHead(200, { 'content-type': 'text/html', connection: 'close' }).end('<html>paused</html>');
      return;
    }
    case 'stop-listening':
      // Refuses connections from now on and keeps running, as above but for good.
      server.close();
      res.writeHead(200, { 'content-type': 'text/html', connection: 'close' }).end('<html>stopped</html>');
      return;
    case 'keep-running':
      // Slow to shut down once stdin closes, as Vite can be in development: stopping waits out its grace and kills the
      // process. Ten seconds is past that grace, and ends the process if nothing does.
      process.stdin.removeAllListeners('end');
      process.stdin.on('end', () => setTimeout(() => process.exit(0), 10_000));
      res.writeHead(200, { 'content-type': 'text/html' }).end(`<html>keeps running ${process.pid}</html>`);
      return;
    case 'fail':
      res.writeHead(500, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ error: { message: 'boom', stack: 'Error: boom\n    at Page (app/fail/page.tsx:1:1)' } }));
      return;
    case 'log-ready':
      // Page code printing lines that look like the handshake must not confuse the parent.
      console.log('NETJES_READY {not json');
      console.log('NETJES_READY {"port":1,"viteOrigin":"http://evil.example"}');
      res.writeHead(200, { 'content-type': 'text/html' });
      res.end('<html>logged</html>');
      return;
    case 'traceparent':
      res.writeHead(200, { 'content-type': 'text/html' });
      res.end(`<html>${req.headers.traceparent ?? 'none'}</html>`);
      return;
    case 'stream-timeout':
      res.writeHead(200, { 'content-type': 'text/html' });
      res.end(`<html>stream-timeout=${process.env.NETJES_STREAM_TIMEOUT_MS}</html>`);
      return;
    case 'stream':
      res.writeHead(200, { 'content-type': 'text/html' });
      res.write(`<html>shell wait=${req.headers['x-netjes-wait-all'] ?? '0'}`);
      setTimeout(() => res.end(' rest</html>'), 1500);
      return;
    case 'pid':
      // Long enough that concurrent renders overlap.
      setTimeout(() => res.writeHead(200, { 'content-type': 'text/html' }).end(`<html>${process.pid}</html>`), 300);
      return;
    case 'slow':
      setTimeout(() => res.writeHead(200).end('<html>slow</html>'), 2000);
      return;
    default:
      res.writeHead(200, { 'content-type': 'text/html' });
      res.end(`<html>${payload.page} ${process.env.NETJES_MODE}</html>`);
  }
});
server.listen(0, '127.0.0.1', () => {
  console.log('starting up');
  process.stdout.write(`NETJES_READY ${JSON.stringify({ port: server.address().port, viteOrigin: null })}\n`);
});
process.stdin.on('end', () => process.exit(0));
process.stdin.resume();
