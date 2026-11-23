# 0059 – A not-found's layout loaders run for the user their policies sign in

Status: accepted; amends [ADR 0053](0053-an-unknown-url-stays-a-404-and-runs-only-the-layout-loaders-the-visitor-may-see.md) and [ADR 0054](0054-layout-loaders-around-404-and-error-pages-are-judged-as-the-middleware-judges-their-pages.md)

## Context

On the 404 for an unknown URL, at the root or below a page package's mount, and on the other pages of endpoints that serve no page, Netjes judges each layout loader on the chain before any of them runs, as the authorization middleware would judge the pages in its folder (ADRs 0053 and 0054). A policy that names authentication schemes is judged against the user those schemes authenticate. Then the request's own user was put back, and the loaders ran for it.

On a page the middleware does not put it back: the user that the page's policy's schemes authenticate stays on the request, and the page's loaders, the segment keys and the antiforgery token see that user. The two differed wherever a policy on the chain names a scheme that is not the default one.

That is how a back office protects itself: its own cookie scheme, a policy that names it on the layout loader of its section, and the site's scheme as the default. A second page package mounted inside that section has its unknown URLs rendered on the chain through the section. The section's loader was judged on the back office's user and let them in, then ran for the request's own user, found nobody, and ended the staffer's session or sent them to sign in. A package could only avoid it with catch-all pages of its own behind the back office's policy.

## Decision

- On the chains Netjes authorizes itself, once the chain is judged, it puts on the request the user the middleware would put there for a page in the deepest folder the visitor passed. These are the chains of endpoints that serve no page: an unknown URL's not-found at the root or below a mount, the status and error pages of a server action or a minimal API. The not-found of an app's catch-all on a mount's chain counts too. The policy is built from the conventions of the layout loaders down to the first segment that refused the visitor and of the mounts whose files those segments hold, with `MapPages()`'s once a layout loader was judged with them. It goes through the app's `IPolicyEvaluator`, which authenticates the schemes the policy names and merges the users they give, as in the middleware.
- That user stays for the rest of the request, as after the middleware: the layout loaders run for it, and the segment keys and the antiforgery token in the payload are built for it.
- A chain whose policies name no scheme keeps the request's user, so an app whose policies name none sees no change. The default scheme's user that ADR 0054 judges where `UseAuthentication()` has not run is still not kept.
- A page's own chain is unchanged: its 401 or 403 page, and its status and error pages from before its authorization ran. The middleware has put the page's user on the request there, or ADR 0054's rule applies.

## Alternatives considered

- **The user on the request only while the loaders run.** The loaders would see the right user, but the segment keys would name another user than the page's. A client navigation from a page in the section would run every layout loader above again, and the antiforgery token in the 404's payload would be for a user the actions there do not see.
- **Each loader with the user its own judgment authenticated.** A page runs all its loaders for one user, the merge of every scheme on its way. Loaders on one chain that saw different users would differ from the page.
- **Leave it to the section's loader**, which could read its own scheme when the request's user is not its own. Every package mounted in a section would depend on that loader doing so, and the next section would meet the same bug.

## Consequences

- A package mounted inside a section behind a policy that names a scheme renders its not-found inside that section for the users the section lets in, and the section's loaders run for them as on its pages. Catch-all pages that worked around this can go.
- An app whose root layout loader requires a policy that names a scheme other than the default runs it on an unknown URL for that scheme's user, as on its pages, where it ran for the default scheme's user before.
- The cost is one more authentication pass per chain, which each scheme's handler answers from what it authenticated earlier in the request.
