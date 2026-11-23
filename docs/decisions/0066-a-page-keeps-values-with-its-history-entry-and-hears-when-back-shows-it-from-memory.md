# 0066 – A page keeps values with its history entry, and hears when Back shows it from memory

Status: accepted

## Context

A list that opens a record wants three things the router did not give. The record should know which list opened it, with that list's filters, to go back to it, and still know after a reload. The list should keep what the reader ticked when they come back. And a list that Back shows from the router's memory shows what it showed when it was left, though the record may have changed it since. The router wrote `{ __netjes: key }` over the whole `history.state` of every entry it added or replaced, kept scroll positions in memory, and showed a remembered page without telling it. An app could keep its own values by URL in `sessionStorage`, but two visits to one list then share them, a record opened by its URL still finds what an earlier visit left, and the router overwrote anything the app put in `history.state` itself.

## Decision

- **`useHistoryState(name, initial, schema?)`** works like `useState`, and the browser keeps the value with the history entry: in `history.state`, under the router's own key `__netjesState`, by name. Back, Forward and a reload bring the entry's value back.
- **A page that stays keeps its values; another page starts over.** A navigation that keeps the page on screen (another query, a fragment, a refresh, a post that stays) gives the entry it adds or replaces the values the page has, as the page keeps its React state; a navigation to another page starts from `initial`. "The page" is the router's page key, the path, so an intercepting route's modal shares the values of the page under it.
- **A navigation can carry the new entry's first values**: `historyState` on `<Link>`, `router.push` and `router.replace`, set over the values a page that stays keeps. The router copies them with `structuredClone`, so a value the browser cannot keep rejects the navigation before it starts.
- **Hydration matches.** The value is `initial` on the server and in the render that hydrates the page. The router reads the entry's values when it is created and shows them from its layout effect, in the hydrating commit, before the browser paints; an entry without values renders nothing again.
- **A setter follows its page, as `useState`'s does.** It writes into the entry on screen, and keeps working, as the same function, through the navigations that carry the page's values over (a refresh, another query, a fragment, a post that stays), so `await deleteTicked(ticked); setTicked([])` clears the ticks after an action that refreshes the page. The router binds it to the lineage of the values, which those navigations keep and which starts over with another page or when Back or Forward switch the entry: a page that sets a value as it unmounts, or while Back loads the entry before it, changes nothing.
- **The router keeps the keys of others.** When it replaces an entry it writes its keys over the entry's state and leaves the rest. A new entry starts with none of the entry before.
- **`useHistoryRestore(callback)`** calls back when the page on screen was shown from memory after Back or Forward: a page the router remembered, or the whole document from the browser's back-forward cache (`pageshow` with `persisted`). A page the router loads from the server, and Back over a fragment link, where the payload on screen stays, do not call it. The router calls back from an effect after the page's own, so a page that mounts from memory has subscribed by then.
- **A schema** parses the stored value, and one it rejects gives `initial`: the entry can hold what an older build put there.

## Alternatives considered

- **One `state` value per navigation**, as React Router's `navigate(to, { state })`. One value per entry makes every component of a page share it, and a layout, the page and a page package would overwrite each other. Values by name let each keep its own.
- **Values by URL in `sessionStorage`**, as an app could do without the router. Two visits to one list share their values, and a page opened by its URL finds values an earlier visit left.
- **Exposing the router's entry key**, so an app keeps its values elsewhere by it. It makes the key an API, and the app still needs the router to tell it when the entry changes.
- **A setter bound to the entry it was made in.** Every navigation that keeps the page gives the entry a new key, so a setter from before `router.refresh()`, or from before a server action that refreshes the page, would write nothing while the page and its values are still on screen, and every refresh would make it a new function, so effects that list it would run again.
- **A new entry that always starts over.** A tab or a filter that pushes an entry would drop what the page holds while it stays on screen, which its React state keeps.
- **Refreshing every page Back shows from memory**, or an option for it. Most pages are fine as they were; a page that is not decides, and decides how much to load again.
- **A schema failure that throws**, as `useParams(schema)` does. The value comes from the visitor's browser and can outlive a deploy; failing the page for it would break a reload.

## Consequences

- A record knows which list opened it through a reload, a list keeps its ticks through Back, and a page refreshes itself after Back when it needs to.
- Values are small and what `structuredClone` copies: ids, names and numbers. Names are shared by everything on the page, so a page package prefixes its own.
- A navigation that loads a document (another origin, `reloadDocument`, a new build) carries no values.
- The page's subtree is the same React element until another page is shown, so a value that changes re-renders only the components that read it.
