/**
 * @typedef {import('./stats.mjs').StepSummary & ({ mode: 'open', rate: number } | { mode: 'closed', concurrency: number })} Step
 * @typedef {{ name: string, method: string, path: string, steps: Step[] }} RouteResult
 * A restart under load: `fallback` is `default` for the app as it was started, `off` for the app started again with
 * ClientRenderFallback off.
 * @typedef {import('./restart.mjs').RestartResult & { fallback: 'default' | 'off', route: string, rate: number }} RestartEntry
 * @typedef {{
 *   schema: 2,
 *   title?: string,
 *   startedAt: string,
 *   finishedAt: string,
 *   provisional: boolean,
 *   note: string | null,
 *   target: string,
 *   machine: import('./machine.mjs').Machine,
 *   loadAverageBeforeBuild: number | null,
 *   loadAverageAfter: number[],
 *   runtimes: import('./machine.mjs').Runtimes,
 *   versions: Record<string, string>,
 *   settings: Record<string, string | number | boolean | number[]>,
 *   routes: RouteResult[],
 *   restarts: RestartEntry[],
 *   memory: { idle: import('./memory.mjs').RssByName, peak: Record<string, number>, end: import('./memory.mjs').RssByName } | null,
 *   warnings: string[],
 * }} Results
 */

const MIB = 1024 ** 2;
const GIB = 1024 ** 3;

/**
 * The results as markdown: a heading, the machine and versions, one table per measurement, and the warnings. Its
 * headings start at the third level, so it goes under the Results section of docs/benchmarks.md as it is.
 * @param {Results} results
 * @returns {string}
 */
