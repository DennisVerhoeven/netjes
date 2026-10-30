// @ts-check
// `netjes agents`: adds what `netjes new` gives coding agents (AGENTS.md, CLAUDE.md and the Netjes skill) to an
// existing app, keeping any of those files that exist unless told to replace them.
import { existsSync, statSync } from 'node:fs';
import { mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fillNames, projectNames, readTemplate, skillFiles } from './scaffold.mjs';

/**
 * The app's names: from `--name`, else its one project file (whose name is its root namespace), else its directory,
 * with a warning on stderr, because AGENTS.md would then name a project that may not exist.
 * @param {import('./main.mjs').CliIo} io
 * @param {string} dir
 * @param {string | undefined} name
 */
async function namesOf(io, dir, name) {
  if (name !== undefined) return projectNames(name);
  const projects = (await readdir(dir)).filter((entry) => entry.endsWith('.csproj'));
  const project = projects.length === 1 ? path.basename(projects[0] ?? '', '.csproj') : undefined;
  if (project) return { ...projectNames(project), dotnet: project };
  const folder = path.basename(dir);
  io.stderr(
    `netjes agents: ${projects.length ? `more than one .csproj in ${dir}` : `no .csproj in ${dir}`}, so the app is named after the folder ("${folder}"). ` +
      'Run it in the app\'s folder, or set the name with --name <Name>.\n',
  );
  return projectNames(folder);
}

/**
 * @param {import('./main.mjs').CliIo} io
 * @param {string} dir
 * @param {{ name?: string; force: boolean }} options
 * @returns {Promise<number>}
 */
export async function agents(io, dir, options) {
  if (!statSync(dir, { throwIfNoEntry: false })?.isDirectory()) {
    io.stderr(`netjes agents: ${dir} is not a directory\n`);
    return 1;
  }
  const names = await namesOf(io, dir, options.name);
  const files = [
    { path: 'AGENTS.md', text: fillNames(await readTemplate('_AGENTS.md'), names) },
    { path: 'CLAUDE.md', text: await readTemplate('_CLAUDE.md') },
    ...(await skillFiles()),
  ];

  io.stdout(`In ${dir}:\n`);
  /** @type {string[]} */
  const kept = [];
  for (const file of files) {
    const target = path.join(dir, ...file.path.split('/'));
    if (existsSync(target) && !options.force) {
      kept.push(file.path);
      io.stdout(`  kept  ${file.path}\n`);
      continue;
    }
    await mkdir(path.dirname(target), { recursive: true });
    await writeFile(target, file.text);
    io.stdout(`  wrote ${file.path}\n`);
  }
  if (kept.length) io.stdout('Files that exist are kept; --force replaces them.\n');
  if (kept.includes('CLAUDE.md') && !/^@AGENTS\.md\s*$/m.test(await readFile(path.join(dir, 'CLAUDE.md'), 'utf8'))) {
    io.stdout('Add a line `@AGENTS.md` to CLAUDE.md, so Claude Code reads AGENTS.md too.\n');
  }
  return 0;
}
