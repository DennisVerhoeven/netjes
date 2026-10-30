import assert from 'node:assert/strict';
import { test } from 'node:test';
import { renderMarkdown } from '../lib/report.mjs';
import { results } from './fixtures.mjs';

test('a provisional run says so under its heading, with its note', () => {
  const md = renderMarkdown(results());
  const lines = md.split('\n');
  // Third-level headings, so the markdown goes under the Results section of docs/benchmarks.md as it is.
  assert.equal(lines[0], '### Netjes benchmark, 2026-10-29 16:20 UTC');
  assert.ok(lines.includes('#### Latency at fixed arrival rates (open loop)'));
  assert.ok(lines.includes('**PROVISIONAL (machine under load)**: smoke run'), md);
  const quiet = renderMarkdown({ ...results(), provisional: false, note: null });
  assert.ok(!quiet.includes('PROVISIONAL'));
});

test('the machine, runtimes, versions and settings come first', () => {
  const md = renderMarkdown(results());
  assert.ok(md.includes('| Machine | Example CPU, 10 cores, 16 threads, 32.0 GiB memory, x64 |'), md);
  assert.ok(md.includes('| OS | Example Linux 1, kernel 7.2.7 |'));
  assert.ok(md.includes('| Load average (1 min) | 2.50 before, 3.25 after |'));
  const built = renderMarkdown({ ...results(), loadAverageBeforeBuild: 0.4 });
  assert.ok(built.includes('| Load average (1 min) | 0.40 before the build, 2.50 at the start, 3.25 after |'), built);
  assert.ok(md.includes('| Runtimes | .NET 10.0.3, ASP.NET Core 10.0.3, Node v26.12.0 (app), Node v26.12.0 (load generator) |'));
  assert.ok(md.includes('| Versions | netjes 0.1.0 (abc1234), react 19.3.1 |'));
  assert.ok(md.includes('| Settings | warmupSeconds 10, stepSeconds 15, sidecars 1 |'));
});

test('open-loop steps give latency, closed-loop steps throughput', () => {
  const md = renderMarkdown(results());
  assert.ok(md.includes('| Route | Rate (req/s) | Achieved (req/s) | TTFB p50 | TTFB p95 | TTFB p99 | Total p50 | Total p95 | Total p99 | Errors |'), md);
  // Under 10 ms two decimals, under 100 one, above that whole milliseconds.
  assert.ok(md.includes('| document | 50 | 49.8 | 1.23 | 2.50 | 3.75 | 1.50 | 12.3 | 146 | 0 |'), md);
  assert.ok(md.includes('| Route | Concurrency | Throughput (req/s) | Total p50 | Total p95 | Total p99 | Errors |'));
  assert.ok(md.includes('| document | 8 | 1234 | 1.50 | 12.3 | 146 | 2 (500: 2) |'), md);
});

test('the restart and memory get tables of their own, and warnings a list', () => {
  const md = renderMarkdown(results());
  assert.ok(
    md.includes(
      '| ClientRenderFallback | Route | Rate (req/s) | Healthy again | First good answer | Fallback shells | Failed requests | Total p99 before | Total p99 after | Max after |',
    ),
    md,
  );
  // One row for the app as started, one for the app started again with the fallback off.
  assert.ok(md.includes('| default | document | 50 | 1235 | 1250 | 27 | 1 of 650 (500: 1) | 4.00 | 1100 | 1260 |'), md);
  assert.ok(md.includes('| off | document | 50 | 1180 | 1300 | 0 | 2 of 650 (500: 2) | 5.00 | 1290 | 1310 |'), md);
  assert.ok(md.includes('| Process | Idle | Peak under load | After the load |'));
  assert.ok(md.includes('| dotnet | 80.0 | 150.5 | 140.0 |'), md);
  assert.ok(md.includes('| node | 60.0 | 90.0 | 85.0 |'), md);
  assert.ok(md.includes('- the machine was busy'));
});

test('what was not measured is left out', () => {
  const md = renderMarkdown({ ...results(), restarts: [], memory: null, warnings: [], runtimes: {} });
  assert.ok(!md.includes('restart under load'));
  assert.ok(!md.includes('Memory'));
  assert.ok(!md.includes('Warnings'));
  assert.ok(md.includes('| Runtimes | Node v26.12.0 (load generator) |'));
});
