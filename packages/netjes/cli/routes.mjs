// @ts-check

/**
 * @typedef {{ pattern: string; file: string; loader: boolean; action: boolean; props: string | null; cached: boolean; authorized: boolean }} RouteRow
 * @typedef {{ method: string; route: string }} EndpointRow
 * @typedef {{ kind: string; message: string; files: string[] }} RouteWarning
 */

/** @param {string[][]} rows */
function table(rows) {
  const widths = rows[0]?.map((_, column) => Math.max(...rows.map((row) => row[column]?.length ?? 0))) ?? [];
  return rows.map((row) => row.map((cell, column) => (column === row.length - 1 ? cell : cell.padEnd(widths[column] ?? 0))).join('  ').trimEnd());
}

const yes = (/** @type {boolean} */ value) => (value ? 'yes' : '');

/**
 * The route table the app reported (`NETJES_COMMAND=routes`), as text, with the app's route warnings after it.
 * @param {{ routes: RouteRow[]; endpoints: EndpointRow[]; warnings?: RouteWarning[] }} report
 */
export function formatRoutes(report) {
  const pages = table([
    ['Pattern', 'File', 'Loader', 'Action', 'Props', 'Cached', 'Auth'],
    ...report.routes.map((r) => [r.pattern, r.file, yes(r.loader), yes(r.action), r.props ?? '', yes(r.cached), yes(r.authorized)]),
  ]);
  const endpoints = report.endpoints.length
    ? ['', 'Endpoints', ...table(report.endpoints.map((e) => [e.method, e.route]))]
    : [];
  const warnings = report.warnings?.length ? ['', 'Warnings', ...report.warnings.map((w) => `  ${w.message}`)] : [];
  return [...pages, ...endpoints, ...warnings].join('\n') + '\n';
}
