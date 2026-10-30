// @ts-check
// The netjes command line: parses the arguments and runs the command from the table in commands.mjs.
import { readdirSync, statSync } from 'node:fs';
import path from 'node:path';
import { parseArgs } from 'node:util';
import { COMMANDS } from './commands.mjs';
import { GLOBAL_OPTIONS, commandHelp, overviewHelp } from './help.mjs';
import { packageVersion } from './scaffold.mjs';
import { suggest } from './suggest.mjs';
import { Interrupted, printError } from './terminal.mjs';

/**
 * A process to run, with the terminal's stdin and stderr. Its stdout is the terminal's too, or with `stdout: 'stderr'`
 * the CLI's stderr, for a command whose own output on stdout is data. `prompt`, there only with a terminal to ask in,
 * resolves to null when no answer comes and rejects with Interrupted on Ctrl+C.
 * @typedef {{ command: string; args: string[]; cwd: string; env: Record<string, string | undefined>; stdout?: 'stderr' }} SpawnCall
 * @typedef {{
 *   cwd: string;
 *   env: Record<string, string | undefined>;
 *   stdout(text: string): void;
 *   stderr(text: string): void;
 *   spawn(call: SpawnCall): Promise<number>;
 *   capture?(call: SpawnCall): Promise<{ code: number; stdout: string; stderr: string }>;
 *   color?: { stdout: boolean; stderr: boolean };
 *   prompt?(question: string): Promise<string | null>;
 *   portFree?(port: number, host?: string): Promise<boolean>;
 * }} CliIo
 * @typedef {import('./commands.mjs').Command} Command
 * @typedef {import('./commands.mjs').OptionSpec} OptionSpec
 */

/** Words people try for a command that is called something else here. Suggestions only, never aliases. */
const MEANT = new Map([
  ['doctor', 'info'],
  ['init', 'new'],
  ['create', 'new'],
  ['serve', 'start'],
  ['version', '--version'],
]);

/** A mistake in the arguments: reported as one line on stderr, with exit code 1. */
class UsageError extends Error {}

/** @param {string | undefined} name */
const commandNamed = (name) => (name !== undefined && Object.hasOwn(COMMANDS, name) ? COMMANDS[name] : undefined);

/**
 * parseArgs options for these specs; never strict, so that each mistake gets a message of our own.
 * @param {Record<string, OptionSpec>} specs
 * @param {string[]} args
 */
function parse(specs, args) {
  /** @type {Record<string, { type: 'string' | 'boolean'; short?: string }>} */
  const options = Object.fromEntries(
    Object.entries(specs).map(([name, { type, short }]) => [name, short ? { type, short } : { type }]),
  );
  return parseArgs({ args, options, allowPositionals: true, strict: false, tokens: true });
}

/**
 * The options and arguments a command was given, or a UsageError for the first mistake in them. With `helping`, the
 * arguments are `help [<command>]`, and the command's options are accepted as they are next to --help.
 * @param {string[]} argv
 * @param {string | undefined} name
 * @param {Command | undefined} command
 * @param {boolean} helping
 */
