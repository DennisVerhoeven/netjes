using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Netjes.Routing;

/// <summary>
/// Where a loader's parameters take their values from, read from the binding minimal APIs infer for them, and whether
/// minimal APIs bind a loader's or an action's parameters at all.
/// </summary>
internal static class LoaderBinding
{
    /// <summary>The route parameters that some of the loader's parameters bind.</summary>
    public static IEnumerable<string> RouteParametersBound(
        IEnumerable<IParameterBindingMetadata> parameters, IReadOnlyList<string> routeParameters) =>
        parameters.Select(p => SourceOf(p, routeParameters).Route).OfType<string>();

    /// <summary>
    /// Where minimal API binding takes a parameter's value from, as far as the route and the query string go: attributes
    /// first, then (for strings and types with <c>TryParse</c>, without <c>BindAsync</c>) the route when the pattern has
    /// a parameter of that name in any case, else the query string. Arrays of those (and <c>StringValues</c>) bind from
    /// the query string: page loaders are GET endpoints, and layout loaders infer no body either.
    /// </summary>
    public static (string? Route, bool Query) SourceOf(IParameterBindingMetadata parameter, IReadOnlyList<string> routeParameters)
    {
        var attributes = Attribute.GetCustomAttributes(parameter.ParameterInfo);
        if (attributes.OfType<IFromRouteMetadata>().FirstOrDefault() is { } route)
        {
            return (route.Name ?? parameter.Name, false);
        }

        if (attributes.Any(IsBindingAttribute) || parameter.HasBindAsync || !parameter.HasTryParse)
        {
            return (null, false);
        }

        var type = parameter.ParameterInfo.ParameterType;
        if (type.IsArray || type == typeof(StringValues) || type == typeof(StringValues?))
        {
            return (null, true);
        }

        return routeParameters.Contains(parameter.Name, StringComparer.OrdinalIgnoreCase) ? (parameter.Name, false) : (null, true);
    }

    /// <summary>
    /// How a loader's request delegate binds: like a minimal API GET handler, from the given route parameters (null when
    /// they are not known, as minimal APIs take them when they build no endpoint), the query string and services, and
    /// never from an inferred body.
    /// </summary>
    public static RequestDelegateFactoryOptions Options(IServiceProvider services, IEnumerable<string>? routeParameters, EndpointBuilder endpoint) => new()
    {
        ServiceProvider = services,
        RouteParameterNames = routeParameters,
        DisableInferBodyFromParameters = true,
        EndpointBuilder = endpoint,
    };

    /// <summary>
    /// Stops the app when minimal API binding refuses a loader, naming it (<paramref name="loader"/>, e.g. "The loader
    /// of app/blog/page.tsx (/blog)"). ASP.NET Core infers the binding when it first builds the endpoints, on the
    /// first request, and an endpoint it cannot build fails every request of the app (a layout loader's request
    /// delegate, built on the first request of its section, every request of the section); here it is inferred at
    /// startup, with the loader's own options. A loader that binds and reads the request body all the same stops the
    /// app too (<see cref="RequireNoBody"/>), and so does one that takes from services a collection meant for the query
    /// string (<see cref="RequireNoCollectionsFromServices"/>) and, when <paramref name="routed"/> (a page loader, which
    /// requests are routed to, while a layout loader is called as a page renders), one whose types add accepts metadata
    /// for themselves (<see cref="RequireNoAcceptsMetadata"/>).
    /// </summary>
    public static void RequireBindable(string loader, MethodInfo method, IServiceProvider services, IEnumerable<string>? routeParameters, bool routed)
    {
        var endpoint = NewEndpoint(services);
        RequireBindable(loader, method, Options(services, routeParameters, endpoint), () => InferredBody(loader, method, services, routeParameters));
        RequireNoBody(loader, method, endpoint);
        if (routed)
        {
            RequireNoAcceptsMetadata(loader, method, endpoint, services);
        }

        RequireNoCollectionsFromServices(loader, method, endpoint, services);
    }

