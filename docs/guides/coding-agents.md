# Coding agents

Netjes gives coding agents (Claude Code, Codex, Cursor, Copilot and the like) three things: the docs as plain text, a guide in each app, and a skill with the conventions in short.

## The docs as text

- [llms.txt](https://raw.githubusercontent.com/DennisVerhoeven/netjes/main/llms.txt) is an index of the docs in the [llms.txt](https://llmstxt.org) format: a summary, then a link and a line for each document, the `netjes` skill below included, so an agent can read it in an app that does not have it.
- [llms-full.txt](https://raw.githubusercontent.com/DennisVerhoeven/netjes/main/llms-full.txt) has those documents in one file, for agents that take a whole file as context. It leaves out the ones `llms.txt` marks as not in it: the architecture page, the benchmarks and the example apps' READMEs, which an agent building an app does not need.

Both follow the `main` branch, so they can describe a newer version than the one an app uses. The installed version's sources and types are in the app's `node_modules/netjes/`.

## A new app

`npx netjes new my-app` writes, next to the code:

| File | For |
|---|---|
| `AGENTS.md` | Any agent that reads `AGENTS.md`: how the app is laid out (`app/`, `Features/`, `generated/netjes.ts`), the commands, how to add a page with data, the rules (C# decides, zod at the boundaries, TanStack Query for server state, never edit generated files) and the checks to run before finishing. |
| `CLAUDE.md` | Claude Code, which reads `CLAUDE.md`: one line, `@AGENTS.md`, which imports the guide. |
| `.claude/skills/netjes/` | The `netjes` skill ([SKILL.md](../../packages/netjes/skills/netjes/SKILL.md)) and references for routing, data and actions, caching and streaming, testing and deployment. Claude Code loads skills from `.claude/skills/`; `AGENTS.md` points other agents at the folder. |

Commit them with the app. `AGENTS.md` is the app's own from then on: add the rules of your codebase to it.

## An existing app

[`netjes agents`](../reference/cli.md#netjes-agents) adds them. Run it in the app's directory (the one with the `.csproj`), or with that directory as the argument:

```bash
npx netjes agents            # or: npx netjes agents path/to/app
```

It writes the same three, with the project file's name as the title of `AGENTS.md` (`--name` sets another; without a single `.csproj` it takes the directory's name and warns on stderr that it did, so run it in the right folder or pass `--name`). It never replaces a file that exists: it prints the directory, then each file it wrote or kept, and if a `CLAUDE.md` of your own does not import `AGENTS.md`, it says to add the line `@AGENTS.md`. `--force` replaces them all.

## After upgrading Netjes

The skill and `AGENTS.md` describe the version that wrote them. The skill belongs to Netjes, so replace it with the installed version's:

```bash
rm -r .claude/skills/netjes && npx netjes agents
```

That keeps `AGENTS.md` and `CLAUDE.md`, which belong to the app. To compare your `AGENTS.md` with the installed version's, run `npx netjes agents --force`, which replaces all three, and use `git diff` to keep your own additions.

## Working on Netjes itself

The repository has its own [AGENTS.md](../../AGENTS.md) (which `CLAUDE.md` imports), for people and agents changing Netjes, and the skills [netjes-contributing](../../.claude/skills/netjes-contributing/SKILL.md) (making a change, from branch to merge) and [netjes-verification](../../.claude/skills/netjes-verification/SKILL.md) (proving it works) in `.claude/skills/`. `llms.txt` and `llms-full.txt` are generated from the docs by `scripts/llms.mjs`: run `npm run docs:llms` after changing a document, and `npm run docs:check` fails while they are out of date or while a link in the docs points at nothing. In a git checkout they are built only from the documents git tracks, so `git add` a new one first.
