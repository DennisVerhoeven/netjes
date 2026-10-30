/** @param {Partial<import('../lib/stats.mjs').Summary>} [s] */
export const summary = (s = {}) => ({ count: 100, min: 1, mean: 2, p50: 2, p95: 3, p99: 4, max: 5, ...s });

/** @returns {import('../lib/report.mjs').Results} */
export function results() {
  const step = {
    requests: 500,
    ok: 500,
    errors: 0,
    errorKinds: {},
    throughput: 49.8,
    ttfb: summary({ p50: 1.234, p95: 2.5, p99: 3.75 }),
    total: summary({ p50: 1.5, p95: 12.25, p99: 145.6 }),
    sendLag: summary({ p99: 0.2 }),
    maxInFlight: 3,
    generatorBusy: 0.2,
  };
  return {
    schema: 2,
    startedAt: '2026-10-29T16:20:00.000Z',
    finishedAt: '2026-10-29T16:27:00.000Z',
    provisional: true,
    note: 'smoke run',
    target: 'http://127.0.0.1:5480/',
    machine: {
      cpu: 'Example CPU',
      logicalCores: 16,
      physicalCores: 10,
      memoryBytes: 32 * 1024 ** 3,
      os: 'Example Linux 1',
      kernel: '7.2.7',
      arch: 'x64',
      loadAverage: [2.5, 3, 4],
      node: 'v26.12.0',
    },
    loadAverageBeforeBuild: null,
    loadAverageAfter: [3.25, 3, 4],
    runtimes: { dotnet: '10.0.3', aspnetcore: '10.0.3', node: 'v26.12.0' },
    versions: { netjes: '0.1.0 (abc1234)', react: '19.3.1' },
    settings: { warmupSeconds: 10, stepSeconds: 15, sidecars: 1 },
    routes: [
      {
        name: 'document',
        method: 'GET',
        path: '/items',
        steps: [
          { mode: 'open', rate: 50, ...step },
          { mode: 'closed', concurrency: 8, ...step, throughput: 1234.4, errors: 2, errorKinds: { 500: 2 } },
        ],
      },
    ],
    restarts: [
      {
        fallback: 'default',
        route: 'document',
        rate: 50,
        killAtMs: 3000,
        restartMs: 1234.5,
        firstOkAfterKillMs: 1250,
        requests: 650,
        failed: 1,
        errorKinds: { 500: 1 },
        shells: summary({ count: 27, p99: 3.5 }),
        before: summary({ p99: 4 }),
        after: summary({ p99: 1100, max: 1260 }),
      },
      {
        fallback: 'off',
        route: 'document',
        rate: 50,
        killAtMs: 3000,
        restartMs: 1180,
        firstOkAfterKillMs: 1300,
        requests: 650,
        failed: 2,
        errorKinds: { 500: 2 },
        shells: summary({ count: 0, min: null, mean: null, p50: null, p95: null, p99: null, max: null }),
        before: summary({ p99: 5 }),
        after: summary({ p99: 1290, max: 1310 }),
      },
    ],
    memory: {
      idle: { dotnet: { rssBytes: 80 * 1024 ** 2, processes: 1 }, node: { rssBytes: 60 * 1024 ** 2, processes: 1 } },
      peak: { dotnet: 150.5 * 1024 ** 2, node: 90 * 1024 ** 2 },
      end: { dotnet: { rssBytes: 140 * 1024 ** 2, processes: 1 }, node: { rssBytes: 85 * 1024 ** 2, processes: 1 } },
    },
    warnings: ['the machine was busy'],
  };
}
