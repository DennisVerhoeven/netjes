using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Netjes.Routing;

/// <summary>
/// Conventions for a layout loader. They also apply to every page endpoint below the layout, so
/// <c>RequireAuthorization()</c> on a layout's loader protects its whole section before any loader runs.
/// </summary>
public sealed class LayoutLoaderBuilder : IEndpointConventionBuilder
{
    internal LayoutLoaderBuilder(LayoutLoader loader) => Loader = loader;

    internal LayoutLoader Loader { get; }

    /// <summary>Adds a convention that runs when the endpoints under the layout are built.</summary>
    public void Add(Action<EndpointBuilder> convention) => Loader.Conventions.Add(convention);

    /// <summary>Adds a convention that runs after this layout's other conventions, on the same endpoints; a page's own conventions run after it.</summary>
    public void Finally(Action<EndpointBuilder> finallyConvention) => Loader.FinallyConventions.Add(finallyConvention);
}

/// <summary>A layout's C# loader: binds like a minimal API handler (route values, query, services) and returns a <see cref="PageResult"/>.</summary>
internal sealed class LayoutLoader(RouteSegment segment, Delegate handler, IServiceProvider services, Type? propsType = null)
{
    private const string ResultKey = "Netjes.LayoutLoaderResult";
    private readonly Lazy<RequestDelegate> _invoke = new(() => Build(segment, handler, services));

    public RouteSegment Segment { get; } = segment;

    /// <summary>The props type the loader's typed result names, for the generated <c>layouts</c> map.</summary>
    public Type? PropsType { get; } = propsType;

    /// <summary>
    /// Whether the user passes the authorization its conventions add (<c>RequireAuthorization(...)</c> and the like),
    /// evaluated the way the authorization middleware would for an endpoint carrying them.
    /// </summary>
    public async Task<bool> AuthorizesAsync(HttpContext http)
    {
        var probe = new RouteEndpointBuilder(null, RoutePatternFactory.Parse("/"), 0) { ApplicationServices = services };
        Conventions.ForEach(convention => convention(probe));
        FinallyConventions.ForEach(convention => convention(probe));
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

    /// <summary>The loader's method and how minimal API binding binds its parameters, inferred as for its request delegate.</summary>
    public (MethodInfo Method, IReadOnlyList<IParameterBindingMetadata> Parameters) InferParameterBindings()
    {
        var endpoint = NewEndpoint(services);
        RequestDelegateFactory.InferMetadata(handler.Method, BindingOptions(Segment, services, endpoint));
        return (handler.Method, [.. endpoint.Metadata.OfType<IParameterBindingMetadata>()]);
    }

    /// <summary>
    /// The loader's parameters that bind a route value outside the segment's URL (<c>[FromRoute(Name = "id")]</c> in
    /// the layout of <c>/team/[team]</c>), with the route parameter each names. Binding refuses them on every request,
    /// and the segment's key holds only the route values of its own URL.
    /// </summary>
    public static IReadOnlyList<(string Parameter, string Route)> RouteBindingsOutsideItsUrl(RouteSegment segment, Delegate handler, IServiceProvider services)
    {
        var endpoint = NewEndpoint(services);
        try
        {
            // Without the segment's route parameter names, which binding would refuse before they could be named.
            RequestDelegateFactory.InferMetadata(handler.Method, new RequestDelegateFactoryOptions
            {
                ServiceProvider = services,
                DisableInferBodyFromParameters = true,
                EndpointBuilder = endpoint,
            });
        }
        catch (Exception)
        {
            // Binding refuses the loader for another reason, which MapLayoutLoader reports next.
            return [];
        }

        return
        [
            .. endpoint.Metadata.OfType<IParameterBindingMetadata>()
                .Select(p => (Parameter: p.Name, Route: LoaderBinding.SourceOf(p, segment.Parameters).Route))
                .Where(b => b.Route is not null && !segment.Parameters.Contains(b.Route, StringComparer.OrdinalIgnoreCase))
                .Select(b => (b.Parameter, b.Route!)),
        ];
    }

    public List<Action<EndpointBuilder>> Conventions { get; } = [];
    public List<Action<EndpointBuilder>> FinallyConventions { get; } = [];

    public async Task<PageResult> InvokeAsync(HttpContext http)
    {
        http.Items.Remove(ResultKey);
        // The minimal API pipeline skips a handler once the response has an error status, which it may have here
        // (a page rendered for a failed form post or a 401); the loader runs as if it were the request's own.
        var status = http.Response.StatusCode;
        http.Response.StatusCode = StatusCodes.Status200OK;
        try
        {
            await _invoke.Value(http);
        }
        finally
        {
            http.Response.StatusCode = status;
        }

        return http.Items[ResultKey] switch
        {
            PageResult result => result,
            ITypedPageResult typed => typed.Inner,
            _ => throw new InvalidOperationException(
                $"The layout loader for '{Segment.Id}' must return a PageResult (Page.Props, Page.NotFound, Page.Redirect, ...)."),
        };
    }

    // RequestDelegateFactory gives the loader minimal API binding; a filter captures its result instead of writing it.
    private static RequestDelegate Build(RouteSegment segment, Delegate handler, IServiceProvider services)
    {
        var endpoint = NewEndpoint(services);
        endpoint.FilterFactories.Add((_, next) => async context =>
        {
            context.HttpContext.Items[ResultKey] = await next(context);
            return TypedResults.Empty;
        });
        return RequestDelegateFactory.Create(handler, BindingOptions(segment, services, endpoint)).RequestDelegate;
    }

    private static RouteEndpointBuilder NewEndpoint(IServiceProvider services) =>
        new(null, RoutePatternFactory.Parse("/"), 0) { ApplicationServices = services };

    // Route values are those of the segment's own URL prefix; other simple parameters bind from the query string.
    // No inferred body, as for the page's own GET endpoint: arrays bind from the query string too, on a page action's
    // render as well, and a complex type must say where it comes from.
    private static RequestDelegateFactoryOptions BindingOptions(RouteSegment segment, IServiceProvider services, EndpointBuilder endpoint) =>
        LoaderBinding.Options(services, segment.Parameters, endpoint);
}

/// <summary>The layout loaders by segment, and the conventions they lend to the pages below them.</summary>
internal sealed class LayoutLoaderRegistry(NetjesContext context)
{
    private readonly byte[] _secret = SecretFrom(context.Options.SegmentKeySecret);

