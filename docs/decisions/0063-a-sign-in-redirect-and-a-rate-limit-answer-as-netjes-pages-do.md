# 0063 – A sign-in redirect and a rate limit answer as Netjes's pages do

Status: accepted; extends [ADR 0020](0020-page-actions-check-their-inputs-like-server-actions.md)

## Context

Two answers come from outside a page's endpoint, before any loader runs, and neither knew about Netjes.

- **Signing in.** Cookie authentication challenges a visitor with a redirect to its `LoginPath`. The client router followed it only with a full page load, and a server action's call got the sign-in page's HTML in place of a failure. The guides told apps to answer 401 from `OnRedirectToLogin`, so that `unauthorized.tsx` renders; an app that wanted a sign-in page with a URL to come back to executed `Page.Redirect` itself in the event, worked out which requests had no page to come back to, and built the return URL from the request, base path and encoding included.
- **Rate limits.** ASP.NET Core's rate limiter refuses a post with an empty status, which Netjes renders as the error page, so the visitor lost what they typed. A back office or a shop rendered its forms again from each policy's `OnRejected`, one `Page.Render` per form with the props built a second time, and read the fields to keep from the posted form itself.

## Decision

- **`Page.RedirectToSignIn(HttpContext context, string path, string returnUrlParameter = "returnUrl")`** returns the `Task` an authentication event returns: `cookie.Events.OnRedirectToLogin = context => Page.RedirectToSignIn(context.HttpContext, "/sign-in")`. It takes the `HttpContext`, not the cookie handler's context, so any scheme's event can use it.
  - It runs a `PageResult` redirect: a document gets a 307, or a 303 after a form post, and a client navigation a redirect payload.
  - A request below `/_netjes` (a server action, the token endpoint, an image) gets an empty 401, which `PageErrorMiddleware` already turns into an action failure or the 401 page. There is no page to come back to.
  - The URL to come back to is the request's path, encoded again as the browser sent it, and its query, without the base path, which `Page.LocalRedirect` adds back. When that is not a local URL by `Page.IsLocalUrl` (a request target that starts with `//`), it is left out. `path` must be a local URL, or the call throws.
  - The answer goes out `Cache-Control: no-store, private`: the same URL gives a signed-in visitor the page.
- **`Page.TooManyRequests(OnRejectedContext context, string? message = null)`** returns the `ValueTask` the limiter's `OnRejected` returns. It answers with 429, `Retry-After` from the lease, and `Cache-Control: no-store`.
  - A post to an action that passes the checks the action makes before it runs, its origin and its antiforgery token unless the action turned them off, is answered like a failed validation (ADR 0020): a page action's page renders again from its loader, and a server action's form posted without JavaScript renders the page it came from again, with the declared fields that are not private and the message as the form's error under `""`. The limiter comes before the action's filters, so the helper makes their checks itself; a post that fails them gets the empty 429.
  - The failed state in the payload now says its status, 422 or 429, and `usePageActionState()` and `useAction`'s first state report it. `createTestApp`'s `pageActionState` takes a status.
  - A script's call to a server action gets a failure with status 429 and the message, which throws a `ServerActionError`, as a 429 did before; the documented contract for script calls, where only a 422 resolves, stays.
  - Anything else gets an empty 429, as without the helper.

## Alternatives considered

- **The cookie handler's `RedirectContext` as the parameter.** It would read `LoginPath` and `ReturnUrlParameter` from the options, but bind the helper to cookies; a handler of another scheme, or an `IAuthorizationMiddlewareResultHandler`, has an `HttpContext`.
- **The cookie handler's own return URL.** It includes the base path and is built from the decoded path, so a `?` or `#` in a path segment would end it.
- **Redirecting a server action's call.** The client router would follow the redirect to a page in place of the action's answer; the caller needs a failure it can tell apart.
- **A 429 that resolves a server action's call as a failed state.** It would change what scripts get for a 429 today (`ServerActionError`), which apps catch, as the TaskBoard example does.
- **Rendering the page again for every refused post.** A post from another site, or without its token, would have its fields written into a page before any check, which a failed validation never does.
- **An option on `NetjesOptions` for the message.** The message often depends on the request, such as its language; the lambda already has the request.

## Consequences

- An app sends visitors to its sign-in page from one line, and drops the code that worked out the return URL and the requests without a page.
- A limited form keeps what was typed, with and without JavaScript, from one line; a page shows the limiter's message as it shows any form error. Each refused post that is handed back renders its page, loaders included.
- The rate limiter's middleware has to come after `UseNetjes()`, as the guides already place it: before routing, the helper sees no endpoint and answers an empty 429.
- A limit on the page's own `GET` refuses the page rendered again too; the guides put the limit on the action.
