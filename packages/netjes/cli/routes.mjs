// @ts-check

/**
 * `package` names the page package a route or an endpoint is of, and `overrides` the package's page that the app's file
 * replaces; both are null for the app's own, and absent in the report of a Netjes before page packages.
 * @typedef {{ pattern: string; file: string; package?: string | null; overrides?: string | null; loader: boolean; action: boolean; props: string | null; cached: boolean; authorized: boolean }} RouteRow
 * @typedef {{ method: string; route: string; package?: string | null }} EndpointRow
 * @typedef {{ name: string; version: string; nuget: string; nugetVersion: string; prefix: string; pattern: string; overrides: string[] }} PackageRow
 * @typedef {{ kind: string; message: string; files: string[] }} RouteWarning
 */

/** @param {string[][]} rows */
function table(rows) {
  const widths = rows[0]?.map((_, column) => Math.max(...rows.map((row) => row[column]?.length ?? 0))) ?? [];
  return rows.map((row) => row.map((cell, column) => (column === row.length - 1 ? cell : cell.padEnd(widths[column] ?? 0))).join('  ').trimEnd());
}

const yes = (/** @type {boolean} */ value) => (value ? 'yes' : '');

/** The Package column of a route: its package, marked when the app's file replaces the package's page. */
const packageOf = (/** @type {RouteRow} */ r) => (r.package ? `${r.package}${r.overrides ? ' (override)' : ''}` : '');

/**
 * The route table the app reported (`NETJES_COMMAND=routes`), as text, with the app's route warnings after it. The
 * Package column, and the list of page packages, show only when the app mounts one.
 * @param {{ routes: RouteRow[]; endpoints: EndpointRow[]; packages?: PackageRow[]; warnings?: RouteWarning[] }} report
 */
export function formatRoutes(report) {
  const packages = report.packages ?? [];
  const mounted = packages.length > 0;
  /** @param {string[]} cells @param {string} cell */
  const withPackage = (cells, cell) => (mounted ? [...cells.slice(0, 2), cell, ...cells.slice(2)] : cells);
  const pages = table([
    withPackage(['Pattern', 'File', 'Loader', 'Action', 'Props', 'Cached', 'Auth'], 'Package'),
    ...report.routes.map((r) =>
      withPackage([r.pattern, r.file, yes(r.loader), yes(r.action), r.props ?? '', yes(r.cached), yes(r.authorized)], packageOf(r)),
    ),
  ]);
  const endpoints = report.endpoints.length
    ? ['', 'Endpoints', ...table(report.endpoints.map((e) => (mounted ? [e.method, e.route, e.package ?? ''] : [e.method, e.route])))]
    : [];
  const listed = mounted
    ? [
        '',
        'Packages',
        ...table(
          packages.map((p) => [
            `${p.name} ${p.version}`,
            `${p.nuget} ${p.nugetVersion}`,
            p.prefix,
            p.overrides.length ? `overrides ${p.overrides.join(', ')}` : '',
          ]),
        ),
      ]
    : [];
  const warnings = report.warnings?.length ? ['', 'Warnings', ...report.warnings.map((w) => `  ${w.message}`)] : [];
  return [...pages, ...endpoints, ...listed, ...warnings].join('\n') + '\n';
}
