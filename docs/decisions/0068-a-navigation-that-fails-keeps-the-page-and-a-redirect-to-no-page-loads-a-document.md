# 0068 – A navigation that fails keeps the page, and a redirect to no page loads a document

Status: accepted

## Context

When a client navigation or `router.refresh()` got no answer, the router loaded the URL as a document. On a lost connection or a server that is restarting, that replaced a working page with the browser's own error page: a screen that refreshes itself on a timer lost its page at the first refresh after the connection dropped, and a link that failed took the reader away from what they had typed. `refresh()` resolved either way, and `useNavigation()` reported only a form post that failed, so a page could not tell that its refresh had failed, nor show it.

A redirect from a page action or a server action to a URL no page answers, such as a signed file URL, was requested twice. The router asked for it as a page (`X-Netjes-Data`), which ran the endpoint and started the download, found no page, and then loaded the URL as a document, which ran the endpoint again. A link that counts its uses was spent on the first request, so apps gave such forms `reloadDocument`.

## Decision

- **A navigation that fails keeps the page.** A navigation (a link, a GET form, `push`, `replace`, Back or Forward), a refresh and a form post that get no answer while the browser is online, or an answer that is a server error (5xx) and no Netjes page, end with the page on screen, its state and what was typed included. `useNavigation()` is then idle with `error`, a `NavigationError`: `kind` (`'navigate'`, `'refresh'` or `'submit'`), `status` (the answer's, or `null` when none came) and `location` (the path and query it went to, without the base path). A form post the server answered with a redirect went through: when the page the redirect leads to fails, the failure is that navigation's (`'navigate'`, with that page's location), so a form that shows its failures does not invite a second post. No exception is in it; the console has the details. It stays until the next navigation, refresh or form post starts, so a try again clears it as it starts. `SubmitError`, the type form posts reported with, is now this type; its `action` became `location`, and the name stays as a deprecated alias.
- **`refresh()` does not reject.** It resolves once the refresh has ended, as it did when a failure loaded the document; the failure is read from `useNavigation()`.
- **An offline browser loads the document, as before.** When `navigator.onLine` is false, a navigation or refresh that gets no answer loads the URL as a document, so the browser's offline page shows, or a service worker's. An answer below 500 that is no Netjes page (a file, a page of another framework, a proxy's 404) is the URL's to show, and loads as a document too.
- **A redirect `fetch` cannot follow is told apart.** A redirect to another origin that does not allow this one, such as an identity provider's sign-in page, rejects `fetch` as a lost connection does. Before it keeps the page, the router asks once more with `redirect: 'manual'`; a redirect there hands the URL to the browser, which follows it.
- **Back and Forward step back.** The browser is on the entry the reader chose before the router hears that its page did not come. The router steps back to the entry of the page on screen, by the place it keeps in `history.state` ([ADR 0067](0067-a-page-can-hold-a-navigation-and-ask-back-and-forward-included.md)), so the address bar and the page agree again. Where it does not know a place (an entry an app pushed itself), it loads the URL as a document.
- **A redirect to a URL that no page answers loads a document, once.** The router asks for the URL a redirect payload or a server action's redirect sent it to with `X-Netjes-Redirected: 1`. Right after routing, `UseNetjes` lets such a request go on to a page only: where routing chose anything else (another endpoint, `Page.Render` from a minimal API, the not-found fallback, nothing), it answers `{ "kind": "document" }` with `no-store`, out of the output cache, without running the endpoint. The router loads the URL as a document, and the endpoint runs once. The document load carries no header, so whatever the URL answers, a redirect back to a page included, is the browser's, and the router starts on the page it lands on: nothing loops.

## Alternatives considered

- **`refresh()` that rejects.** Every caller would need a `catch`, the refresh a server action asks for has no caller to catch it, and a page that shows the failure would still need it in state. One place, `useNavigation()`, serves a link, a refresh and a post alike.
- **Keeping the page while offline too.** Considered and left out: the browser's offline page and a service worker's offline page are what an app that works offline relies on, and a page kept on screen cannot say more than the app writes. A screen that refreshes on a timer skips its refresh while `navigator.onLine` is false.
- **Keeping the page on any answer that is no page.** A file, a page of another framework or a static host's 404 is what the URL is; a static export navigates by loading each page as a document this way.
- **`Page.Redirect(url).AsDocument()`**, a redirect the app marks. Every app would have to know which of its URLs are pages, a package's redirects included, and a server action's redirect would need the same. Routing knows it.
- **The server judging the location as it writes the redirect.** Matching a URL to an endpoint outside the request pipeline would repeat ASP.NET Core's routing (order, precedence, constraints, methods) and get it wrong at the edges. Asking at the URL itself runs the real routing, for one small extra request.
- **The header on every request of the router.** A link to a minimal API that renders a page with `Page.Render` would then load as a document too; a link to a file is written as a plain `<a>` already.

## Consequences

- A page shows a navigation or a refresh that failed where its shell is, from `useNavigation()`; one that does not keeps its page, and the reader sees nothing happen, where before the browser showed its error page.
- Apps that caught a rejection of `refresh()` read `error?.kind === 'refresh'` instead. Code that read `error.action` reads `error.location`, and code that showed every `error` as a form's failure checks `kind === 'submit'`.
- A navigation that gets no answer costs one more request, the one without following redirects.
- A form whose page action redirects to a file needs no `reloadDocument`. A redirect to a minimal API that renders a page with `Page.Render` loads it as a document. A redirect to a file that the static files middleware serves, ahead of routing, is still requested twice, and so is one behind a proxy that drops `X-Netjes-Redirected`.
- TaskBoard's app shell shows a failed navigation or refresh, and its end-to-end suite cuts the connection for one page's requests to check that the board stays.
