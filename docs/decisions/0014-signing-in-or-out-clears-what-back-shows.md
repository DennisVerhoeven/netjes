# 0014 – Signing in or out clears what Back shows

Status: accepted

## Context

Back and forward can show a page without asking the server, from two places. The client router keeps the last 50 pages it showed, one per history entry, plus prefetched payloads for 30 seconds. The browser keeps documents in its HTTP cache and its back/forward cache, and shows them again on back and forward without revalidating them, `no-cache` or not. So after a user signs out, and someone else signs in, in the same tab, Back could show the first user's pages: with JavaScript from the router's memory, and without it, or past a full page load, from the browser's caches. A document sent to a visitor is a harder case: it stays when the client router signs the visitor in, and then shows their pages. Within the document, two more things outlive a user change: requests the router sent while the action ran, which carry the old cookies, and the TanStack Query cache, which lives as long as the page load.

Next.js has a rule for its router: cookies that a server action sets or deletes invalidate the client router's cache. It sends its dynamic pages `no-store`. Netjes actions are C# endpoints, and script cannot read `Set-Cookie` from a fetch response.

## Decision

- A client router's call of a `MapAction` or `MapPageAction` whose response sets or deletes a cookie is answered with `X-Netjes-Cookies: changed`. An endpoint filter checks the response as it starts. The cookies that count are the ones written while the action ran, including its own `OnStarting` callbacks and a failure that still carries the cookie. Cookies already on the response before the action ran (a middleware's) or written by callbacks registered before it (cookie authentication's sliding renewal) do not count. Nor does the antiforgery cookie on its own: it names no user, and a page rendered for a visitor without one stores it. Posts without JavaScript are not marked.
- On that header the router forgets every page it kept and every prefetched page, and starts over the requests still on their way, which went out with the old cookies: the navigation in progress and every prefetch in flight. A form post is never sent twice: what it answers after another call changed cookies is shown as it came. Until a page comes from the server again, the router keeps the page on screen for no history entry, not even one that a fragment link on it adds. Actions that change no cookie leave the router's memory alone.
- After such a `MapAction` call the router loads one page from the server, as Next.js does when an action changes cookies: the action's redirect, or its revalidation, or else a refresh of the page on screen (a form post's answer is that page already). A redirect to a fragment of the page loads nothing, so a refresh follows it too; a redirect to another site does not. If the user moved on while the action ran, to a page that loaded in the meantime (React shows it only once the action is done), the router refreshes that page instead.
- The query shell clears its `QueryClient` on that header: every query, seeded ones too, since a seed came from a page rendered for the previous user. The page loads again and seeds its own; the action's own query entries are applied after the clear.
- Every page render that the output cache does not store goes out with `Cache-Control: no-store, private`, unless the page or the app set its own header, as Next.js sends its dynamic pages. That covers documents and client payloads, for visitors and signed-in users alike. A visitor's document is no exception, because it stays when the client router signs the visitor in. Responses the output cache stores keep what they send.

## Alternatives considered

- **Forget the router's memory after every action.** Simpler, but it costs a request for every Back after every like or save.
- **A field in the action payload instead of a header.** It would not reach the router for page actions, whose responses are page payloads, or for failures written by other code. A header covers every response.
- **Count the antiforgery cookie.** No more privacy, since a user change always changes the authentication cookie, and more round trips, for example after a page action without antiforgery stores its first token.
- **Keep the page the action ran on in memory.** That is the page the user signed out from, so Back after signing out would still show it.
- **Leave the page on screen until the user leaves it.** No extra request, but the page shows whoever was signed in before, and so does a page that loaded while the action ran.
- **Clear only the queries that no payload seeded.** Seeds come from pages rendered for the previous user too, and the page on screen seeds its own again when it loads.
- **Refresh a form post's answer by GET when another call changed cookies while it ran.** That would replace what the post answered, such as its validation errors.
- **`no-store` for a signed-in user's pages only (`HttpContext.User.Identity.IsAuthenticated`), `no-cache` for a visitor's.** A returning visitor's pages would stay in the browser's caches and be eligible for the back/forward cache. But a visitor's document stays when the client router signs the visitor in, so after a full page load and a sign-out in the later document, the back/forward cache would restore it on Back with the user's pages.
- **Load a restored document again on `pageshow` (`persisted`) once its router has seen a cookie change.** It covers the same document without changing any header, but needs JavaScript and may show the old page until the new one arrives.
- **Leave documents `no-cache` and send `Clear-Site-Data` on sign-out.** It needs every app's sign-out to send it, and it clears more than pages.

## Consequences

- Back and forward after signing in or out load from the server: with JavaScript because the router has forgotten its pages, and without it, or past a full page load, because no page's document is kept.
- That includes a document first sent to a visitor that the client router then signed in: after a full page load (a `reloadDocument` link, a typed URL, a new deploy) and a sign-out in the later document, Back loads it from the server instead of restoring it with the user's pages. An end-to-end test checks this in Chromium with its back/forward cache on.
- Browsers store none of these pages in their HTTP cache, a visitor's included, so Back past a full page load fetches the page again unless the back/forward cache restores it. A browser that keeps `no-store` pages out of its back/forward cache always fetches it. Chrome keeps them there, and restores them only while the site's cookies are unchanged. Client navigations are unaffected: they never reuse a stored document.
- A page that sets its own `Cache-Control` decides for itself: one the browser may keep can come back on Back after a sign-out.
- An action that sets or deletes a cookie and neither redirects nor revalidates refreshes its page, one request more, so the page shows the new user at once. With a redirect or a revalidation, that load is the only one.
- A fragment link followed before that refresh arrives supersedes it, as it supersedes any navigation: the page stays as it was, kept for no history entry, until the user leaves it.
- A form post on its way while another call signs someone in or out shows what it answered for the old cookies.
- After a user change `useQuery` fetches again what it had cached. A component that does not render again with the reloaded page (a memoized one whose props did not change) keeps its last result until it renders or refetches.
- Only the tab that made the call sees the header. It tells the app's other tabs, which forget their pages and queries the same way and load the page on screen again ([ADR 0021](0021-signing-in-or-out-reaches-the-other-tabs.md)). In a browser without `BroadcastChannel`, a tab whose user was signed out or changed in another tab keeps its router's memory and its query cache: Back there can show the first user's pages, and client-fetched queries keep that user's data for their `staleTime`, next to pages the server renders for the second user.
- A sign-in that changes no cookie in the action goes unseen, for example with a server-side session whose cookie the session middleware writes. Such an action can set or delete a cookie itself to mark the change.
- A proxy that strips `X-Netjes-Cookies` hides the change from the router: it keeps its pages and its query cache, as after an action that changed no cookie.
