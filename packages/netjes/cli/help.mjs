// @ts-check
// Help text, built from the command table so it cannot drift from what the parser accepts.

/**
 * @typedef {import('./commands.mjs').Command} Command
 * @typedef {import('./commands.mjs').OptionSpec} OptionSpec
 */

export const DOCS_URL = 'https://github.com/DennisVerhoeven/netjes/blob/main/docs/reference/cli.md';

/**
 * Options every command takes.
 * @type {{ cwd: OptionSpec; help: OptionSpec; version: OptionSpec }}
 */
export const GLOBAL_OPTIONS = {
  cwd: { type: 'string', value: '<dir>', description: 'Run in another directory', default: 'the current directory' },
  help: { type: 'boolean', short: 'h', description: 'Show help' },
  version: { type: 'boolean', short: 'v', description: 'Print the netjes version' },
};

/** @param {string} name @param {OptionSpec} option */
const label = (name, option) => `${option.short ? `-${option.short}, ` : ''}--${name}${option.value ? ` ${option.value}` : ''}`;

/**
 * The description, then the default as a sentence of its own: descriptions carry parentheses themselves, and a default
 * in parentheses after them would read as two in a row.
 * @param {{ description: string; default?: string }} option
 */
const describe = (option) => `${option.description}${option.default ? `. Default: ${option.default}` : ''}`;

/** @param {[string, string][]} rows */
function columns(rows) {
  const width = Math.max(14, ...rows.map(([left]) => left.length)) + 2;
  return rows.map(([left, right]) => `  ${left.padEnd(width)}${right}`.trimEnd()).join('\n');
}

/** @param {string} name @param {Command} command */
const usage = (name, command) => `netjes ${name}${command.args ? ` ${command.args}` : ''}`;

/**
 * The overview: every command and the global options.
 * @param {Record<string, Command>} commands
 * @param {string} version
 */
export function overviewHelp(commands, version) {
  return [
    `netjes ${version}: React apps and sites on ASP.NET Core`,
    '',
    'Usage: netjes <command> [options]',
    '',
    'Commands:',
    columns(Object.entries(commands).map(([name, command]) => [usage(name, command).slice('netjes '.length), command.summary])),
    '',
    'Options:',
    columns(Object.entries(GLOBAL_OPTIONS).map(([name, option]) => [label(name, option), describe(option)])),
    '',
    'Run `netjes help <command>` for its options and examples.',
    `Docs: ${DOCS_URL}`,
    '',
  ].join('\n');
}

/**
 * One command: its usage, arguments, options with their defaults, and examples.
 * @param {string} name
 * @param {Command} command
 */
export function commandHelp(name, command) {
  const options = { ...command.options, cwd: GLOBAL_OPTIONS.cwd };
  return [
    command.summary,
    ...(command.details ? ['', command.details] : []),
    '',
    `Usage: ${usage(name, command)} [options]`,
    ...(command.arguments
      ? ['', 'Arguments:', columns(Object.entries(command.arguments).map(([arg, spec]) => [arg, describe(spec)]))]
      : []),
    '',
    'Options:',
    columns([
      ...Object.entries(options).map(([option, spec]) => /** @type {[string, string]} */ ([label(option, spec), describe(spec)])),
      [label('help', GLOBAL_OPTIONS.help), 'Show this help'],
    ]),
    '',
    'Examples:',
    ...command.examples.map((example) => `  ${example}`),
    '',
  ].join('\n');
}
