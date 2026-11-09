using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Netjes.Routing;

/// <summary>The authorization that endpoint conventions add, for what carries conventions without being an endpoint.</summary>
internal static class ConventionAuthorization
{
    /// <summary>
    /// Whether the user passes the authorization <paramref name="conventions"/> and <paramref name="finallyConventions"/>
    /// add (<c>RequireAuthorization(...)</c> and the like), evaluated the way the authorization middleware would for an
    /// endpoint carrying them. Conventions that add none refuse nobody: the fallback policy is for endpoints.
    /// </summary>
    public static async Task<bool> AuthorizesAsync(
        HttpContext http,
        IServiceProvider services,
        IReadOnlyList<Action<EndpointBuilder>> conventions,
        IReadOnlyList<Action<EndpointBuilder>> finallyConventions)
    {
        var metadata = MetadataOf(services, conventions.Concat(finallyConventions));
        return !AsksForAuthorization(metadata) || await AuthorizesAsync(http, metadata);
    }

    /// <summary>
    /// Whether an endpoint carrying <paramref name="metadata"/> asks for authorization of its own, as the authorization
    /// middleware reads it: an <see cref="IAuthorizeData"/>, an <see cref="AuthorizationPolicy"/>, or requirements its
    /// metadata states (<see cref="IAuthorizationRequirementData"/>). An endpoint that asks for none gets only the fallback policy.
    /// </summary>
    public static bool AsksForAuthorization(IEnumerable<object> metadata) =>
        metadata.Any(m => m is IAuthorizeData or AuthorizationPolicy or IAuthorizationRequirementData);

    /// <summary>The metadata <paramref name="conventions"/> give an endpoint, in their order.</summary>
    public static IReadOnlyList<object> MetadataOf(IServiceProvider services, IEnumerable<Action<EndpointBuilder>> conventions)
    {
        var probe = new RouteEndpointBuilder(null, RoutePatternFactory.Parse("/"), 0) { ApplicationServices = services };
        foreach (var convention in conventions)
        {
            convention(probe);
        }

        return [.. probe.Metadata];
    }

    /// <summary>
    /// Whether the user passes the authorization that an endpoint carrying <paramref name="metadata"/> asks for, evaluated
    /// the way the authorization middleware would: its <see cref="IAuthorizeData"/> and <see cref="AuthorizationPolicy"/>,
    /// else the fallback policy, joined by the requirements its metadata states (<see cref="IAuthorizationRequirementData"/>),
    /// unless it allows anonymous users, through the app's <see cref="IPolicyEvaluator"/>: against the user the policy's
    /// schemes authenticate, else the request's.
    /// </summary>
    public static async Task<bool> AuthorizesAsync(HttpContext http, IEnumerable<object> metadata)
    {
        var items = metadata as IReadOnlyCollection<object> ?? [.. metadata];
        if (items.OfType<IAllowAnonymous>().Any())
        {
            return true;
        }

        var data = items.OfType<IAuthorizeData>().ToList();
        var policies = items.OfType<AuthorizationPolicy>().ToList();
        var requirements = items.OfType<IAuthorizationRequirementData>().SelectMany(r => r.GetRequirements()).ToList();
        // Without authorization services nothing is authorized, and only an endpoint that asks for none can be served.
        var provider = data.Count == 0 && policies.Count == 0 && requirements.Count == 0
            ? http.RequestServices.GetService<IAuthorizationPolicyProvider>()
            : http.RequestServices.GetRequiredService<IAuthorizationPolicyProvider>();
        if (provider is null)
        {
            return true;
        }

        var policy = await AuthorizationPolicy.CombineAsync(provider, data, policies);
        if (requirements.Count > 0)
        {
            // As the middleware does: the stated requirements join the policy, or stand alone.
            var stated = new AuthorizationPolicyBuilder().AddRequirements([.. requirements]).Build();
            policy = policy is null ? stated : AuthorizationPolicy.Combine(policy, stated);
        }

        if (policy is null)
        {
            return true;
        }

        // Through the app's policy evaluator, as the middleware does: it authenticates the schemes the policy names, merged
        // as there, and puts the user they give on the request while the handlers run, which some read there. The
        // request's own user is back once the policy is decided.
        var requestUser = http.User;
        try
        {
            var evaluator = http.RequestServices.GetService<IPolicyEvaluator>()
                ?? new PolicyEvaluator(http.RequestServices.GetRequiredService<IAuthorizationService>());
            var authenticated = await evaluator.AuthenticateAsync(policy, http);
            return (await evaluator.AuthorizeAsync(policy, authenticated, http, http)).Succeeded;
        }
        finally
        {
            http.User = requestUser;
        }
    }
}
