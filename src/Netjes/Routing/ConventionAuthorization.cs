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
    /// endpoint carrying them.
    /// </summary>
    public static async Task<bool> AuthorizesAsync(
        HttpContext http,
        IServiceProvider services,
        IReadOnlyList<Action<EndpointBuilder>> conventions,
        IReadOnlyList<Action<EndpointBuilder>> finallyConventions)
    {
        var probe = new RouteEndpointBuilder(null, RoutePatternFactory.Parse("/"), 0) { ApplicationServices = services };
        foreach (var convention in conventions.Concat(finallyConventions))
        {
            convention(probe);
        }

        var data = probe.Metadata.OfType<IAuthorizeData>().ToList();
        var policies = probe.Metadata.OfType<AuthorizationPolicy>().ToList();
        if ((data.Count == 0 && policies.Count == 0) || probe.Metadata.OfType<IAllowAnonymous>().Any())
        {
            return true;
        }

        var provider = http.RequestServices.GetRequiredService<IAuthorizationPolicyProvider>();
        if (await AuthorizationPolicy.CombineAsync(provider, data, policies) is not { } policy)
        {
            return true;
        }

        var result = await http.RequestServices.GetRequiredService<IAuthorizationService>().AuthorizeAsync(http.User, http, policy);
        return result.Succeeded;
    }
}
