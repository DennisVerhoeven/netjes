// What the sidecar does in development when the Vite dev server's port is taken (see packages/netjes/sidecar/server.mjs).
process.stdout.write('NETJES_FAILED {"reason":"vite-port-in-use","port":5173}\n', () => process.exit(1));
