# 0027 – Testing Library advances Vitest's fake timers through a jest global that exists only while they are on

Status: accepted; amends [ADR 0005](0005-a-small-default-stack.md)

## Context

Testing Library (`@testing-library/dom` and `@testing-library/react`) handles fake timers only when they are Jest's: it looks for a `jest` global and a `setTimeout` that a fake clock replaced, and then moves that clock on with `jest.advanceTimersByTime`. Vitest has no `jest` global, so under `vi.useFakeTimers()` Testing Library took its path for real timers while the timers were fake. Every user-event call, `waitFor` and `findBy` runs in the wrapper that `@testing-library/react` sets up, which ends on a `setTimeout(0)`; nothing advanced it, so none of them returned. A `waitFor` for an element already on the page waited until the test timed out, and so did a click from a user-event `user` created with the `advanceTimers` option, which advances the clock for user-event's own delays only. A component test that needs fake timers, for a debounce or a message that goes away after a few seconds, could not use Testing Library's queries or user-event at all.

The `netjes()` plugin reaches an app's tests only through the Vitest settings it adds. Its one setup file, Testing Library's cleanup, was added only to an app that set no `setupFiles` of its own, by the rule of [ADR 0005](0005-a-small-default-stack.md) that the app's own `test` config overrides the plugin's test defaults. The setup file this record adds is an exception to that rule: the app's `setupFiles` do not replace it.

## Decision

- `netjes()` adds a setup file of its own, `dist/testing-vitest.js`, to every app's `setupFiles`, after the app's own setup files, whatever they are. `netjes({ test: false })` adds none. The file is named by its path, like the cleanup file, and is not exported. The cleanup file keeps its rule: it is added only when the app sets no `setupFiles`.
- The setup file defines `jest` on `globalThis` as a getter. While `vi.isFakeTimers()` is true it returns a bridge whose `advanceTimersByTime(ms)` calls `vi.advanceTimersByTime(ms)`; otherwise it returns `undefined`. Testing Library then moves Vitest's fake clock on as it moves Jest's, and with real timers it behaves as before.
- A `jest` global that exists before the setup file runs, from the app's own setup file or from its test environment, is left alone. A test that assigns `jest` replaces the getter with its own value.

## Alternatives considered

- **Documentation only**, with a common workaround, `globalThis.jest = vi` in a setup file of the app's. Every app would meet the hang before it found the page, and a `jest` global that is always there makes code that looks for Jest take its Jest path in every test.
- **A `jest` global that is always defined.** `typeof jest` would be `'object'` in tests on real timers too, where nothing needs it.
- **An opt-in helper**, such as a function a test calls before it fakes timers, or a setup file to list. The hang comes first and does not point to the helper.
- **Wrapping Testing Library's `asyncWrapper`** to advance the clock past its last `setTimeout(0)`. user-event would return, but `waitFor` and `findBy` would still poll on a fake `setInterval` that nothing advances.
- **Adding the new setup file only when the app sets no `setupFiles`**, as the cleanup file is added. Apps with setup files of their own would not get the bridge, and nothing would tell them.
- **Adding the cleanup file to every app as well.** An app that sets `setupFiles` to leave the cleanup out could no longer do so.

## Consequences

- Under fake timers, `waitFor` and `findBy` return, and a `user` from `userEvent.setup({ advanceTimers: vi.advanceTimersByTime })` clicks and types. `waitFor` and `findBy` advance the fake clock by their polling interval, and their timeout runs on the fake clock. user-event's calls without the `advanceTimers` option still wait on its own fake delays.
- While Vitest's timers are fake, `typeof jest` is `'object'`, so a library that looks for Jest finds it then; with real timers it is `'undefined'`. `'jest' in globalThis` is always true.
- A test on `vi.useFakeTimers({ shouldAdvanceTime: true })` used to take Testing Library's path for real timers and now takes its path for fake ones, in which `waitFor` advances the clock itself.
- `fireEvent` is synchronous and needs no bridge. A timer it starts is run with `await act(() => vi.advanceTimersByTimeAsync(ms))`.
- An app that set `setupFiles: []` to keep the plugin's setup file out now gets this one. `netjes({ test: false })` leaves out every Vitest setting the plugin adds.
- Under Vitest 5, an inline project in `test.projects` that changes no Vite options shares the Vite server of the config that declares it, and Vitest builds its `test` options from that config's as written, before any plugin's `config` hook adds to them. Such a project gets neither this setup file nor the plugin's other Vitest defaults, and nothing tells it. With `sharedViteServer: false` in the root's `test` options, Vitest resolves the config for each project, plugins included, and the docs say so. The plugin leaves that option to the app, since turning it off gives every project a Vite server of its own.
- Of the packages a Netjes app installs, only Testing Library's two read a `jest` global, and the bridge has only the method they call.