    /// <summary>
    /// Stops the app when a page loader's endpoint has accepts metadata while none of its parameters reads the body: a
    /// parameter's type, or the loader's result type, added it for itself (<c>IEndpointParameterMetadataProvider</c>,
    /// <c>IEndpointMetadataProvider</c>). Routing sends a request without a Content-Type to an endpoint without accepts
    /// metadata when one matches its URL, and the not-found fallback matches every URL, so every navigation would get the
    /// not-found page. The types that add it are found by putting each parameter's type to minimal API binding on its own.
    /// </summary>
    private static void RequireNoAcceptsMetadata(string loader, MethodInfo method, EndpointBuilder endpoint, IServiceProvider services)
    {
        // Routing reads the last of them.
        if (endpoint.Metadata.OfType<IAcceptsMetadata>().LastOrDefault() is not { } accepts)
        {
            return;
        }

        var adding = endpoint.Metadata.OfType<IParameterBindingMetadata>().Where(p => AddsAcceptsMetadata(p.ParameterInfo.ParameterType, services)).ToList();
        var subject = adding.Count switch
        {
            0 => $"return {TypeName(PagePropsMetadata.UnwrapAwaitable(method.ReturnType))}: its type adds",
            1 => $"bind {Named(adding, method)}: its type adds",
            _ => $"bind {Named(adding, method)}: their types add",
        };
        var contentTypes = accepts.ContentTypes.Count == 0 ? "a Content-Type it accepts" : $"a Content-Type of {Join(accepts.ContentTypes, "or")}";
        throw new InvalidOperationException(
            $"{loader} cannot {subject} accepts metadata (IAcceptsMetadata) for a request body, so routing matches the page only for requests with {contentTypes}, and every navigation, a GET request without a body, gets the not-found page. " +
            "Take a type that binds from the route or the query string without adding accepts metadata; read posted data in a page action (MapPageAction) or a server action (MapAction).");
    }

    /// <summary>Whether a loader parameter of this type, with no attributes, gives the endpoint accepts metadata.</summary>
    private static bool AddsAcceptsMetadata(Type type, IServiceProvider services)
    {
        var endpoint = NewEndpoint(services);
        return AloneFor(type) is { } alone && Infers(alone, Options(services, [], endpoint)) && endpoint.Metadata.OfType<IAcceptsMetadata>().Any();
    }

    /// <summary>
    /// Stops the app when a loader takes, without a binding attribute, a collection interface of .NET whose elements bind
    /// from a string (<c>IEnumerable&lt;int&gt;</c>, where <c>int[]</c> binds from the query string) and which the
    /// service container supplies whether or not it is registered. Minimal APIs bind it from services, which give an
    /// empty one, so the query string the loader meant goes unread on every request. Microsoft's container supplies any
    /// <c>IEnumerable&lt;T&gt;</c>, and some others more interfaces: which ones is asked of the container, as minimal
    /// APIs ask it, for an element type no app registers. A collection interface the container does not supply on its
    /// own binds from services when it is registered (<c>IReadOnlyList&lt;string&gt;</c>); an <c>IEnumerable&lt;T&gt;</c>
    /// meant as a service needs <c>[FromServices]</c>, as the message says.
    /// </summary>
    private static void RequireNoCollectionsFromServices(string loader, MethodInfo method, EndpointBuilder endpoint, IServiceProvider services)
    {
        // Without it minimal APIs infer no services, and take a collection interface for a body, which loaders refuse.
        if (services.GetService<IServiceProviderIsService>() is not { } container
            || endpoint.Metadata.OfType<IParameterBindingMetadata>().FirstOrDefault(p =>
                !Attribute.GetCustomAttributes(p.ParameterInfo).Any(IsBindingAttribute)
                && SuppliedUnregistered(p.ParameterInfo.ParameterType, container, services)) is not { } parameter)
        {
            return;
        }

        var element = TypeName(parameter.ParameterInfo.ParameterType.GenericTypeArguments[0]);
        throw new InvalidOperationException(
            $"{loader} cannot bind {Named([parameter], method)}: minimal APIs take it from the app's services, which supply an empty one when none is registered, never from the query string. " +
            $"Take {element}[] instead (or [FromQuery] {element}[]), which binds from repeated query string values, or mark it [FromServices] if a service is meant.");
    }

    /// <summary>
    /// Whether the type is a collection interface of .NET of one element type that binds from a string, which the
    /// container supplies for an element type nothing registers.
    /// </summary>
    private static bool SuppliedUnregistered(Type type, IServiceProviderIsService container, IServiceProvider services)
    {
        if (!type.IsInterface || type.GenericTypeArguments is not [var element] || ElementOf(type) != element
            || !container.IsService(type) || !BindsFromAString(element, services))
        {
            return false;
        }

        try
        {
            return container.IsService(type.GetGenericTypeDefinition().MakeGenericType(typeof(Unregistered)));
        }
        catch (ArgumentException)
        {
            // A constraint on the element type that Unregistered does not meet.
            return false;
        }
    }

