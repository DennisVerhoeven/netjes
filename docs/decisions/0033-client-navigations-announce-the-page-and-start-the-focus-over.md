# 0033 – Client navigations announce the page and start the focus over

Status: accepted

## Context

When the browser loads a page, a screen reader announces the new document, and the keyboard focus starts over at the top: the next Tab reaches the first element that takes the focus. The client router swapped the page, scrolled and stopped. Nothing told assistive technology that the page had changed. The focus stayed on the link that was followed when that link was in a layout, which stays mounted, and fell back to `body` when the link went with the old page, with the next Tab starting from wherever the browser had kept its place. A link to a `#fragment` scrolled to its element but left the focus where it was, so a skip link kept the focus on itself. Without JavaScript, the browser's own fragment navigation focuses the element, or makes the next Tab start from it when it takes no focus, and the next Tab after a skip link goes into the content.

Next.js 16.4 announces the new page in an assertive live region, by its title or else its first `h1`, and leaves the focus alone: its source defers resetting the focus on navigation, and earlier versions moved it to the new segment's first element when that element took the focus. SvelteKit focuses `body` after every navigation unless the navigation asks to keep the focus, lets an `autofocus` element win, and reads the title from a live region.

## Decision

- **An announcer.** `RouterProvider` renders a visually hidden `<div aria-live="polite" aria-atomic="true" data-netjes-route-announcer="">` after the app's shell, inside the router's contexts, so neither the shell nor an error boundary of the app can remove it. It renders in the browser only, once the page has hydrated, so the server's HTML, hydration and a Content-Security-Policy are as before; React sets its styles through the CSSOM, which a `style-src` without `'unsafe-inline'` allows. It has no role. After a navigation to another path or query it takes the page's name, `document.title`, else the trimmed text of the first `h1`, else the path, and puts it in the region when it differs from the previous page's name. A later sibling of the shell, it runs its effect after the app's, so it reads what the new page has set.
- **The focus.** In the commit that shows the page, after it has scrolled, the router moves the focus by the first of these rules that applies:
  1. The element the fragment names takes the focus, on the same page or on another. One that takes no focus of its own gets `tabindex="-1"`, which goes again when it loses the focus.
  2. When the page key did not change, the focus stays: a query change, a refresh, a post that stays on its page, a modal that opens, closes or refreshes over the page under it. The page key is what remounts the page: its path, or for an intercepted page the key of the page under it.
  3. When an element other than `body` has the focus, and it is not the one that had it as the navigation started, the focus stays: the new page's `autoFocus`, the user or the app's code moved it.
  4. When the element that had the focus at the start still has it, it keeps it with `scroll={false}`, or when the user types into it (a text field, a `select`, editable content).
  5. Otherwise the focus starts over at the top of the document: `body` takes it, with `tabindex="-1"` for that moment, and gets its own `tabindex` back.
- Every focus call passes `preventScroll`, so the scroll position the router set stays, the one Back restores included. Back and Forward follow the same rules as a link.
- The router reads which element has the focus as a navigation starts, before its first `await`: inside the click or keypress that started it, before React has handled that event's own state updates, such as a menu that closes. `<Link>` and `<Form>` read it before the app's own `onClick` or `onSubmit`, which run as part of following the link or submitting the form.
- There is no new API. The rules leave the focus in fields, menus, modals, `autoFocus` elements and `scroll={false}` tabs, and a page that wants it elsewhere moves it in its own effect, which runs after the router's.
- The template's root layout and the Blog's start with a skip link to `<main id="content">`.

## Alternatives considered

- **The first `h1` inside `main`, else `main`.** A screen reader reads the heading at once, but the next Tab skips what comes before the heading inside `main`, such as a post's link back to the list, keyboard users see a focus ring on a heading, and the heading and the region both speak.
- **Leave the focus alone, as Next.js 16.4 does.** The focus would stay on a link in the header, or fall back to `body` with the next Tab starting from wherever the browser kept its place, and a link to a fragment would still only scroll.
- **Start the focus over after every navigation, with an option to keep it, as SvelteKit does.** A search box that navigates as the user types loses the focus at each keystroke unless every call passes the option.
- **Compare paths rather than page keys.** Back from a modal changes the path but not the page under it, and moved the focus from the link that opened the modal to the top.
- **Tie the focus to the scroll intent.** `scroll={false}` would have been the only way to keep the focus.
- **A focus option on `<Link>` and the router's methods.** It can come later without a break, if the rules leave a case uncovered.
- **Give the focus back, on Back and Forward, to the element that had it when the user left the entry.** The element has to be found again in a page that rendered anew, by an id or by its link and position. It can come later.
- **Leave links to a fragment of the page on screen to the browser.** Two code paths for one kind of link.
- **A custom element with a shadow root for the region.** DOM code outside React, for nothing a `div` does not do.
- **`role="status"` or `role="alert"` on the region.** An app's test that finds its own status or alert by its role would find two. An alert also interrupts what the screen reader is reading.
- **Announce every commit whose name differs from the last.** A page without a title would be announced again after each refresh that changes its `h1`, such as a count of entries.

## Consequences

- After a client navigation to another page, screen readers hear its name, and keyboard users start from the top of the page, as after a page load. A page whose name is the previous page's is not announced, so apps give their pages distinct titles.
- Keyboard users Tab through the header again after each navigation, unless the root layout starts with a skip link.
- A tab or pagination link inside a page goes with the page when it changes the path, so the focus starts over at the top, even with `scroll={false}`.
- A field in a layout keeps the focus after a navigation it started, also after a GET form posts to another path, where a page load would start the focus over.
- End-to-end tests that type right after a navigation without focusing a field, or that read the whole text of a container, need a change. An app's own announcer, and its own code that focuses a fragment's element, can go.
- When client navigations show a page in several commits, as the roadmap's instant loading plans, only the commit of the complete page may announce it or move the focus.
