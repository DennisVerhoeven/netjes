// @vitest-environment node
import { existsSync } from 'node:fs';
import { mkdir, mkdtemp, readdir, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { PassThrough } from 'node:stream';
import { fileURLToPath } from 'node:url';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { run } from '../cli/main.mjs';
import { terminalPrompt } from '../cli/process-io.mjs';
import { namesFor } from '../cli/scaffold.mjs';
import { recordingIo } from './cli-io.ts';

const TEMPLATE = fileURLToPath(new URL('../template', import.meta.url));

let temp = '';
beforeEach(async () => {
  temp = await mkdtemp(path.join(tmpdir(), 'netjes-cli-new-'));
});
afterEach(async () => {
  await rm(temp, { recursive: true, force: true });
});

async function fails(args: string[]) {
  const { io, out, err, calls } = recordingIo(temp);
  expect(await run(args, io)).toBe(1);
  expect(out()).toBe('');
  expect(calls).toEqual([]);
  expect(err().trimEnd().split('\n')).toHaveLength(1);
  return err().trimEnd();
}

describe('netjes new names', () => {
  it.each([
    ['my-shop', 'MyShop'],
    ['1Shop', 'App1Shop'],
    ['My Shop', 'MyShop'],
    ['Shop_Front', 'ShopFront'],
    ['Café', 'Cafe'],
  ])('rejects --name %s, which is not a .NET project name, and suggests %s', async (name, suggestion) => {
    const message = await fails(['new', 'app', '--name', name, '--skip-install']);

    expect(message).toBe(
      `netjes new: --name '${name}' is not a valid .NET project name; use letters and digits, starting with a letter, e.g. --name ${suggestion}`,
    );
    expect(existsSync(path.join(temp, 'app'))).toBe(false);
  });

  it.each([['System'], ['HomePage']])('accepts %s, which builds', async (name) => {
    const { io } = recordingIo(temp);

    expect(await run(['new', 'app', '--name', name, '--skip-install'], io)).toBe(0);
  });

  it('uses --name as given', async () => {
    const { io } = recordingIo(temp);

    expect(await run(['new', 'app', '--name', 'myShop2', '--skip-install'], io)).toBe(0);

    expect(existsSync(path.join(temp, 'app', 'myShop2.csproj'))).toBe(true);
  });

  it('makes the names from a folder name with accents without them', async () => {
    const { io, out } = recordingIo(temp);

    expect(await run(['new', 'Mijn Café', '--skip-install'], io)).toBe(0);

    expect(out()).toMatch(/^Created MijnCafe in /);
    expect(existsSync(path.join(temp, 'Mijn Café', 'MijnCafe.csproj'))).toBe(true);
    expect(JSON.parse(await readFile(path.join(temp, 'Mijn Café', 'package.json'), 'utf8')).name).toBe('mijn-cafe');
    expect(await readFile(path.join(temp, 'Mijn Café', 'README.md'), 'utf8')).toMatch(/^# Mijn Cafe\n/);
  });

  it('needs --name when the folder name has nothing to make a name from', async () => {
    expect(await fails(['new', 'ウェブ', '--skip-install'])).toBe(
      "netjes new: cannot make a project name from the folder name 'ウェブ'; pass --name <Name>",
    );
    expect(existsSync(path.join(temp, 'ウェブ'))).toBe(false);

    const { io } = recordingIo(temp);
    expect(await run(['new', 'ウェブ', '--name', 'Web', '--skip-install'], io)).toBe(0);
  });

  it.each([['react'], ['vite'], ['zod'], ['netjes']])('rejects %s, a package the app depends on, as its npm name', async (name) => {
    const message = await fails(['new', name, '--skip-install']);

    expect(message).toContain(`'${name}'`);
    expect(message).toContain('choose another');
    expect(existsSync(path.join(temp, name))).toBe(false);
  });

  it.each([
    ['class', 'Class'],
    ['event', 'Event'],
    ['namespace', 'Namespace'],
  ])('rejects --name %s, a C# keyword, and suggests %s', async (name, suggestion) => {
    expect(await fails(['new', 'app', '--name', name, '--skip-install'])).toBe(
      `netjes new: --name '${name}' is a C# keyword; choose another name, e.g. --name ${suggestion}`,
    );
  });

  // A root namespace of the same name hides the type from the app's code, and the build fails (CS0101, CS0118, CS0234).
  it.each([
    ['SiteProps'],
    ['Program'],
    ['Page'],
    ['ServerAction'],
    ['WebApplication'],
    ['ValueTask'],
    ['TimeProvider'],
    ['FromFormAttribute'],
  ])('rejects %s, a type name the app uses', async (name) => {
    const message = await fails(['new', 'app', '--name', name, '--skip-install']);

    expect(message).toBe(`netjes new: '${name}' is the name of a type the app's code uses; choose another name with --name`);
  });

  it('rejects a folder whose name is a type name the app uses', async () => {
    expect(await fails(['new', 'page', '--skip-install'])).toBe(
      "netjes new: 'Page' is the name of a type the app's code uses; choose another name with --name",
    );
    expect(existsSync(path.join(temp, 'page'))).toBe(false);
  });

  it("rejects every type name the template's C# uses by its simple name, apart from the ones that build", async () => {
    // Each of these, as the root namespace, was built with the template: it hides nothing the app's code looks up.
    const builds = new Set(['Microsoft', 'HomePage', 'MapHomePage', 'HomeProps', 'GreetInput', 'Greeting', 'FromForm']);
    const parameters = new Set(['Message', 'RenderedAt', 'Name', 'Text', 'SiteName']);
    const used = new Set<string>();
    for (const file of await readdir(TEMPLATE, { recursive: true })) {
      if (!file.endsWith('.cs')) continue;
      const code = (await readFile(path.join(TEMPLATE, file), 'utf8')).replace(/\/\/.*$/gm, '').replace(/"(?:[^"\\]|\\.)*"/g, '""');
      for (const [name] of code.matchAll(/(?<![\w.])[A-Z]\w*/g)) used.add(name);
    }

    const unchecked = [];
    for (const name of used) {
      if (builds.has(name) || parameters.has(name)) continue;
      if (!('problem' in (await namesFor(path.join(temp, 'app'), name)))) unchecked.push(name);
    }

    expect(used).toContain('Page');
    expect(unchecked, 'build the template with each of these as --name, then refuse it or list it as one that builds').toEqual([]);
  });
});

describe('netjes new folders', () => {
  it('refuses a file', async () => {
    await writeFile(path.join(temp, 'taken'), 'x');

    expect(await fails(['new', 'taken', '--skip-install'])).toBe(`netjes new: ${path.join(temp, 'taken')} is a file, not a folder`);
  });

  it('refuses a folder that is not empty, and leaves it alone', async () => {
    await mkdir(path.join(temp, 'taken'));
    await writeFile(path.join(temp, 'taken', 'notes.txt'), 'x');

    expect(await fails(['new', 'taken', '--skip-install'])).toBe(
      `netjes new: ${path.join(temp, 'taken')} already exists and is not empty; choose a new folder`,
    );
    expect(await readdir(path.join(temp, 'taken'))).toEqual(['notes.txt']);
  });

  it('uses an empty folder', async () => {
    await mkdir(path.join(temp, 'empty'));
    const { io } = recordingIo(temp);

    expect(await run(['new', 'empty', '--skip-install'], io)).toBe(0);
  });
});

describe('netjes new output', () => {
  it('says what it created, where everything lives, and what to run next', async () => {
    const { io, out, err } = recordingIo(temp);

    expect(await run(['new', 'my-shop'], io)).toBe(0);

    const text = out();
    expect(err()).toBe('');
    expect(text).toMatch(/^Created MyShop in my-shop/);
    // Each path it points out exists.
    const listed = [...text.matchAll(/^ {2}(\S+)\s{2,}/gm)].map((match) => match[1] as string);
    expect(listed).toEqual(expect.arrayContaining(['app/', 'Features/', 'components/', 'generated/netjes.ts', 'Program.cs']));
    for (const entry of listed) expect(existsSync(path.join(temp, 'my-shop', entry)), entry).toBe(true);
    expect(text).toMatch(/Next:\n {2}cd my-shop\n {2}npm run dev .*http:\/\/localhost:5080/);
    expect(text).not.toContain('npm install');
  });

  it('gives the app a launch profile, so dotnet run and an IDE start it in Development where npm run dev does', async () => {
    const { io, out } = recordingIo(temp);

    expect(await run(['new', 'my-shop', '--skip-install'], io)).toBe(0);

    const settings = JSON.parse(await readFile(path.join(temp, 'my-shop', 'Properties', 'launchSettings.json'), 'utf8'));
    const profiles = Object.values(settings.profiles) as { commandName: string; applicationUrl: string; environmentVariables: Record<string, string> }[];
    // dotnet run takes the first one.
    expect(profiles[0]?.commandName).toBe('Project');
    expect(profiles[0]?.environmentVariables.ASPNETCORE_ENVIRONMENT).toBe('Development');
    expect(out()).toContain(`npm run dev    then open ${profiles[0]?.applicationUrl}\n`);
  });

  it('lists npm install among the next steps after --skip-install', async () => {
    const { io, out } = recordingIo(temp);

    await run(['new', 'my-shop', '--skip-install'], io);

    expect(out()).toMatch(/Next:\n {2}cd my-shop\n {2}npm install\n {2}npm run dev/);
  });

  it.skipIf(process.platform === 'win32')('quotes a folder a shell would split', async () => {
    const { io, out } = recordingIo(temp);

    expect(await run(['new', 'my shop!', '--skip-install'], io)).toBe(0);

    expect(out()).toContain("  cd 'my shop!'\n");
  });

  it('says where the app is when npm install fails, and passes its exit code on', async () => {
    const { io, out, err } = recordingIo(temp, () => 7);

    expect(await run(['new', 'my-shop'], io)).toBe(7);

    expect(out()).toMatch(/^Created MyShop/);
    expect(out()).not.toContain('npm run dev');
    expect(err()).toBe(
      `netjes new: npm install failed (exit code 7). The app is in ${path.join(temp, 'my-shop')}: fix what npm reported, then run \`npm install\` there.\n`,
    );
  });
});

describe('netjes new without a folder', () => {
  /** netjes new with the terminal prompt on these streams, and `keys` typed once it asks. */
  async function typed(keys: string) {
    const input = new PassThrough();
    const output = new PassThrough();
    let shown = '';
    output.on('data', (chunk) => {
      if (!shown) setImmediate(() => input.write(keys));
      shown += chunk;
    });
    const recorded = recordingIo(temp);
    recorded.io.prompt = terminalPrompt(input, output);
    const code = await run(['new', '--skip-install'], recorded.io);
    return { code, shown, err: recorded.err() };
  }

  it('asks for one in a terminal', async () => {
    const recorded = recordingIo(temp);
    const questions: string[] = [];
    recorded.io.prompt = async (question) => {
      questions.push(question);
      return 'from-prompt';
    };

    expect(await run(['new', '--skip-install'], recorded.io)).toBe(0);

    expect(questions).toEqual(['Folder for the new app: ']);
    expect(existsSync(path.join(temp, 'from-prompt', 'FromPrompt.csproj'))).toBe(true);
  });

  it('takes the folder typed at the terminal prompt', async () => {
    const { code } = await typed('typed-app\r');

    expect(code).toBe(0);
    expect(existsSync(path.join(temp, 'typed-app', 'TypedApp.csproj'))).toBe(true);
  });

  it('ends the prompt line, then fails, when input ends (Ctrl+D)', async () => {
    const { code, shown, err } = await typed('\x04');

    expect(code).toBe(1);
    expect(shown).toMatch(/^.*Folder for the new app: .*\n$/s);
    expect(err).toBe('netjes new: which folder? e.g. `netjes new my-app`\n');
  });

  it('ends the prompt line and stops with exit code 130 on Ctrl+C', async () => {
    const { code, shown, err } = await typed('\x03');

    expect(code).toBe(130);
    expect(shown).toMatch(/\n$/);
    expect(err).toBe('');
    expect(await readdir(temp)).toEqual([]);
  });

  it('fails when the answer is empty', async () => {
    const recorded = recordingIo(temp);
    recorded.io.prompt = async () => '  ';

    expect(await run(['new', '--skip-install'], recorded.io)).toBe(1);

    expect(recorded.err()).toContain('netjes new: which folder?');
  });

  it('fails without a terminal to ask in', async () => {
    expect(await fails(['new', '--skip-install'])).toBe('netjes new: which folder? e.g. `netjes new my-app`');
  });
});
