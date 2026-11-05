# 0025 – A mark on `<html>` says the client router runs

Status: accepted

## Context

An end-to-end test has to wait for hydration before it clicks or types. Until then a `<Link>` loads the next page in full and a `<Form>` posts natively, so a test that acts too early can pass on the path a browser without JavaScript takes. Netjes gave no signal for it. The examples waited for the key the router stores in `history.state`, which is internal and outlives the document: the browser keeps a history entry's state when it loads the document again (a reload, or a back or forward that does not come from the back/forward cache), and the router takes over a key it finds there rather than writing a new one. After a reload the key is there before any of the page's scripts has run, so a test that waits for it can go on before the page has hydrated.

## Decision

- The client router puts `data-netjes-hydrated=""` on `<html>` once it runs in the document, and takes it off if it unmounts. The attribute means that the client router runs in this document. It is public, and its name is reserved. Tests wait for it with `page.locator('html[data-netjes-hydrated]').waitFor({ state: 'attached' })`, or with `expect(page.locator('html')).toHaveAttribute('data-netjes-hydrated', '')`.
- It is set in a passive effect (`useEffect`) declared after the one that registers the router as the page's action client. React runs a commit's layout effects before its passive effects, and one component's effects in the order they are declared, so by the time the mark shows, the router has keyed the history entry, listens for back and forward, and sends server action calls at once.
- It is never rendered. The server's HTML and the client-render shell that .NET writes do not change, and a page whose modules fail to load, which the client entry leaves as the server sent it, never gets it. A page that React renders in the browser because no sidecar was ready gets it as a hydrated one does.
- It does not wait for Suspense boundaries. React hydrates their content in passes of its own after the rest of the page, and when a click lands on content it has not hydrated yet, it hydrates that first and then handles the click.
- Netjes ships no Playwright helper: the guides show the one-line wait.

## Alternatives considered

- **Document `history.state.__netjes`.** It has the wrong lifetime, as above, and would make an internal key part of the API.
- **An event on `window`.** A test that starts listening after the event has fired never hears it.
- **A global on `window`.** Next.js sets one, `__NEXT_HYDRATED`, in its own test mode only, and does not document it. Locators and web-first assertions cannot see a global.
- **A `waitForHydration(page)` helper in the package.** It would tie the package to Playwright's API for one line that the guides can show.
- **The mark in the layout effect that keys the history entry.** It would show before the router has registered as the action client, while server action calls still wait for it.
- **A new history key in every document.** The router would rewrite the entry's state on every load, and tests would still read an internal key that no locator sees, written before the router is the action client.
- **The name `data-netjes-ready`.** It reads as a claim that the whole page is interactive, which the mark does not make.

## Consequences

- An end-to-end test waits for a documented signal of the document it is on, which a reload starts over.
- `<html>` in the browser has one attribute more, which a stylesheet or a DOM snapshot of its attributes sees. An app does not set, change or remove it.
- A test that waits for the mark on a page whose modules failed to load times out, which is right: no router runs there.
- The mark's effect has to stay after the registration effect. The package's tests fail when it moves before it or into a layout effect.
