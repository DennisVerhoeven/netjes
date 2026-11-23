# 0066 – Cookies the app names change without reloading every tab

Status: accepted; extends [ADR 0014](0014-signing-in-or-out-clears-what-back-shows.md) and [ADR 0021](0021-signing-in-or-out-reaches-the-other-tabs.md)

## Context

Under ADR 0014 and ADR 0021, a client call to a server action or page action whose response sets or deletes any cookie but the antiforgery cookie is marked `X-Netjes-Cookies: changed`. The router then forgets the pages it keeps for Back and Forward, starts over what is on its way, clears the query cache and loads the page on screen again, and every other tab of the app does the same. That is right for signing in and out, the case those records are about, and for any cookie that changes what pages show. A shop's cart cookie, written at the first add to the cart, changes nothing another tab shows, yet it reloads the page the customer is on and costs a request in every open tab. An app could only avoid it by writing such a cookie as rarely as it can, once when the cart is made and never again.

## Decision

- `NetjesOptions.NoReloadCookies` lists cookie names. A cookie in the list does not count, as the antiforgery cookie does not: a response that writes only these, or these and the antiforgery cookie, is not marked. One cookie outside the list marks it, as before. Names are read as browsers read them (up to the first `=`, without the spaces and tabs around it) and compared with their case.
- The cookie of every cookie authentication scheme counts whatever the list says: `CookieAuthenticationOptions.Cookie.Name` of each scheme whose handler is ASP.NET Core's cookie handler, ASP.NET Core Identity's schemes included. It is compared without its case, as ASP.NET Core reads the request's cookies. The schemes are looked up when the response starts, and only for a cookie the list names.
- The antiforgery cookie never counts on its own, listed or not.

## Alternatives considered

- **An opt-out per response**, such as `Page.Props(...).WithCookie(name, value, reload: false)` or a call in the action. Any action could then hide any cookie, a sign-in among them, and the choice would be spread over every action that writes the cookie. A cookie that changes nothing on another tab changes nothing whichever action writes it, so the name is the right place.
- **A list that may name the sign-in cookie.** An app that named it would leave the other tabs, and Back, showing the previous user's pages after a sign-out: what ADR 0014 and ADR 0021 prevent.
- **Stopping the app at startup when the list names a sign-in cookie.** Schemes can be added after startup (`IAuthenticationSchemeProvider.AddScheme`) and their options read again, so only a check when the cookie is written always holds.
- **Name patterns or prefixes.** More to get wrong, and a pattern can take in cookies the app did not mean. A list of names covers a cookie whose name the app knows.

## Consequences

- An action that writes only listed cookies is treated as one that writes none: the router keeps its pages, its prefetches and the query cache, refreshes nothing unless the action asks (`.Refresh()`, `.Revalidate(...)`, a redirect), and the other tabs are not told. When the page on screen shows what the cookie changed, the action returns it or asks for a refresh.
- An app has nothing to do: the list is empty by default, and every cookie counts as before.
- A sign-in cookie in the list keeps marking its responses, without a warning. Netjes knows the names of cookie authentication's cookies only: a cookie that holds who is signed in for another handler, or for the app's own code, is the app's to keep out of the list.
- A cookie whose name holds a value, such as one per store, is listed under every name it can have, or given one name.
