# 0054 – Layout loaders around 404 and error pages are judged as the middleware judges their pages

Status: accepted; amends [ADR 0053](0053-an-unknown-url-stays-a-404-and-runs-only-the-layout-loaders-the-visitor-may-see.md)

## Context

ADR 0053 judges each layout loader on the chain of a page that no page endpoint has let its visitor in for as the authorization middleware would judge a file page in its folder. Checked against the middleware again, that rule still differed from it in four ways, and the record says one thing that ASP.NET Core does not do:

- A page with a loader (`MapPageLoader`) carries the conventions of the layout loaders above it and its mount's, but none of `MapPages()`'s, which reach file pages only. Under `MapPages().AllowAnonymous()`, the usual way to open the file pages under a fallback policy, a section's layout loader with `RequireAuthorization()` whose pages all have loaders passed as a file page and ran for a visitor every one of those pages refuses: on their 401, on an unknown URL's 404 at the root and below a mount, and on a status page from before authorization.
- Since ASP.NET Core 8 the middleware joins the requirements of `IAuthorizationRequirementData` metadata to an endpoint's policy, or uses them alone. Netjes read only `IAuthorizeData` and `AuthorizationPolicy`, so an `[Authorize]` attribute with requirements of its own counted as asking for a signed-in user, and requirements put on with `.WithMetadata()` as asking for nothing. The export, the sitemap and `netjes routes` looked for `IAuthorizeData` alone.
- The middleware's policy evaluator puts the identities of each later scheme a policy names first, and puts that user on the request while the handlers run. Netjes merged them in order and left the request's user in place, so a policy on the primary identity, or a handler that reads the request's user, decided differently; an app's own `IPolicyEvaluator` was not asked.
- The guides put `UseAuthentication()` and `UseAuthorization()` after `UseNetjes()`. The 413 for a body over the limit, an unsupported locale's 404 and a status from a middleware between the two render before the authentication middleware has put a user on the request, so a signed-in visitor was judged as nobody and lost every layout whose loader asks for a user, which they kept before ADR 0053.
- ADR 0053 says an unknown URL is a 404 with no challenge whatever guards the layouts. Under a fallback policy it is not: the fallback endpoint carries no authorization metadata, so the fallback policy covers it, and the middleware challenges a visitor it refuses before any page renders. Netjes's tests already expected that.

## Decision

- A layout loader runs on those chains only for a visitor who passes two judgments. One on what every page in its folder carries, whatever its kind: the conventions of the layout loaders down to it and those of the mount whose files the folder holds, which refuse nobody when they ask for no authorization. One on what a file page there carries, those and `MapPages()`'s, as ADR 0053 has it: an `AllowAnonymous()` on any of them lets every visitor in, and with none that asks for authorization the fallback policy applies.
- Authorization is read as the middleware reads it: `IAuthorizeData`, `AuthorizationPolicy` and the requirements of `IAuthorizationRequirementData`, joined into one policy or standing alone. Each of them counts as asking for authorization, on those chains and in the export, the sitemap and `netjes routes`.
- The policy goes through the app's `IPolicyEvaluator`, as in the middleware. The request's own user is put back once the policy is decided, so the layout loaders that run afterwards see it, as ADR 0053 has it.
- Where the authentication middleware has not run for the request (it has no `IAuthenticationFeature`) and the policy names no scheme, the visitor is judged on the user the default authenticate scheme signs in, as that middleware would have found them. If the scheme throws, the visitor is judged as having no user, and the page keeps its status.
- The status stays what it was. `MapPages()`'s conventions never challenge an unknown URL. A fallback policy covers the not-found fallback as it covers every endpoint without authorization metadata, so it challenges a visitor it refuses there, with a 401 or a login redirect, and Netjes leaves that to ASP.NET Core.
- An unknown URL authenticates the schemes that the policies on its chain name, as a page there would, so a sliding cookie of such a scheme can be renewed on the 404, which then carries its `Set-Cookie`.

## Alternatives considered

- **Only what every page carries, without `MapPages()`'s.** Under `MapPages().RequireAuthorization()` an unprotected root layout loader would again run for every visitor of an unknown URL, which ADR 0053 closed.
- **Let a loader in when some page below it lets the visitor in.** That needs the pages below each folder and their endpoints at request time, an unknown URL has none of its own, and one page's conventions say nothing about its siblings. Both judgments use what the loader's props reach on any page in its folder.
- **Ask apps to put `UseAuthentication()` before `UseNetjes()`.** The guides and the template put it after, and a signed-in visitor would lose the layouts on those pages until every app moved it.
- **Keep the default scheme's user on the request afterwards.** The loaders there would see the user they were judged as, but every later read of the request's user, the segment keys and the antiforgery token among them, would change on pages that ran without one before.
- **Let the default scheme's exception through.** The 413 and the 429 would lose the app's error page for Netjes's plain one, and the locale's 404 would become a 500, where an authentication handler that throws in `UseAuthentication()` gets the app's error page for a visitor without a user.
- **Mark the fallback endpoint `AllowAnonymous()`, so an unknown URL is a 404 under a fallback policy too.** It would exempt every unknown URL from the policy the app set for every endpoint without metadata of its own.

## Consequences

- An app that authorizes nothing sees no change.
- Under `MapPages().AllowAnonymous()`, a layout loader with `RequireAuthorization()` runs on those chains only for the visitors its own authorization lets in, also where a file page below it lets every visitor in.
- A signed-in visitor keeps the layouts on the 413, the locale's 404 and a status from a middleware ahead of `UseAuthentication()`, in the order the guides give; the loaders there run for the request's user, who has none, as they did before ADR 0053.
- `netjes export` stops at a page or an `IncludeInExport` endpoint whose metadata states a policy or requirements without an `[Authorize]`, the sitemap leaves such a page out, and `netjes routes` reports it as authorized.
- The cost stays on those pages: a second evaluation of the conventions for each layout loader on the chain, and on a page from before `UseAuthentication()` one authentication of the default scheme, whose handler authenticates once per request.