function validate(argv, name, command, helping = false) {
  /** @type {Record<string, OptionSpec>} */
  const allowed = { ...GLOBAL_OPTIONS, ...command?.options };
  const own = name !== undefined && !helping;
  const prefix = helping ? 'netjes help' : own ? `netjes ${name}` : 'netjes';
  const helpCommand = own ? `netjes help ${name}` : 'netjes --help';
  const { values, positionals, tokens } = parse(allowed, argv);
  const accepts = helping ? 1 : Object.keys(command?.arguments ?? {}).length;
  // The first positional is the command itself (or help); `accepts` more may follow it.
  let seen = 0;
  for (const token of tokens) {
    if (token.kind === 'positional') {
      if (seen++ > accepts) throw new UsageError(`${prefix}: unexpected argument '${token.value}'; run \`${helpCommand}\` for its usage`);
    } else if (token.kind === 'option') {
      const spec = Object.hasOwn(allowed, token.name) ? allowed[token.name] : undefined;
      if (!spec) {
        if (own && (name === 'dev' || name === 'start') && (token.name === 'port' || token.name === 'p')) {
          // The number the user typed (`--port 3000`, `--port=3000`, `-p3000`), else a placeholder.
          const typed = [token.value, /^-p(\d+)$/.exec(argv[token.index] ?? '')?.[1], argv[token.index + 1]];
          const port = typed.find((value) => value !== undefined && /^\d+$/.test(value)) ?? '<port>';
          throw new UsageError(
            `${prefix}: unknown option '${token.rawName}'; netjes has no port option, the app's URL sets it: ` +
              `set ASPNETCORE_URLS=http://localhost:${port} when you run netjes ${name}`,
          );
        }
        const meant = token.rawName.startsWith('--') ? suggest(token.name, Object.keys(allowed)) : undefined;
        throw new UsageError(
          `${prefix}: unknown option '${token.rawName}'; ` +
            (meant ? `did you mean '--${meant}'?` : `run \`${helpCommand}\` for ${own ? 'its' : 'the'} options`),
        );
      }
      if (spec.type === 'string' && (token.value === undefined || (!token.inlineValue && token.value.startsWith('-')))) {
        throw new UsageError(`${prefix}: option '--${token.name}' needs a value: --${token.name} ${spec.value ?? '<value>'}`);
      }
      if (spec.type === 'boolean' && token.value !== undefined) {
        throw new UsageError(`${prefix}: option '--${token.name}' takes no value`);
      }
    }
  }
  return { values, args: positionals.slice(1) };
}

/**
 * @param {string[]} argv
 * @param {CliIo} io
 * @returns {Promise<number>}
 */
export async function run(argv, io) {
  try {
    return await dispatch(argv, io);
  } catch (error) {
    if (error instanceof Interrupted) return 130;
    if (!(error instanceof UsageError)) throw error;
    printError(io, error.message);
    return 1;
  }
}

/**
 * @param {string[]} argv
 * @param {CliIo} io
 * @returns {Promise<number>}
 */
async function dispatch(argv, io) {
  // A first pass, knowing every option's type so that a value (--name Shop) is not taken for the command.
  const every = Object.assign({}, GLOBAL_OPTIONS, ...Object.values(COMMANDS).map((command) => command.options));
  const first = parse(every, argv);
  if (first.values.version) {
    io.stdout(`${await packageVersion()}\n`);
    return 0;
  }

  const [word, ...rest] = first.positionals;
  const helping = word === 'help';
  // `netjes help help` is the overview, as `netjes help` is.
  const name = helping ? (rest[0] === 'help' ? undefined : rest[0]) : word;
  const command = commandNamed(name);
  if (name !== undefined && !command) {
    const meant = MEANT.get(name.toLowerCase()) ?? suggest(name, Object.keys(COMMANDS));
    throw new UsageError(
      `netjes: unknown command '${name}'; ` + (meant ? `did you mean \`netjes ${meant}\`?` : 'run `netjes --help` for the commands'),
    );
  }

  const { values, args } = validate(argv, name, command, helping);
  if (helping || values.help || !command || !name) {
    io.stdout(command && name ? commandHelp(name, command) : overviewHelp(COMMANDS, await packageVersion()));
    return 0;
  }
  const cwd = typeof values.cwd === 'string' ? path.resolve(io.cwd, values.cwd) : io.cwd;
  if (typeof values.cwd === 'string' && !statSync(cwd, { throwIfNoEntry: false })?.isDirectory()) {
    throw new UsageError(`netjes ${name}: --cwd ${values.cwd} is not a directory (${cwd})`);
  }
  if (command.project?.(values) && !readdirSync(cwd).some((entry) => entry.endsWith('.csproj'))) {
    throw new UsageError(
      `netjes ${name}: no .csproj in ${cwd}; run it in the app's folder, pass --cwd <dir>, or create an app with \`netjes new <dir>\``,
    );
  }
  return command.run({ io, cwd, args, options: values });
}
