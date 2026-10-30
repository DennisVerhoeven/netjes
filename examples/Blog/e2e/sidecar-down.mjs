// @ts-check
// For the e2e suite's "fallback" project only, which points Netjes:SidecarScript here: a sidecar that is down for good.
// The first process an app starts reports ready, so the app starts, and exits at once; its restarts never report
// ready. Every document request then finds no sidecar ready, and gets the shell that renders in the browser.
import { createHash } from 'node:crypto';
import { existsSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import http from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

// One marker per app run, named after the app's process and its sidecar token, which is new for each run and the same
// for each restart (hashed: it is a secret).
const prefix = 'netjes-e2e-sidecar-down-';
const run = createHash('sha256').update(process.env.NETJES_TOKEN ?? '').digest('hex').slice(0, 16);
const marker = join(tmpdir(), `${prefix}${process.ppid}-${run}`);

/** Whether a process with this id is running. */
function running(/** @type {number} */ pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return /** @type {NodeJS.ErrnoException} */ (error).code === 'EPERM';
  }
}

if (existsSync(marker)) {
  // A restart: stays up, never ready, until the app stops (stdin closes).
  process.stdin.on('end', () => {
    rmSync(marker, { force: true });
    process.exit(0);
  });
  process.stdin.resume();
} else {
  // An app stopped with a kill takes its sidecar along before that sees its stdin close: its marker stays behind.
  for (const name of readdirSync(tmpdir())) {
    if (name.startsWith(prefix) && !running(Number(name.slice(prefix.length).split('-')[0]))) rmSync(join(tmpdir(), name), { force: true });
  }
  writeFileSync(marker, '');
  const server = http.createServer((_req, res) => res.writeHead(503).end());
  server.listen(0, '127.0.0.1', () => {
    const address = /** @type {import('node:net').AddressInfo} */ (server.address());
    process.stdout.write(`NETJES_READY ${JSON.stringify({ port: address.port, viteOrigin: null })}\n`, () => process.exit(1));
  });
}
