// @ts-check
// The netjes commands, one entry each. Commands that need the C# side run the app itself: in command mode
// (NETJES_COMMAND=generate|routes|export) it binds a throwaway loopback port, writes its result and exits. Only export
// starts the Node sidecar, to render the pages.
import { existsSync, statSync } from 'node:fs';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { agents } from './agents.mjs';
import { checkProject, checkTools, formatChecks } from './info.mjs';
import { findPackage } from './packages.mjs';
import { formatRoutes } from './routes.mjs';
import { packageVersion, scaffold } from './scaffold.mjs';
import { printError } from './terminal.mjs';

/**
 * @typedef {import('./main.mjs').CliIo} CliIo
 * @typedef {{ io: CliIo; cwd: string; args: string[]; options: Record<string, string | boolean | undefined> }} CommandContext
 * @typedef {{ type: 'string' | 'boolean'; short?: string; value?: string; description: string; default?: string }} OptionSpec
 * @typedef {{
 *   summary: string;
 *   details?: string;
 *   args?: string;
 *   arguments?: Record<string, { description: string; default?: string }>;
 *   options: Record<string, OptionSpec>;
 *   examples: string[];
 *   project?(options: CommandContext['options']): boolean;
 *   run(context: CommandContext): Promise<number>;
 * }} Command
 */

const LIFETIME_LOGS = 'Logging__LogLevel__Microsoft.Hosting.Lifetime';

/**
 * Runs the app once in command mode; the app writes its result and stops. It binds a throwaway port, so the host's
 * own messages ("Now listening on", "Press Ctrl+C to shut down") would only mislead: they are left out unless the
 * environment sets their level.
 * @param {CliIo} io
 */
const commandEnv = (io) => ({ ASPNETCORE_URLS: 'http://127.0.0.1:0', [LIFETIME_LOGS]: io.env[LIFETIME_LOGS] ?? 'Warning' });

/**
 * @param {CliIo} io
 * @param {Record<string, string>} extra
 */
function appEnv(io, extra) {
  return { ...baseEnv(io), ASPNETCORE_ENVIRONMENT: io.env.ASPNETCORE_ENVIRONMENT ?? 'Development', ...extra };
}

// The variables the CLI sets itself to put the app into command mode.
const COMMAND_VARIABLES = new Set(['NETJES_COMMAND', 'NETJES_OUTPUT', 'NETJES_CHECK']);

/**
 * The CLI's environment without its own command variables: one left over in a shell or CI job (NETJES_COMMAND=export)
 * must not turn the app a command starts into another command. Each command sets the ones it needs. Every other
 * variable passes through, including settings the sidecar reads (NETJES_VITE_HOST, NETJES_VITE_ORIGIN) and Netjes__
 * configuration keys. Names match case-insensitively, as environment variables do on Windows.
 * @param {CliIo} io
 * @returns {Record<string, string | undefined>}
 */
function baseEnv(io) {
  return Object.fromEntries(Object.entries(io.env).filter(([key]) => !COMMAND_VARIABLES.has(key.toUpperCase())));
}

/**
 * @param {CliIo} io
 * @param {string} cwd
 * @param {{ check?: boolean }} options
 */
function generate(io, cwd, options = {}) {
  return io.spawn({
    command: 'dotnet',
    args: ['run', '--no-launch-profile'],
    cwd,
    env: appEnv(io, { ...commandEnv(io), NETJES_COMMAND: 'generate', ...(options.check ? { NETJES_CHECK: '1' } : {}) }),
  });
}

/**
 * The app's own vite entry point. `npx vite` would download vite@latest when the app has none installed, bypassing the
 * lockfile, so resolve it from the app's node_modules and run it with node instead.
 * @param {string} cwd
 * @returns {Promise<string | null>}
 */
async function findVite(cwd) {
  const manifest = findPackage(cwd, 'vite');
  if (!manifest) return null;
  try {
    const { bin } = JSON.parse(await readFile(manifest, 'utf8'));
    const entry = typeof bin === 'string' ? bin : bin?.vite;
    return typeof entry === 'string' ? path.resolve(path.dirname(manifest), entry) : null;
  } catch {
    return null;
  }
}

