import { readFile } from 'node:fs/promises';
import { USER_AGENT } from './load.mjs';

/**
 * What a route's answer must look like before it is measured: a benchmark of an error page measures nothing.
 * `contentType` is a prefix of the content-type header; `header` names a header that must be present (for example
 * `age` on a page served from the output cache).
 * @typedef {{ status?: number, contentType?: string, contains?: string, header?: string }} Expect
 * @typedef {{ name: string, path: string, method: string, headers: Record<string, string>, body?: string, expect: Expect }} Route
 * `restart` names the route to load while the renderer is killed, the health path to poll, and the whole body of a
 * healthy answer there.
 * @typedef {{ route: string, health: string | null, healthy: string | null }} Restart
 * @typedef {{ routes: Route[], restart: Restart | null }} RouteList
 */

/**
 * @param {string} file
 * @returns {Promise<RouteList>}
 */
export async function loadRoutes(file) {
  return parseRoutes(JSON.parse(await readFile(file, 'utf8')), file);
}

/**
 * @param {unknown} json
 * @param {string} source
 * @returns {RouteList}
 */
export function parseRoutes(json, source) {
  /** @type {string[]} */
  const problems = [];
  const root = isObject(json) ? json : {};
  const list = Array.isArray(root.routes) ? root.routes : [];
  if (list.length === 0) problems.push('routes must be a non-empty array');

  /** @type {Route[]} */
  const routes = [];
  const names = new Set();
  list.forEach((/** @type {unknown} */ item, i) => {
    const at = `routes[${i}]`;
    const entry = isObject(item) ? item : {};
    if (typeof entry.name !== 'string' || entry.name === '') problems.push(`${at}.name must be a non-empty string`);
    else if (names.has(entry.name)) problems.push(`${at}: "${entry.name}" is listed twice`);
    else names.add(entry.name);
    if (typeof entry.path !== 'string' || !entry.path.startsWith('/')) problems.push(`${at}.path must start with /`);
    if (entry.method !== undefined && typeof entry.method !== 'string') problems.push(`${at}.method must be a string`);
    if (entry.body !== undefined && typeof entry.body !== 'string') problems.push(`${at}.body must be a string`);
    if (entry.headers !== undefined && !isStringMap(entry.headers)) problems.push(`${at}.headers must map names to strings`);
    const expect = entry.expect === undefined ? {} : isObject(entry.expect) ? entry.expect : null;
    if (expect === null) problems.push(`${at}.expect must be an object`);
    else {
      if (expect.status !== undefined && typeof expect.status !== 'number') problems.push(`${at}.expect.status must be a number`);
      for (const key of ['contentType', 'contains', 'header']) {
        if (expect[key] !== undefined && typeof expect[key] !== 'string') problems.push(`${at}.expect.${key} must be a string`);
      }
    }
    routes.push({
      name: /** @type {string} */ (entry.name),
      path: /** @type {string} */ (entry.path),
      method: typeof entry.method === 'string' ? entry.method.toUpperCase() : 'GET',
      headers: isStringMap(entry.headers) ? { ...entry.headers } : {},
      ...(typeof entry.body === 'string' ? { body: entry.body } : {}),
      expect: /** @type {Expect} */ (expect ?? {}),
    });
  });

  /** @type {Restart | null} */
  let restart = null;
  if (root.restart !== undefined) {
    const entry = isObject(root.restart) ? root.restart : {};
    if (typeof entry.route !== 'string' || !names.has(entry.route)) {
      problems.push(`restart.route "${String(entry.route)}" is not one of the routes`);
    }
    if (entry.health !== undefined && (typeof entry.health !== 'string' || !entry.health.startsWith('/'))) {
      problems.push('restart.health must be a path starting with /');
    }
    if (entry.healthy !== undefined && typeof entry.healthy !== 'string') problems.push('restart.healthy must be a string');
    restart = {
      route: /** @type {string} */ (entry.route),
      health: typeof entry.health === 'string' ? entry.health : null,
      healthy: typeof entry.healthy === 'string' ? entry.healthy : null,
    };
  }

  if (problems.length > 0) throw new Error(`${source}: ${problems.join('; ')}`);
  return { routes, restart };
}

/**
 * The URL of a path below the target, which may itself have a path (a base path).
 * @param {URL} target
 * @param {string} path
 */
export function resolvePath(target, path) {
  return new URL(target.pathname.replace(/\/+$/, '') + path, target);
}

/**
 * The request for a route.
 * @param {URL} target
 * @param {Route} route
 * @returns {import('./load.mjs').RequestSpec}
 */
export function toRequest(target, route) {
  return {
    url: resolvePath(target, route.path),
    method: route.method,
    headers: route.headers,
    ...(route.body === undefined ? {} : { body: route.body }),
  };
}

/**
 * Requests a route twice (the first may fill a cache) and checks the second answer against the route's `expect`.
 * @param {URL} target
 * @param {Route} route
 * @returns {Promise<string[]>} what did not match; empty when the route is fine
 */
export async function checkRoute(target, route) {
  const request = toRequest(target, route);
  /** @type {Response | undefined} */
  let response;
  let body = '';
  try {
    for (let i = 0; i < 2; i++) {
      response = await fetch(request.url, {
        method: request.method,
        headers: { 'user-agent': USER_AGENT, ...request.headers },
        body: request.body,
        redirect: 'manual',
      });
      body = await response.text();
    }
  } catch (error) {
    const cause = /** @type {{ cause?: { code?: string } }} */ (error).cause;
    return [`request failed: ${cause?.code ?? /** @type {Error} */ (error).message}`];
  }
  const answer = /** @type {Response} */ (response);
  return answerProblems({ status: answer.status, headers: Object.fromEntries(answer.headers), body }, route.expect);
}

/**
 * What an answer misses of a route's `expect`.
 * @param {{ status: number, headers: Record<string, string | string[] | undefined>, body: string }} answer `headers`
 *   by lower-case name, as Node and the Fetch API give them
 * @param {Expect} expect
 * @returns {string[]} empty when the answer is as expected
 */
export function answerProblems(answer, expect) {
  const { status = 200, contentType, contains, header } = expect;
  /** @type {string[]} */
  const problems = [];
  if (answer.status !== status) problems.push(`status ${answer.status}, expected ${status}`);
  const type = String(answer.headers['content-type'] ?? '');
  if (contentType !== undefined && !type.startsWith(contentType)) {
    problems.push(`content-type ${type || '(none)'}, expected ${contentType}`);
  }
  if (contains !== undefined && !answer.body.includes(contains)) problems.push(`the body does not contain ${JSON.stringify(contains)}`);
  if (header !== undefined && answer.headers[header.toLowerCase()] === undefined) problems.push(`no ${header.toLowerCase()} header`);
  return problems;
}

/**
 * @param {unknown} value
 * @returns {value is Record<string, any>}
 */
function isObject(value) {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/**
 * @param {unknown} value
 * @returns {value is Record<string, string>}
 */
function isStringMap(value) {
  return isObject(value) && Object.values(value).every((v) => typeof v === 'string');
}
