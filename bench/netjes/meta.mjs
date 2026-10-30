#!/usr/bin/env node
// Prints the versions and settings run.sh records with a run of the benchmark app, as JSON for bench.mjs --meta.
// Usage: node meta.mjs --root <repo> --app <app dir> --publish <publish dir> --dotnet-sdk <version> [--setting k=v ...]
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { parseArgs } from 'node:util';
import { gitCommit, packageVersions } from './lib/versions.mjs';

const { values } = parseArgs({
  options: {
    root: { type: 'string' },
    app: { type: 'string' },
    publish: { type: 'string' },
    'dotnet-sdk': { type: 'string' },
    setting: { type: 'string', multiple: true, default: [] },
  },
});
const root = /** @type {string} */ (values.root);
const app = /** @type {string} */ (values.app);

const netjes = JSON.parse(await readFile(path.join(root, 'packages', 'netjes', 'package.json'), 'utf8')).version;
const commit = await gitCommit(root);
const packages = await packageVersions(app, ['react', 'react-dom', 'vite', 'zod', '@tanstack/react-query']);
const runtimeConfig = values.publish
  ? JSON.parse(await readFile(path.join(values.publish, 'NetjesBench.runtimeconfig.json'), 'utf8')).runtimeOptions.configProperties
  : {};
const appSettings = JSON.parse(await readFile(path.join(app, 'appsettings.json'), 'utf8'));

/** @type {Record<string, string | number | boolean>} */
const settings = {
  'server GC': runtimeConfig['System.GC.Server'] === true,
  slowFieldMs: process.env.Bench__SlowFieldMs ? Number(process.env.Bench__SlowFieldMs) : appSettings.Bench.SlowFieldMs,
};
for (const pair of /** @type {string[]} */ (values.setting)) {
  const at = pair.indexOf('=');
  const value = pair.slice(at + 1);
  if (value !== '') settings[pair.slice(0, at)] = /^\d+$/.test(value) ? Number(value) : value;
}

process.stdout.write(
  `${JSON.stringify(
    {
      versions: {
        netjes: commit ? `${netjes} (${commit})` : netjes,
        ...(values['dotnet-sdk'] ? { 'dotnet SDK': values['dotnet-sdk'] } : {}),
        ...packages,
      },
      settings,
    },
    null,
    2,
  )}\n`,
);
