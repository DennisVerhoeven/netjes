// What the sidecar does in development when a CommonJS vite.config (vite.config.cjs or .cts, or a vite.config.ts in a
// package that is not "type": "module") requires a package that is not installed (see packages/netjes/sidecar/server.mjs).
const message = "Cannot find module 'some-plugin'\nRequire stack:\n- /app/vite.config.cjs\n- /app/node_modules/vite/dist/node/chunks/node.js";
const failed = { reason: 'vite-failed', message, code: 'MODULE_NOT_FOUND' };
process.stdout.write(`NETJES_FAILED ${JSON.stringify(failed)}\n`, () => process.exit(1));