export function renderMarkdown(results) {
  const { machine } = results;
  const out = [`### ${results.title ?? 'Netjes benchmark'}, ${results.startedAt.slice(0, 16).replace('T', ' ')} UTC`, ''];
  if (results.provisional) {
    out.push(`**PROVISIONAL (machine under load)**${results.note ? `: ${results.note}` : ''}`, '');
  } else if (results.note) {
    out.push(results.note, '');
  }

  const cores = machine.physicalCores === null ? `${machine.logicalCores} logical cores` : `${machine.physicalCores} cores, ${machine.logicalCores} threads`;
  const runtimes = [
    results.runtimes.dotnet && `.NET ${results.runtimes.dotnet}`,
    results.runtimes.aspnetcore && `ASP.NET Core ${results.runtimes.aspnetcore}`,
    results.runtimes.node && `Node ${results.runtimes.node} (app)`,
    `Node ${machine.node} (load generator)`,
  ].filter(Boolean);
  out.push(
    '| | |',
    '|---|---|',
    `| Target | ${results.target} |`,
    `| Machine | ${machine.cpu}, ${cores}, ${(machine.memoryBytes / GIB).toFixed(1)} GiB memory, ${machine.arch} |`,
    `| OS | ${machine.os}, kernel ${machine.kernel} |`,
    results.loadAverageBeforeBuild === null
      ? `| Load average (1 min) | ${fixed2(machine.loadAverage[0])} before, ${fixed2(results.loadAverageAfter[0])} after |`
      : `| Load average (1 min) | ${fixed2(results.loadAverageBeforeBuild)} before the build, ${fixed2(machine.loadAverage[0])} at the start, ${fixed2(results.loadAverageAfter[0])} after |`,
    `| Runtimes | ${runtimes.join(', ')} |`,
  );
  const versions = Object.entries(results.versions);
  if (versions.length > 0) out.push(`| Versions | ${versions.map(([k, v]) => `${k} ${v}`).join(', ')} |`);
  const settings = Object.entries(results.settings);
  if (settings.length > 0) {
    out.push(`| Settings | ${settings.map(([k, v]) => `${k} ${Array.isArray(v) ? v.join('/') : v}`).join(', ')} |`);
  }
  out.push('');

  const open = results.routes.flatMap((r) => r.steps.filter((s) => s.mode === 'open').map((s) => ({ route: r.name, step: s })));
  if (open.length > 0) {
    out.push(
      '#### Latency at fixed arrival rates (open loop)',
      '',
      'Milliseconds from when each request was due. TTFB: until the response headers; total: until the last byte.',
      '',
      '| Route | Rate (req/s) | Achieved (req/s) | TTFB p50 | TTFB p95 | TTFB p99 | Total p50 | Total p95 | Total p99 | Errors |',
      '|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|',
      ...open.map(({ route, step }) =>
        row([
          route,
          String(/** @type {{ rate: number }} */ (step).rate),
          rps(step.throughput),
          ms(step.ttfb.p50),
          ms(step.ttfb.p95),
          ms(step.ttfb.p99),
          ms(step.total.p50),
          ms(step.total.p95),
          ms(step.total.p99),
          errors(step.errors, step.errorKinds),
        ]),
      ),
      '',
    );
  }

  const closed = results.routes.flatMap((r) => r.steps.filter((s) => s.mode === 'closed').map((s) => ({ route: r.name, step: s })));
  if (closed.length > 0) {
    out.push(
      '#### Throughput at fixed concurrency (closed loop)',
      '',
      'Each connection sends its next request when the previous one is answered. Latencies here leave out queueing.',
      '',
      '| Route | Concurrency | Throughput (req/s) | Total p50 | Total p95 | Total p99 | Errors |',
      '|---|--:|--:|--:|--:|--:|--:|',
      ...closed.map(({ route, step }) =>
        row([
          route,
          String(/** @type {{ concurrency: number }} */ (step).concurrency),
          rps(step.throughput),
          ms(step.total.p50),
          ms(step.total.p95),
          ms(step.total.p99),
          errors(step.errors, step.errorKinds),
        ]),
      ),
      '',
    );
  }

  const { restarts } = results;
  if (restarts.length > 0) {
    out.push(
      '#### Sidecar restart under load',
      '',
      'A renderer process killed under open-loop load. Milliseconds from when the kill command returned. A good answer is the page; ' +
        'a fallback shell is the document without it that Netjes sends while no sidecar is ready. ClientRenderFallback: default is ' +
        'the app as it was started (on in production unless the app turns it off), off the app started again with it off.',
      '',
      '| ClientRenderFallback | Route | Rate (req/s) | Healthy again | First good answer | Fallback shells | Failed requests | Total p99 before | Total p99 after | Max after |',
      '|---|---|--:|--:|--:|--:|--:|--:|--:|--:|',
      ...restarts.map((restart) =>
        row([
          restart.fallback,
          restart.route,
          String(restart.rate),
          ms(restart.restartMs),
          ms(restart.firstOkAfterKillMs),
          String(restart.shells.count),
          `${errors(restart.failed, restart.errorKinds)}`.replace(/^(\d+)/, `$1 of ${restart.requests}`),
          ms(restart.before.p99),
          ms(restart.after.p99),
          ms(restart.after.max),
        ]),
      ),
      '',
    );
  }

  const { memory } = results;
  if (memory) {
    const names = [...new Set([...Object.keys(memory.idle), ...Object.keys(memory.peak), ...Object.keys(memory.end)])].sort();
    out.push(
      '#### Memory (RSS, MiB)',
      '',
      '| Process | Idle | Peak under load | After the load |',
      '|---|--:|--:|--:|',
      ...names.map((name) => {
        const count = Math.max(memory.idle[name]?.processes ?? 0, memory.end[name]?.processes ?? 0);
        return row([
          count > 1 ? `${name} × ${count}` : name,
          mib(memory.idle[name]?.rssBytes),
          mib(memory.peak[name]),
          mib(memory.end[name]?.rssBytes),
        ]);
      }),
      '',
    );
  }

  if (results.warnings.length > 0) out.push('#### Warnings', '', ...results.warnings.map((w) => `- ${w}`), '');
  return out.join('\n');
}

/** @param {string[]} cells */
const row = (cells) => `| ${cells.join(' | ')} |`;

/** @param {number | null | undefined} value */
function ms(value) {
  if (value === null || value === undefined) return '–';
  if (value < 10) return value.toFixed(2);
  if (value < 100) return value.toFixed(1);
  return String(Math.round(value));
}

/** @param {number} value */
const rps = (value) => (value < 100 ? value.toFixed(1) : String(Math.round(value)));

/** @param {number | undefined} value */
const fixed2 = (value) => (value === undefined ? '–' : value.toFixed(2));

/** @param {number | undefined} bytes */
const mib = (bytes) => (bytes === undefined ? '–' : (bytes / MIB).toFixed(1));

/**
 * @param {number} count
 * @param {Record<string, number>} kinds
 */
function errors(count, kinds) {
  const entries = Object.entries(kinds);
  return entries.length === 0 ? String(count) : `${count} (${entries.map(([k, n]) => `${k}: ${n}`).join(', ')})`;
}
