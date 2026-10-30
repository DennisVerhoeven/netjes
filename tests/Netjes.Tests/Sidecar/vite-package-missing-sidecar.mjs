// What the sidecar does in development when vite.config imports a package that is not installed (see
// packages/netjes/sidecar/server.mjs).
const message = "Cannot find package 'some-plugin' imported from /app/node_modules/.vite-temp/vite.config.ts.timestamp-1.mjs";
const failed = { reason: 'vite-failed', message, code: 'ERR_MODULE_NOT_FOUND' };
process.stdout.write(`NETJES_FAILED ${JSON.stringify(failed)}\n`, () => process.exit(1));
