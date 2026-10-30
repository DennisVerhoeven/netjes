// @ts-check
import { execFileSync } from 'node:child_process';
import { containerName, dbPort } from './db-config.mjs';

/** Removes the suite's Postgres (started by db.mjs). */
export default function teardown() {
  try {
    execFileSync('docker', ['rm', '-f', containerName(dbPort())], { stdio: 'ignore' });
  } catch {
    // Already gone.
  }
}
