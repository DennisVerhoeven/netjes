# 0034 – Navigation promises resolve when React has the page

Status: accepted

## Context

`router.push`, `router.replace` and `router.refresh()` return promises. The docs said that they settle "when the navigation has finished", which reads as: once the new page is on screen. They resolve earlier, once the router has fetched the page, loaded its modules, updated history and handed the page to React in a transition, which React commits later. Right after `await router.push('/about')`, the old page's heading and title can still be in the document. `<Link>` does not use the promise.

Waiting for the commit is not safe. React 19 holds the transitions started while an async transition is pending, such as a form action or a `useActionState` action, and commits them when it ends. `useAction` runs its calls in a `useActionState` action, and a call awaits the navigation or the refresh that the action's answer asks for. A promise that waited for the router's commit there would wait for a commit that waits for the action, and neither would ever end.

## Decision

- When the promises resolve stays as it is, and is documented: once the router has handed the new page to React, or once the navigation has ended another way, because another navigation took its place, the browser loads the URL with a full page load, or the router refused the URL.
- The docs say that the old page can still be on screen when a promise resolves, and that inside a transition of the app's own, a form action included, React shows the new page once that transition has ended.
- Code that needs the new page runs where it is sure to have it: in an effect of the new page, or in an effect that depends on `usePathname()` and `useSearchParams()`. A link to a fragment moves the focus to its element without any code ([ADR 0033](0033-client-navigations-announce-the-page-and-start-the-focus-over.md)).
- A test pins that `await router.push()` inside `startTransition(async () => ...)` resolves, and that the page shows once that transition has ended.

## Alternatives considered

- **Resolve once React has committed the page.** It never resolves inside an async transition or a form action, as above.
- **Resolve on the commit or after a timeout, whichever comes first.** What the promise means would then depend on timing.
- **Commit the page outside a transition.** While the new page suspends, React would show the nearest Suspense fallback instead of keeping the old page on screen until the new one is ready.
- **An `onNavigated` callback on `<Link>`, and an option of `push` and `replace` that takes one, called from the router's commit.** It works, but it is more API than Next.js has, where an effect does this job, and it can come later without a break.

## Consequences

- Running apps see no change. Code that awaited `router.push` and then read the document expecting the new page reads it in an effect instead.
- A navigation that a form action or an async transition of the app's own starts resolves, and the new page shows when the action or transition ends.
