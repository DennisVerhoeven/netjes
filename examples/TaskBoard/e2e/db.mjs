// @ts-check
// A throwaway Postgres for the end-to-end suite, on 127.0.0.1:E2E_DB_PORT (default 8432). Each Playwright web server runs this before
// starting the app (both may at once, so it is idempotent); global-teardown.mjs removes the container.
import { execFileSync } from 'node:child_process';
import { containerName, dbPort } from './db-config.mjs';

const PORT = dbPort();
const CONTAINER = containerName(PORT);
const IMAGE = 'postgres:17.11-alpine';

/** @param {string[]} args */
function docker(args) {
  return execFileSync('docker', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
}

function running() {
  try {
    return docker(['inspect', '-f', '{{.State.Running}}', CONTAINER]) === 'true';
  } catch {
    return false;
  }
}

if (!running()) {
  try {
    docker([
      'run', '-d', '--rm', '--name', CONTAINER, '-p', `127.0.0.1:${PORT}:5432`,
      '-e', 'POSTGRES_USER=taskboard', '-e', 'POSTGRES_PASSWORD=e2e', '-e', 'POSTGRES_DB=taskboard', IMAGE,
    ]);
  } catch (error) {
    // The other web server started it a moment ago.
    if (!running()) throw error;
  }
}

for (let attempt = 0; ; attempt++) {
  try {
    docker(['exec', CONTAINER, 'pg_isready', '-U', 'taskboard', '-h', '127.0.0.1']);
    break;
  } catch (error) {
    if (attempt > 60) throw error;
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
}
