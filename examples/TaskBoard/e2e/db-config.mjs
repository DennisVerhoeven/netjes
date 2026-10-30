// @ts-check
// Where the end-to-end suite's throwaway Postgres lives. Shared by the Playwright config, db.mjs and
// global-teardown.mjs, so a run on another E2E_DB_PORT gets its own container and never touches another run's.
export const DEFAULT_DB_PORT = 8432;

/** @param {Record<string, string | undefined>} [env] */
export function dbPort(env = process.env) {
  const raw = env.E2E_DB_PORT;
  if (raw === undefined || raw === '') return DEFAULT_DB_PORT;
  const port = Number(raw);
  if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error(`E2E_DB_PORT must be a port number, got "${raw}".`);
  return port;
}

/** @param {number} port */
export function containerName(port) {
  return port === DEFAULT_DB_PORT ? 'taskboard-e2e-db' : `taskboard-e2e-db-${port}`;
}
