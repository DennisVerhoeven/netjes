# 0021 – Signing in or out reaches the app's other tabs

Status: accepted

## Context

This record extends [ADR 0014](0014-signing-in-or-out-clears-what-back-shows.md). Under it, the client router forgets the pages it keeps and the query shell clears its `QueryClient` when a response to one of the router's calls says `X-Netjes-Cookies: changed`. Only the tab that made the call sees that header, but every tab of a browser sends the same cookies. After a sign-out, and a sign-in as someone else, in one tab, another tab of the app would keep the first user's pages for Back and Forward and that user's client-fetched queries, next to pages the server renders for the second user. On a shared computer, the next person could go back through the first user's pages in a tab they never signed in on. The layout keys cover part of it: that tab's next click loads its layouts again for the new user. They do not cover Back or the query cache.

## Decision

- A tab whose router sees the header posts a message on a `BroadcastChannel` named after the app, `netjes:` and its base path (`netjes:/` at the root). A channel reaches only documents of the same origin, and the base path keeps two apps on one origin apart, as cookie authentication scopes its cookie to the base path by default.
- Every other tab of that app does on that message what the tab that made the call does. It forgets the pages it keeps for Back and Forward, its prefetched pages and its fetched antiforgery token. It starts over the navigation and the prefetches on their way, and clears its query cache. Then it loads the page on screen from the server again, in place, unless a navigation it started over is about to show one, or a form post is on its way: the post is not sent again, and its answer is shown as it came, as in ADR 0014.
- A tab posts only when it sees the header itself, never on a message, and a channel does not receive its own messages, so tabs never answer each other.
- A router that handed its tab to a document a form post answered (not a Netjes page) stops listening, so no page is loaded into that document.
- Where the browser has no `BroadcastChannel`, nothing is posted and the other tabs keep what they hold, as without this record.

## Alternatives considered

- **A `storage` event.** One tab writes a key to `localStorage` and the others get the event. It needs site data, which a browser that blocks it refuses (reading `localStorage` then throws), it leaves a value behind, and writing the same value again fires nothing, so every message needs a new one. Current browsers all have `BroadcastChannel`, which needs neither.
- **Polling.** Each tab asks the server every few seconds whether its user changed. That is a request per tab every few seconds, and until the next poll the tab still shows the previous user's pages. Script cannot compare the authentication cookie itself, since it is `HttpOnly`.
- **Clear at once, but refresh a hidden tab only when it is shown again.** Fewer requests while tabs are hidden, but a second code path on `visibilitychange`, and a tab in a window next to another is visible without having focus.
- **Nothing.** The layout keys already load the layouts again on that tab's next click, but Back there shows the first user's pages, and its client-fetched queries keep that user's data until their `staleTime` runs out.

## Consequences

- After a sign-in or sign-out in one tab, Back and Forward in every tab of the app load from the server, no tab's query cache keeps the previous user's data, and each tab shows its page as the server renders it after the change, without a click. A page that needs an account answers with its unauthorized page there, as it would on a reload.
- Each other open tab of the app, hidden ones included, sends one request per sign-in or sign-out.
- The refresh loads the page by GET and renders it in place. What a user typed into a form in another tab stays, unless the page changes around it or moves on (an unauthorized page that sends the visitor to sign in), but a page a form post answered, with its validation errors say, is replaced by the page as a GET shows it.
- A change the router does not see is not posted either: a form post without JavaScript, a sign-in that changes no cookie, a proxy that strips the header (ADR 0014). The other tabs then keep their pages and queries.
- Another browser, or a private window, has cookies of its own and is not told. A tab of the app on another build is told: the message does not name a build.