    /// <summary>A type no app registers as a service.</summary>
    private sealed class Unregistered;

    /// <summary>Whether minimal APIs bind the type from a string (a string, or a type with <c>TryParse</c>), and so an array of it from the query string.</summary>
    private static bool BindsFromAString(Type type, IServiceProvider services)
    {
        var endpoint = NewEndpoint(services);
        return AloneFor(type) is { } alone && Infers(alone, Options(services, [], endpoint))
            && endpoint.Metadata.OfType<IParameterBindingMetadata>().Any(p => p.HasTryParse);
    }

    /// <summary>
    /// Stops the app when a loader reads the request body although none is inferred for it: a <c>[FromBody]</c> or
    /// <c>[FromForm]</c> parameter, or a form, a file or files, top level or a member of an <c>[AsParameters]</c> type.
    /// Minimal APIs bind them on a GET endpoint too, and refuse every navigation, which has no body: the page would answer
    /// its not-found page, and a layout loader would fail every page below the layout. That the loader reads a body is what
    /// minimal APIs inferred for it (<paramref name="endpoint"/>'s accepts metadata). Which parameters do is not: form
    /// fields and files share one entry, so they are named by the attributes and types minimal APIs read from the body.
    /// </summary>
    private static void RequireNoBody(string loader, MethodInfo method, EndpointBuilder endpoint)
    {
        if (!endpoint.Metadata.OfType<IAcceptsMetadata>().Any())
        {
            return;
        }

        var bodies = endpoint.Metadata.OfType<IParameterBindingMetadata>().Where(p => ReadsTheBody(p.ParameterInfo)).ToList();
        if (bodies.Count == 0)
        {
            // Accepts metadata a type added for itself, which a layout loader may have and a page loader may not
            // (RequireNoAcceptsMetadata).
            return;
        }

        var (they, them) = bodies.Count == 1 ? ("it reads", "it") : ("they read", "them");
        throw new InvalidOperationException(
            $"{loader} cannot bind {Named(bodies, method)}: {they} the request body, which loaders do not read, as a navigation is a GET request without one. " +
            $"Bind {them} from the route or the query string, or take {them} from a service; read posted data in a page action (MapPageAction) or a server action (MapAction).");
    }

    /// <summary>
    /// The loader's parameters as a message names them: <c>parameter 'q' of type string</c>, or for a member of an
    /// <c>[AsParameters]</c> type, <c>'Q' of type string, a member of its [AsParameters] type Filter</c>.
    /// </summary>
    private static string Named(IEnumerable<IParameterBindingMetadata> parameters, MethodInfo method) =>
        Join(parameters.Select(p => p.ParameterInfo.Member.Equals(method)
            ? $"parameter '{p.Name}' of type {TypeName(p.ParameterInfo.ParameterType)}"
            : $"'{p.Name}' of type {TypeName(p.ParameterInfo.ParameterType)}, a member of its [AsParameters] type {TypeName(p.ParameterInfo.Member.DeclaringType!)}"));

    /// <summary>
    /// Whether minimal APIs read the parameter from the request body: <c>[FromBody]</c> or <c>[FromForm]</c>, unless a
    /// route, query string or header attribute comes first, or else a form, a file or files without a binding attribute.
    /// </summary>
    private static bool ReadsTheBody(ParameterInfo parameter)
    {
        var attributes = Attribute.GetCustomAttributes(parameter);
        if (attributes.Any(a => a is IFromRouteMetadata or IFromQueryMetadata or IFromHeaderMetadata))
        {
            return false;
        }

        if (attributes.Any(a => a is IFromBodyMetadata or IFromFormMetadata))
        {
            return true;
        }

        var type = parameter.ParameterType;
        return !attributes.Any(IsBindingAttribute)
            && (type == typeof(IFormFile) || type == typeof(IFormFileCollection) || type == typeof(IFormCollection));
    }

