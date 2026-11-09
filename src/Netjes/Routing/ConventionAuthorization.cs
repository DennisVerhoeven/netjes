using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
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
        return !metadata.Any(m => m is IAuthorizeData or AuthorizationPolicy) || await AuthorizesAsync(http, metadata);
    }

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
    /// else the fallback policy, unless it allows anonymous users, against the user the policy's schemes authenticate.
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
        // Without authorization services nothing is authorized, and only an endpoint that asks for none can be served.
        var provider = data.Count == 0 && policies.Count == 0
            ? http.RequestServices.GetService<IAuthorizationPolicyProvider>()
            : http.RequestServices.GetRequiredService<IAuthorizationPolicyProvider>();
        if (provider is null || await AuthorizationPolicy.CombineAsync(provider, data, policies) is not { } policy)
        {
            return true;
        }

        var user = await UserForAsync(http, policy);
        var result = await http.RequestServices.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, http, policy);
        return result.Succeeded;
    }

    /// <summary>
    /// The user a policy is checked against, as the authorization middleware's policy evaluator finds them without
    /// replacing the request's: for a policy that names authentication schemes, what those schemes authenticate, merged,
    /// or a user without an identity when none does; else the request's user.
    /// </summary>
    private static async Task<ClaimsPrincipal> UserForAsync(HttpContext http, AuthorizationPolicy policy)
    {
        if (policy.AuthenticationSchemes.Count == 0)
        {
            return http.User;
        }

        var identities = new List<ClaimsIdentity>();
        foreach (var scheme in policy.AuthenticationSchemes)
        {
            if (await http.AuthenticateAsync(scheme) is { Succeeded: true, Principal: { } principal })
            {
                identities.AddRange(principal.Identities);
            }
        }

        return identities.Count == 0 ? new ClaimsPrincipal(new ClaimsIdentity()) : new ClaimsPrincipal(identities);
    }
}
