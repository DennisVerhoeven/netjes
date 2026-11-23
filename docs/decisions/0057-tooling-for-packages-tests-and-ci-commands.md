# 0057 – The CLI takes a build configuration, and the test harness covers a package's tests and a page action's failure

Status: accepted; extends [ADR 0027](0027-testing-library-advances-vitest-fake-timers.md) and [ADR 0028](0028-test-harness-starts-from-the-apps-query-defaults.md)

## Context

Four small gaps sat in the tooling around apps and page packages:

- `netjes generate` and `netjes routes` ran `dotnet run` in its default configuration, Debug. A pipeline or a development container that had built Release compiled the whole app a second time, in another folder, only to write a file or print a table.
- The setup file that lets Testing Library and user-event work under Vitest's fake timers came only with the `netjes()` plugin, and the plugin needs an app's `app/` folder. The component tests of a page package run without it, so under `vi.useFakeTimers()` they waited on a timer nothing advanced.
- `createTestApp()` had no way to show the state `usePageActionState()` returns after a page action's DataAnnotations fail. A test could only pass the failure to the page as props, which is not how a page reads it.
- The link check dropped a lone underscore from a heading's anchor, such as the one in `## The (_) marker` or `## _private`, so a link to the anchor GitHub makes was reported as broken.

## Decision

- `netjes generate` and `netjes routes` take `-c, --configuration <name>` and pass it to `dotnet run -c`. Without it the arguments are as before. `build` and `export` keep their own: `build` runs `generate` in the default configuration, and `export` runs Release.
- The setup file is an entry point, `netjes/testing/vitest`. It is the file the plugin already adds to every app, so the two cannot differ. A page package lists it in `setupFiles`. Without a provided `netjes:query` it hands the harness no app defaults, and `createTestApp()` starts from its own.
- `createTestApp({ pageActionState: { errors, values? } })` sends the payload a server sends after a 422: status 422 and an `actionState` for the page, which `route` names, or else the URL's path. `usePageActionState()` reads it as it reads the server's. The state is the page's, so it is not offered to `useAction`, which reads the state of a server action by name.
- The link check pairs emphasis underscores as Markdown does and removes only a pair, so a lone one stays in the anchor.

## Alternatives considered

- **`-c` on `build` and `export` too.** `build` would then have to pass it to its first step only, and `export` already runs Release. The gap was in the two commands that start the app and nothing else; widening them is a later decision.
- **A second, separate setup file for fake timers.** The plugin's file does both jobs from one module, and two files could drift. Exporting the one that exists leaves one place to change.
- **An action name in `pageActionState`.** A page action's state is the page's own, so the route is its name, as in the payload.

## Consequences

- An app or pipeline that built Release runs `netjes generate -c Release` and `netjes routes -c Release` against that build. Nothing changes for one that does not pass the option.
- A page package adds `setupFiles: ['netjes/testing/vitest']` to its Vitest config to use fake timers with Testing Library. An app using `netjes()` changes nothing.
- A component test can show a page action's failed state without the page taking it as props.
- Anchors for headings with a lone underscore now match GitHub's, so a link to one passes the docs check.
