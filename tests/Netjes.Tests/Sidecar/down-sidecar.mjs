// A sidecar that is down for good: the first process of an app reports ready, so the app starts, and exits at once;
// its restarts never report ready. The marker in the app's root tells them apart.
import { existsSync, writeFileSync } from 'node:fs';
import http from 'node:http';
import path from 'node:path';

const marker = path.join(process.env.NETJES_ROOT ?? process.cwd(), '.sidecar-down');
process.stdin.on('end', () => process.exit(0));
process.stdin.resume();
if (!existsSync(marker)) {
  writeFileSync(marker, '');
  const server = http.createServer((_req, res) => res.writeHead(500).end());
  server.listen(0, '127.0.0.1', () => {
    const address = /** @type {import('node:net').AddressInfo} */ (server.address());
    process.stdout.write(`NETJES_READY ${JSON.stringify({ port: address.port, viteOrigin: null })}\n`, () => process.exit(1));
  });
}
