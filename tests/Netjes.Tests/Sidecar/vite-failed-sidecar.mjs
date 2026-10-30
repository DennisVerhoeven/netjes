// What the sidecar does in development when the Vite dev server does not start for another reason than its port (see
// packages/netjes/sidecar/server.mjs): here an error in vite.config, which Vite reports with a code frame.
console.error('failed to load config from /app/vite.config.ts');
const message = 'Build failed with 1 error:\n\n[PARSE_ERROR] Unexpected token\n   ╭─[ vite.config.ts:5:20 ]\n 5 │   plugins: [netjes(],\n───╯\n';
process.stdout.write(`NETJES_FAILED ${JSON.stringify({ reason: 'vite-failed', message })}\n`, () => process.exit(1));
