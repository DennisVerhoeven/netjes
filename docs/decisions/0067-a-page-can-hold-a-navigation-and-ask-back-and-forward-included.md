# 0067 – A page can hold a navigation and ask, Back and Forward included

Status: accepted

## Context

A form with changes that are not saved loses them when the reader leaves its page. `beforeunload` asks only before the document unloads, and a client navigation unloads nothing. A click listener on links catches links, but not a form that navigates, not `router.push` in the app's code, and not the browser's Back and Forward: the browser moves to the other entry before the page hears of it, and `popstate` cannot be cancelled. The router wrote no place for an entry into `history.state`, so after Back or Forward it could not tell how many steps the browser took, nor which way.

## Decision

- **`useNavigationGuard({ when, onAttempt })`.** While `when` is true, every client navigation asks `onAttempt` first: links, `<Form method="get">`, `router.push`, `router.replace`, `router.back`, `router.forward` and the browser's Back and Forward. The attempt says where the navigation goes (`to`, the path, query and fragment without the base path) and how (`type`: `push`, `replace`, `back` or `forward`), and the navigation waits until its `proceed()` or `stay()` is called. The page draws the question itself, in its own words and language.
- **Back and Forward: the entry is put back, then the question asked.** The router keeps each entry's place in the tab's history in `history.state`, under its own key `__netjesIndex`: one more than the entry before it, the same as the entry it replaced, kept through a reload as the browser keeps the state. On a `popstate` while a guard holds, the place of the entry the browser went to less that of the entry on screen is the number of steps and their direction. The router takes them back (`history.go(-steps)`) and asks once the browser is on the entry again, so the address bar shows the page on screen while the question is open; `proceed()` steps from the entry on screen to the one the browser went to, and that step is not held. The address bar shows the other URL for a moment.
- **A guard never traps the reader.** `proceed()` works whenever it is called, also after its guard stopped holding, as after a save the reader asked for first. A newer navigation takes the place of one still waiting for an answer, and is asked about, so a guard that never answers keeps nothing from going. A step to an entry whose place the router does not know, such as one an app pushed with `history.pushState` itself, is let through: a guessed direction could take the reader somewhere else. What loads or unloads the document goes through `beforeunload` alone, which a guard sets while it holds: a reload, closing the tab, typing an address, a link to another site, `reloadDocument`. The browser asks its own question there, and the reader can always answer it.
- **What is not held.** A link to a fragment of the page on screen, and Back or Forward over one, keep the page. A form post, a server action and a server action's redirect are the form saving itself. `router.refresh()` stays on the page. A URL on another origin unloads the document, and `beforeunload` asks.
- **Several guards** are asked in turn, the one that started to hold last first; each `proceed()` passes the navigation to the next, and the first `stay()` ends it.
- **One question per unload.** A navigation the guards let go on that the router then loads in full (a page from another build, an answer that is not a Netjes page) unloads without the browser asking again, once.
- **`beforeunload` only while a guard holds.** Browsers keep no page with a `beforeunload` listener in their back-forward cache, so the listener is added when the first guard starts to hold and removed when the last one stops.

## Alternatives considered

- **Leaving Back and Forward to the browser**, as a click listener does. A client traversal unloads nothing, so `beforeunload` does not fire and the changes are lost on the most common way of leaving.
- **Asking at once and putting the entry back meanwhile.** The question would show while the address bar has the other URL, and the answer could come before the entry is back.
- **The Navigation API's `navigate` event**, which can cancel a traversal. Netjes would depend on how far each browser supports it, and still need this way for the others.
- **Guessing the place of an entry the router did not make**, from `history.length`. A wrong guess moves the reader to an entry they did not choose; letting the step through loses changes only in an app that pushes entries itself.
- **`window.confirm` as the question.** It blocks the page, cannot be styled, and speaks the browser's language, not the app's. The page draws its own.
- **One guard per page**, the last one winning. A form and an upload on one page would each lose what the other holds.

## Consequences

- A page with something to lose holds every way of leaving it, and a page package can hold its own.
- `history.state` has a third key of the router's, `__netjesIndex`, beside `__netjes` and `__netjesState`; the keys of others stay.
- The question is the app's: Netjes draws none and says nothing in any language.
- `createTestApp`'s router asks the guards about push and replace, so a component test can answer the question. It keeps no history, so its `back` and `forward` are recorded without asking.
- TaskBoard's task form asks before it is left with changes, and its end-to-end suite runs that spec in Chromium, Firefox and WebKit, since browsers differ in when they fire `popstate`.