    private string? _secretFingerprint;

    private readonly Dictionary<string, LayoutLoader> _bySegment = new(StringComparer.Ordinal);

    /// <summary>
    /// Names the secret that signs this instance's keys without giving it away: a keyed hash of a fixed label, which no
    /// key can equal (what a key hashes always has line breaks). Instances with the same secret have the same one.
    /// </summary>
    public string SecretFingerprint =>
        _secretFingerprint ??= Convert.ToHexString(HMACSHA256.HashData(_secret, "netjes-segment-key-secret"u8), 0, 16);

    private static byte[] SecretFrom(string? configured)
    {
        if (configured is not { Length: > 0 })
        {
            return RandomNumberGenerator.GetBytes(32);
        }

        var bytes = Encoding.UTF8.GetBytes(configured);
        // Issued keys are HMACs of guessable inputs, so a short secret could be found from them offline.
        return bytes.Length >= 32
            ? bytes
            : throw new InvalidOperationException(
                $"Netjes:SegmentKeySecret must be at least 32 bytes (it is {bytes.Length}). Use a random value, e.g. `openssl rand -base64 32`.");
    }
    private readonly Lock _gate = new();

    public int Version { get; private set; }

    public LayoutLoader Register(RouteSegment segment, Delegate handler, IServiceProvider services, Type? propsType = null)
    {
        lock (_gate)
        {
            if (_bySegment.ContainsKey(segment.Id))
            {
                throw new InvalidOperationException($"A layout loader for '{segment.Id}' is already mapped.");
            }

            var loader = new LayoutLoader(segment, handler, services, propsType);
            _bySegment[segment.Id] = loader;
            Version++;
            return loader;
        }
    }

    public IReadOnlyList<LayoutLoader> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _bySegment.Values.OrderBy(l => l.Segment.Id, StringComparer.Ordinal)];
            }
        }
    }

    public LayoutLoader? Find(string segmentId)
    {
        lock (_gate)
        {
            return _bySegment.GetValueOrDefault(segmentId);
        }
    }

    /// <summary>Applies the conventions of every layout loader above <paramref name="route"/>, outermost first.</summary>
    public void ApplyConventions(PageRoute route, EndpointBuilder builder)
    {
        foreach (var loader in route.Segments.Select(s => Find(s.Id)).OfType<LayoutLoader>())
        {
            loader.Conventions.ForEach(convention => convention(builder));
            loader.FinallyConventions.ForEach(convention => convention(builder));
        }
    }

    /// <summary>
    /// Identifies what a segment's loader saw: its folder, the route values in its URL prefix, the signed-in user
    /// and the build. The client sends back the keys it holds, and a loader whose key matches is not run again.
    /// Keys are signed (HMAC), so a client cannot make up one for a segment it was never given. Each field of the signed
    /// text carries its length, so no value can pass for others: a route value with a line break (<c>%0A</c> in the
    /// path) for two route values, a user name for the name and the identifier, a role with a comma for two roles.
    /// </summary>
    public string KeyFor(RouteSegment segment, HttpContext http, string version)
    {
        var user = http.User;
        var text = new StringBuilder();
        Field(text, segment.Id);
        Field(text, version);
        Field(text, user.Identity?.AuthenticationType);
        Field(text, user.Identity?.Name);
        Field(text, user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value);
        // A role granted or revoked runs the loader again, so checks inside it see the change.
        var roles = user.Identities.SelectMany(i => i.FindAll(i.RoleClaimType)).Select(c => c.Value).Order(StringComparer.Ordinal).ToList();
        Field(text, roles.Count.ToString(CultureInfo.InvariantCulture));
        roles.ForEach(role => Field(text, role));
        foreach (var parameter in segment.Parameters)
        {
            Field(text, http.Request.RouteValues.TryGetValue(parameter, out var value) ? value?.ToString() : null);
        }

        return Convert.ToBase64String(HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(text.ToString())), 0, 16)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>One field of a key's signed text: its length and value (<c>-</c> for none), and a line break.</summary>
    private static void Field(StringBuilder text, string? value) =>
        (value is null ? text.Append('-') : text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value)).Append('\n');
}