/**
 * Types, then the client and server bundles; stops at the first step that fails.
 * @param {CliIo} io
 * @param {string} cwd
 */
async function build(io, cwd) {
  const vite = await findVite(cwd);
  if (!vite) {
    printError(io, 'netjes: vite is not installed in this project. Run `npm install` first.');
    return 1;
  }
  for (const step of [
    () => generate(io, cwd),
    () => io.spawn({ command: process.execPath, args: [vite, 'build'], cwd, env: baseEnv(io) }),
    () => io.spawn({ command: process.execPath, args: [vite, 'build', '--ssr'], cwd, env: baseEnv(io) }),
  ]) {
    const code = await step();
    if (code !== 0) return code;
  }
  return 0;
}

/**
 * `target` as the user would type it from `from`: relative when it is below, else absolute.
 * @param {string} from
 * @param {string} target
 */
function displayPath(from, target) {
  const relative = path.relative(from, target);
  return relative.startsWith('..') || path.isAbsolute(relative) ? target : relative || '.';
}

/**
 * A path quoted for the shell when a shell would split or expand it.
 * @param {string} value
 */
function shellPath(value) {
  if (/^[\w./\\:@%+=,-]+$/.test(value)) return value;
  return process.platform === 'win32' ? `"${value}"` : `'${value.replaceAll("'", "'\\''")}'`;
}

const VITE_DEV_PORT_KEY = 'NETJES:VITEDEVPORT';

/**
 * The Vite dev server's port as the app will read it (Netjes:ViteDevPort): from the environment, else from the
 * appsettings file of the app's environment, else from appsettings.json, else 5173. Null when one of those files
 * cannot be parsed; the app reports a taken port itself. One the app sets in code is not seen here.
 * @param {CliIo} io
 * @param {string} cwd
 * @returns {Promise<number | null>}
 */
async function viteDevPort(io, cwd) {
  for (const [key, value] of Object.entries(io.env)) {
    if (key.toUpperCase().replaceAll('__', ':') === VITE_DEV_PORT_KEY && /^\d+$/.test(value ?? '')) return Number(value);
  }
  for (const file of [`appsettings.${io.env.ASPNETCORE_ENVIRONMENT ?? 'Development'}.json`, 'appsettings.json']) {
    /** @type {string} */
    let text;
    try {
      text = await readFile(path.join(cwd, file), 'utf8');
    } catch {
      continue;
    }
    /** @type {unknown} */
    let settings;
    try {
      // As ASP.NET Core reads them: a byte order mark, comments and trailing commas are allowed.
      settings = JSON.parse(
        text
          .replace(/^\uFEFF/, '')
          .replace(/"(?:[^"\\]|\\.)*"|\/\/[^\n]*|\/\*[\s\S]*?\*\/|,(?=\s*[}\]])/g, (token) => (token.startsWith('"') ? token : '')),
      );
    } catch {
      return null;
    }
    const port = settingsPort(settings, '');
    if (port !== undefined) return port;
  }
  return 5173;
}

/**
 * Netjes:ViteDevPort in parsed appsettings, under its keys in any case, nested or joined with colons.
 * @param {unknown} settings
 * @param {string} prefix
 * @returns {number | undefined}
 */
function settingsPort(settings, prefix) {
  if (typeof settings !== 'object' || settings === null) return undefined;
  /** @type {number | undefined} */
  let port;
  for (const [key, value] of Object.entries(settings)) {
    const name = `${prefix}${key}`.toUpperCase();
    if (name === VITE_DEV_PORT_KEY) port = /^\d+$/.test(String(value)) ? Number(value) : port;
    else if (VITE_DEV_PORT_KEY.startsWith(`${name}:`)) port = settingsPort(value, `${name}:`) ?? port;
  }
  return port;
}

/**
 * Where things are in a new app, as `netjes new` points them out.
 * @param {string} name
 */
