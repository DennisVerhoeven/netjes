// @ts-check
// Colour for a terminal that wants it: never in pipes or files, never with NO_COLOR (https://no-color.org).

/**
 * Whether to colour a stream: NO_COLOR turns it off, FORCE_COLOR on (or off with 0 or false), a dumb terminal off;
 * otherwise only when the stream is a terminal.
 * @param {Record<string, string | undefined>} env
 * @param {boolean | undefined} tty
 */
export function colorSupport(env, tty) {
  if (env.NO_COLOR) return false;
  const force = env.FORCE_COLOR;
  if (force !== undefined && force !== '') return force !== '0' && force !== 'false';
  if (env.TERM === 'dumb') return false;
  return tty === true;
}

/**
 * Styles that only apply when `on`.
 * @param {boolean} on
 */
export function paint(on) {
  /** @param {number} open @param {number} close */
  const style = (open, close) => (/** @type {string} */ text) => (on ? `\u001b[${open}m${text}\u001b[${close}m` : text);
  return { bold: style(1, 22), dim: style(2, 22), red: style(31, 39), green: style(32, 39), yellow: style(33, 39), cyan: style(36, 39) };
}

/**
 * Writes one message to stderr, its `netjes …:` label in red when stderr takes colour.
 * @param {import('./main.mjs').CliIo} io
 * @param {string} message
 */
export function printError(io, message) {
  const colon = message.indexOf(': ');
  const { red } = paint(io.color?.stderr === true);
  io.stderr(colon > 0 ? `${red(message.slice(0, colon + 1))}${message.slice(colon + 1)}\n` : `${red(message)}\n`);
}

/** Ctrl+C at a prompt. The command stops there, with exit code 130, as a shell reports SIGINT. */
export class Interrupted extends Error {}