    /// <summary>
    /// Stops the app when minimal API binding refuses a loader or action (<paramref name="handler"/>) with these options,
    /// with what <paramref name="explain"/> says, or else minimal APIs' own reason.
    /// </summary>
    public static void RequireBindable(string handler, MethodInfo method, RequestDelegateFactoryOptions options, Func<string?>? explain = null)
    {
        try
        {
            RequestDelegateFactory.InferMetadata(method, options);
        }
        catch (Exception ex)
        {
            throw explain?.Invoke() is { } message
                ? new InvalidOperationException(message)
                : new InvalidOperationException($"{handler} cannot bind its parameters: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// What to say when binding refuses the loader because it would read parameters from the request body, which a
    /// loader does not read; null when that is not why. Each parameter's type is put to minimal API binding on its own
    /// (<see cref="ReadsFromTheBody"/>). A member of an <c>[AsParameters]</c> type is found by inferring the loader once
    /// more with a body allowed, which names the body's type when there is one.
    /// </summary>
    private static string? InferredBody(string loader, MethodInfo method, IServiceProvider services, IEnumerable<string>? routeParameters)
    {
        const string Why = "it is not a registered service, has no binding attribute and does not bind itself, so minimal APIs would read it from the request body, which loaders do not read.";
        var bodies = method.GetParameters()
            .Where(p => !Attribute.GetCustomAttributes(p).Any(IsBindingAttribute) && ReadsFromTheBody(p.ParameterType, services))
            .Select(p => (Name: p.Name ?? "", p.ParameterType))
            .ToList();
        if (bodies is [var (name, type), .. var others])
        {
            return $"{loader} cannot bind parameter '{name}' of type {TypeName(type)}: {Why} {Fixes(type, services, member: false)}" +
                (others.Count > 0 ? $" Minimal APIs would read {Join(others.Select(o => $"'{o.Name}' of type {TypeName(o.ParameterType)}"))} from the request body too." : "");
        }

        var endpoint = NewEndpoint(services);
        if (!Infers(method, new RequestDelegateFactoryOptions { ServiceProvider = services, RouteParameterNames = routeParameters, EndpointBuilder = endpoint }))
        {
            return null;
        }

        // A [FromServices] member may have the body's type too.
        if (endpoint.Metadata.OfType<IAcceptsMetadata>().FirstOrDefault()?.RequestType is not { } body
            || endpoint.Metadata.OfType<IParameterBindingMetadata>().FirstOrDefault(p => !p.ParameterInfo.Member.Equals(method)
                && p.ParameterInfo.ParameterType == body && !Attribute.GetCustomAttributes(p.ParameterInfo).Any(IsBindingAttribute)) is not { } member)
        {
            return null;
        }

        return $"{loader} cannot bind '{member.Name}' of type {TypeName(body)}, a member of its [AsParameters] type {TypeName(member.ParameterInfo.Member.DeclaringType!)}: {Why} " +
            Fixes(body, services, member: true);
    }

    /// <summary>
    /// The fixes that fit a type minimal APIs would read from the body: the array a collection could be; a service for a
    /// type of .NET, an interface or an abstract type; else <c>[AsParameters]</c> (not for a <paramref name="member"/> of
    /// an <c>[AsParameters]</c> type, as nested <c>[AsParameters]</c> is not supported), <c>BindAsync</c> and, for a
    /// class, a service. What binds a <c>Point?</c> binds a <c>Point</c>, but only a <c>Point?</c> service binds it.
    /// </summary>
    private static string Fixes(Type type, IServiceProvider services, bool member)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (ElementOf(target) is { } element)
        {
            var owner = Nullable.GetUnderlyingType(element) ?? element;
            if (Binds(element.MakeArrayType(), services))
            {
                return $"Take {TypeName(element)}[] instead, which binds from repeated query string values.";
            }

            return IsTheAppsOwn(owner)
                ? $"An array binds from repeated query string values only when its element type binds from a string: give {TypeName(owner)} a static TryParse method{(target.IsArray ? "" : $" and take {TypeName(element)}[] instead")}."
                : "Read the values with a type of your own that has a static BindAsync method.";
        }

        if (!IsTheAppsOwn(target))
        {
            return $"Register {TypeName(type)} as a service, or take a type of your own that has a static BindAsync method instead.";
        }

        if (target.IsInterface || target.IsAbstract)
        {
            return $"Register {TypeName(type)} as a service, or give {TypeName(target)} a static BindAsync method to bind it yourself.";
        }

        var fixes = new List<string>();
        if (!member)
        {
            fixes.Add($"mark the parameter [AsParameters] to bind the properties of {TypeName(target)} from the route and the query string");
        }

        fixes.Add($"give {TypeName(target)} a static BindAsync method to bind it yourself");
        if (!target.IsValueType)
        {
            fixes.Add($"register {TypeName(type)} as a service");
        }

        var text = fixes.Count == 1 ? fixes[0] : $"{string.Join(", ", fixes[..^1])}, or {fixes[^1]}";
        return $"{char.ToUpperInvariant(text[0])}{text[1..]}.";
    }

    /// <summary>
    /// The element type of an array, or of a collection the app does not own (a <c>List&lt;T&gt;</c>, an
    /// <c>IReadOnlyList&lt;T&gt;</c>); null for any other type.
    /// </summary>
    private static Type? ElementOf(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }

        if (type == typeof(string) || IsTheAppsOwn(type) || !typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
        {
            return null;
        }

        return type.GetInterfaces().Append(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }

    /// <summary>Whether the app can give the type a static method: not an array or a type of .NET or ASP.NET Core.</summary>
    private static bool IsTheAppsOwn(Type type)
    {
        if (type.IsArray)
        {
            return false;
        }

        var ns = type.Namespace ?? "";
        return !(ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)
            || ns == "Microsoft" || ns.StartsWith("Microsoft.", StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether minimal API binding takes a parameter of this type, with no attributes, for a request body: it refuses
    /// one in a loader, and with a body allowed, infers one.
    /// </summary>
    private static bool ReadsFromTheBody(Type type, IServiceProvider services)
    {
        if (AloneFor(type) is not { } alone || Infers(alone, Options(services, [], NewEndpoint(services))))
        {
            return false;
        }

        var endpoint = NewEndpoint(services);
        return Infers(alone, new RequestDelegateFactoryOptions { ServiceProvider = services, RouteParameterNames = [], EndpointBuilder = endpoint })
            && endpoint.Metadata.OfType<IAcceptsMetadata>().Any();
    }

    /// <summary>Whether minimal API binding takes a loader parameter of this type, with no attributes.</summary>
    private static bool Binds(Type type, IServiceProvider services) =>
        AloneFor(type) is { } alone && Infers(alone, Options(services, [], NewEndpoint(services)));

    /// <summary><see cref="Alone{T}"/> for the type; null when it is not a type argument (a by-ref or pointer type, a ref struct), which binding refuses for another reason.</summary>
    private static MethodInfo? AloneFor(Type type)
    {
        try
        {
            return AloneMethod.MakeGenericMethod(type);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static readonly MethodInfo AloneMethod = typeof(LoaderBinding).GetMethod(nameof(Alone), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>A handler whose one parameter has a given type and no attributes.</summary>
    private static void Alone<T>(T value)
    {
    }

    private static bool Infers(MethodInfo method, RequestDelegateFactoryOptions options)
    {
        try
        {
            RequestDelegateFactory.InferMetadata(method, options);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>a, a and b, or a, b and c (or with another conjunction: a, b or c).</summary>
    private static string Join(IEnumerable<string> items, string conjunction = "and")
    {
        var list = items.ToList();
        return list.Count == 1 ? list[0] : $"{string.Join(", ", list[..^1])} {conjunction} {list[^1]}";
    }

    /// <summary>A type as C# code names it, without its namespace: <c>Filter</c>, <c>Filter[]</c>, <c>List&lt;Filter&gt;</c>, <c>Point?</c>.</summary>
    private static string TypeName(Type type)
    {
        if (Keywords.TryGetValue(type, out var keyword))
        {
            return keyword;
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return TypeName(underlying) + "?";
        }

        if (type.IsArray)
        {
            return TypeName(type.GetElementType()!) + "[]";
        }

        // A type nested in a generic one is generic without a backtick in its name.
        var tick = type.Name.IndexOf('`');
        return tick < 0 ? type.Name : $"{type.Name[..tick]}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
    }

    private static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(bool)] = "bool", [typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte", [typeof(char)] = "char",
        [typeof(short)] = "short", [typeof(ushort)] = "ushort", [typeof(int)] = "int", [typeof(uint)] = "uint",
        [typeof(long)] = "long", [typeof(ulong)] = "ulong", [typeof(float)] = "float", [typeof(double)] = "double",
        [typeof(decimal)] = "decimal", [typeof(string)] = "string", [typeof(object)] = "object",
    };

    private static RouteEndpointBuilder NewEndpoint(IServiceProvider services) =>
        new(null, RoutePatternFactory.Parse("/"), 0) { ApplicationServices = services };

    /// <summary>Whether the attribute says where minimal API binding takes the parameter's value from.</summary>
    public static bool IsBindingAttribute(Attribute attribute) =>
        attribute is IFromRouteMetadata or IFromQueryMetadata or IFromHeaderMetadata or IFromBodyMetadata or IFromFormMetadata
            or IFromServiceMetadata or FromKeyedServicesAttribute or AsParametersAttribute;
}