function layout(name) {
  const rows = [
    ['app/', 'the pages: page.tsx is /, about/page.tsx is /about, layout.tsx wraps them'],
    ['components/', 'React components, each with a Vitest test'],
    ['Features/', 'the C# side, one folder per feature: loaders and server actions'],
    ['Program.cs', `the ASP.NET Core app (${name}.csproj)`],
    ['generated/netjes.ts', 'types and zod schemas from C#, rewritten by netjes generate; never edit it'],
    ['Dockerfile', 'and the docker-compose files: Docker for development and production'],
  ];
  const width = Math.max(...rows.map(([entry]) => (entry ?? '').length)) + 2;
  return rows.map(([entry, what]) => `  ${(entry ?? '').padEnd(width)}${what}`).join('\n');
}

/** @type {Record<string, Command>} */
export const COMMANDS = {
  new: {
    summary: 'Create an app: ASP.NET Core with React pages, Tailwind CSS, TanStack Query, zod and Vitest',
    args: '<dir>',
    arguments: { '<dir>': { description: 'The folder to create the app in; it must not exist yet, or be empty' } },
    options: {
      name: {
        type: 'string',
        value: '<Name>',
        description: 'The .NET project name and root namespace',
        default: 'the folder name in PascalCase, e.g. my-shop gives MyShop',
      },
      packages: {
        type: 'boolean',
        description: 'Use the Netjes NuGet and npm packages even when netjes runs from a Netjes checkout',
        default: 'off; outside a checkout the packages are used anyway',
      },
      'skip-install': { type: 'boolean', description: 'Do not run npm install in the new app', default: 'off' },
    },
    examples: ['netjes new my-shop', 'netjes new my-shop --name Shop --skip-install'],
    async run({ io, cwd, args, options }) {
      const dir = args[0] ?? (await io.prompt?.('Folder for the new app: '))?.trim();
      if (!dir) {
        printError(io, 'netjes new: which folder? e.g. `netjes new my-app`');
        return 1;
      }
      const target = path.resolve(cwd, dir);
      const shown = shellPath(displayPath(io.cwd, target));
      try {
        const { names, mode } = await scaffold(target, {
          ...(typeof options.name === 'string' ? { name: options.name } : {}),
          packages: options.packages === true,
        });
        const source = mode === 'checkout' ? 'referencing this Netjes checkout' : `using the Netjes ${await packageVersion()} packages`;
        io.stdout(`Created ${names.dotnet} in ${shown} (${source})\n\n${layout(names.dotnet)}\n`);
      } catch (error) {
        printError(io, `netjes new: ${error instanceof Error ? error.message : String(error)}`);
        return 1;
      }
      if (!options['skip-install']) {
        const code = await io.spawn({ command: 'npm', args: ['install'], cwd: target, env: baseEnv(io) });
        if (code !== 0) {
          printError(
            io,
            `netjes new: npm install failed (exit code ${code}). The app is in ${target}: fix what npm reported, then run \`npm install\` there.`,
          );
          return code;
        }
      }
      const steps = [`cd ${shown}`, ...(options['skip-install'] ? ['npm install'] : []), 'npm run dev    then open http://localhost:5080'];
      io.stdout(`\nNext:\n${steps.map((step) => `  ${step}\n`).join('')}`);
      return 0;
    },
  },

  dev: {
    summary: 'Run the app in Development: dotnet watch with C# hot reload, Vite HMR for pages',
    details:
      'Serves http://localhost:5080 unless ASPNETCORE_URLS says otherwise, with the Vite dev server on port 5173 unless ' +
      'the app sets Netjes:ViteDevPort. Stop it with Ctrl+C.',
    options: {
      'no-watch': { type: 'boolean', description: 'Run once with dotnet run instead of dotnet watch', default: 'off' },
    },
    examples: ['netjes dev', 'ASPNETCORE_URLS=http://localhost:3000 netjes dev'],
    project: () => true,
    async run({ io, cwd, options }) {
      // The app needs the sidecar from netjes and the Vite dev server; without them it fails at startup, in a stack trace.
      const missing = ['netjes', 'vite'].filter((name) => !findPackage(cwd, name));
      if (missing.length) {
        printError(io, `netjes dev: ${missing.join(' and ')} ${missing.length > 1 ? 'are' : 'is'} not installed in this project. Run \`npm install\` first.`);
        return 1;
      }
      const urls = io.env.ASPNETCORE_URLS ?? 'http://localhost:5080';
      // A port that is taken fails the app at startup, in a stack trace; check the one local URL it will bind.
      const port = Number(/^https?:\/\/(?:localhost|127\.0\.0\.1|\[::1\]):(\d+)\/?$/i.exec(urls)?.[1]);
      if (port && io.portFree && !(await io.portFree(port))) {
        printError(
          io,
          `netjes dev: port ${port} is in use (${urls}); stop what is using it, or pick another port: ASPNETCORE_URLS=http://localhost:${port + 1} netjes dev`,
        );
        return 1;
      }
      // The app's Node process runs the Vite dev server on a port of its own, which must be free too. Vite binds
      // localhost, which may resolve to ::1 only, unless NETJES_VITE_HOST says otherwise; check it the same way.
      const vitePort = await viteDevPort(io, cwd);
      if (vitePort && io.portFree && !(await io.portFree(vitePort, io.env.NETJES_VITE_HOST || 'localhost'))) {
        printError(
          io,
          `netjes dev: port ${vitePort} is in use, and the Vite dev server needs it; stop what is using it, or move Vite to another port: Netjes__ViteDevPort=${vitePort + 1} netjes dev`,
        );
        return 1;
      }
      return io.spawn({
        command: 'dotnet',
        args: options['no-watch'] ? ['run', '--no-launch-profile'] : ['watch', 'run', '--no-launch-profile'],
        cwd,
        env: appEnv(io, { ASPNETCORE_URLS: urls }),
      });
    },
  },

  build: {
    summary: 'Generate the types, then build the client and server bundles into dist/',
    details: 'Uses the vite installed in the project (never a downloaded one), so run npm install first.',
    options: {},
    examples: ['netjes build'],
    project: () => true,
    run: ({ io, cwd }) => build(io, cwd),
  },

  start: {
    summary: 'Run the app in Production, from a build (netjes build) or a published app',
    options: {
      dll: {
        type: 'string',
        value: '<path>',
        description: "Run a published app's .dll (dotnet publish) from the .dll's folder, instead of the project",
        default: 'dotnet run -c Release in the project',
      },
    },
    examples: ['netjes start', 'netjes start --dll bin/publish/MyShop.dll'],
    project: (options) => typeof options.dll !== 'string',
    async run({ io, cwd, options }) {
      const env = { ...baseEnv(io), ASPNETCORE_ENVIRONMENT: io.env.ASPNETCORE_ENVIRONMENT ?? 'Production' };
      if (typeof options.dll === 'string') {
        // The content root is the current folder, so the published app runs from the folder it was published to: it
        // serves the published dist/ and reads the published appsettings*.json, not the project's.
        const dll = path.resolve(cwd, options.dll);
        // Spawning in a folder that is not there fails with a bare ENOENT, and dotnet's own message for a missing file
        // is hard to read.
        if (!statSync(dll, { throwIfNoEntry: false })?.isFile()) {
          io.stderr(`netjes start: --dll ${options.dll} is not a file (${dll})\n`);
          return 1;
        }
        return io.spawn({ command: 'dotnet', args: [dll], cwd: path.dirname(dll), env });
      }
      // A hint, not an error: an app can keep its build elsewhere (NetjesOptions.DistDirectory).
      if (!existsSync(path.join(cwd, 'dist', 'server', 'server-entry.js'))) {
        io.stderr('netjes start: dist/ has no build yet; if the app fails to start, run `netjes build` first.\n');
      }
      return io.spawn({ command: 'dotnet', args: ['run', '--no-launch-profile', '-c', 'Release'], cwd, env });
    },
  },

  generate: {
    summary: 'Write generated/netjes.ts from the C# types',
    details: 'Runs the app once in command mode: it binds no real port, starts no Node sidecar and needs no build.',
    options: {
      check: {
        type: 'boolean',
        description: 'Write nothing, and exit 1 when generated/netjes.ts is out of date (for CI)',
        default: 'off',
      },
    },
    examples: ['netjes generate', 'netjes generate --check'],
    project: () => true,
    run: ({ io, cwd, options }) => generate(io, cwd, { check: options.check === true }),
  },

  routes: {
    summary: 'Print the route table and its warnings, as the app sees them',
    details: 'Runs the app once in command mode, like generate. Warnings do not change the exit code.',
    options: {
      json: { type: 'boolean', description: 'Print the report as JSON', default: 'off: a table' },
    },
    examples: ['netjes routes', 'netjes routes --json'],
    project: () => true,
    async run({ io, cwd, options }) {
      const dir = await mkdtemp(path.join(tmpdir(), 'netjes-routes-'));
      const output = path.join(dir, 'routes.json');
      try {
        const code = await io.spawn({
          command: 'dotnet',
          args: ['run', '--no-launch-profile'],
          cwd,
          env: appEnv(io, { ...commandEnv(io), NETJES_COMMAND: 'routes', NETJES_OUTPUT: output }),
          // stdout is for the report, so that `netjes routes --json | jq` works; the build and app output go to stderr.
          stdout: 'stderr',
        });
        if (code !== 0) return code;
        const report = await readFile(output, 'utf8');
        io.stdout(options.json ? `${report}\n` : formatRoutes(JSON.parse(report)));
        return 0;
      } finally {
        await rm(dir, { recursive: true, force: true });
      }
    },
  },

  export: {
    summary: 'Build, then write every page as static HTML into a folder',
    args: '[dir]',
    arguments: { '[dir]': { description: 'The folder to write the site to, inside the app', default: 'out' } },
    options: {
      'no-build': { type: 'boolean', description: 'Export the build already in dist/', default: 'off: build first' },
    },
    examples: ['netjes export', 'netjes export site --no-build'],
    project: () => true,
    async run({ io, cwd, args, options }) {
      if (!options['no-build']) {
        const code = await build(io, cwd);
        if (code !== 0) return code;
      }
      return io.spawn({
        command: 'dotnet',
        args: ['run', '--no-launch-profile', '-c', 'Release'],
        cwd,
        env: {
          ...baseEnv(io),
          ...commandEnv(io),
          ASPNETCORE_ENVIRONMENT: io.env.ASPNETCORE_ENVIRONMENT ?? 'Production',
          NETJES_COMMAND: 'export',
          NETJES_OUTPUT: path.resolve(cwd, args[0] ?? 'out'),
        },
      });
    },
  },

  info: {
    summary: 'Check the tools and the project setup',
    options: {},
    examples: ['netjes info', 'netjes info --cwd ../my-shop'],
    async run({ io, cwd }) {
      const version = await packageVersion();
      const tools = await checkTools(io, cwd, baseEnv(io));
      const project = await checkProject(cwd, version);
      const color = io.color?.stdout === true;
      io.stdout(`netjes ${version} in ${cwd}\n\n${formatChecks(tools, color)}\n${formatChecks(project, color)}`);
      return [...tools, ...project].some((check) => check.status === 'fail') ? 1 : 0;
    },
  },

  agents: {
    summary: 'Add AGENTS.md, CLAUDE.md and the Netjes skill for coding agents to an existing app',
    args: '[dir]',
    arguments: { '[dir]': { description: 'The app folder', default: 'the current folder' } },
    options: {
      name: {
        type: 'string',
        value: '<Name>',
        description: "The app's name in AGENTS.md",
        default: 'the name of its .csproj, else the folder name',
      },
      force: { type: 'boolean', description: 'Replace the files that exist', default: 'off: keep them' },
    },
    examples: ['netjes agents', 'netjes agents ../my-shop --force'],
    async run({ io, cwd, args, options }) {
      return agents(io, path.resolve(cwd, args[0] ?? '.'), {
        ...(typeof options.name === 'string' ? { name: options.name } : {}),
        force: options.force === true,
      });
    },
  },
};
