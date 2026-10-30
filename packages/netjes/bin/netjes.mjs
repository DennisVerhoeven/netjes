#!/usr/bin/env node
// @ts-check
import { run } from '../cli/main.mjs';
import { processIo } from '../cli/process-io.mjs';

// A reader that goes away early (`netjes help | head -1`) is not an error: what netjes writes after that is dropped.
for (const stream of [process.stdout, process.stderr]) {
  stream.on('error', (error) => {
    if (/** @type {NodeJS.ErrnoException} */ (error).code !== 'EPIPE') throw error;
  });
}

process.exitCode = await run(process.argv.slice(2), processIo());
