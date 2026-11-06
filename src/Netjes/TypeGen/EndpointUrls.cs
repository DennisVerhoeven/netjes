using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Primitives;
using Netjes.Routing;

namespace Netjes.TypeGen;

/// <summary>
/// The generated file's URLs of the app's own endpoints (<see cref="AppEndpoints"/>): <c>EndpointParams</c>, the
/// values each one reads from its URL, typed from its route template and the parameters its handler binds, and the
/// templates <c>endpointUrl()</c> fills with <c>buildEndpointUrl</c>. A JSON endpoint (one in <c>api</c>) has a key
/// for each of its methods but <c>HEAD</c>, and any other endpoint one for <c>GET</c>, if it answers it: a link or a
/// fetch asks it for a page, a file or text, while what is posted to it, such as a webhook, comes from elsewhere.
/// </summary>
internal static class EndpointUrls
{
    /// <param name="Key">The method and the route as written, as in <c>api</c>: <c>GET /api/posts/{slug}/likes/</c>.</param>
    /// <param name="Template">The route that <c>buildEndpointUrl</c> fills (<see cref="EndpointUrls.Template"/>).</param>
    /// <param name="Values">The route values, in the order of the route, then the query values.</param>
    public sealed record Entry(string Key, string Template, IReadOnlyList<Value> Values);

    /// <param name="Name">The route parameter or query string key.</param>
    /// <param name="Type">The TypeScript type of what binding accepts for it.</param>
    /// <param name="Optional">Whether the URL can leave it out.</param>
    public sealed record Value(string Name, string Type, bool Optional);

    /// <param name="endpoints">Every endpoint; the app's own are kept.</param>
    /// <param name="isJson">Whether an endpoint is in the <c>api</c> map.</param>
    public static List<Entry> Entries(IEnumerable<RouteEndpoint> endpoints, Func<RouteEndpoint, bool> isJson)
    {
        var entries = new List<Entry>();
        foreach (var endpoint in endpoints.Where(AppEndpoints.IsTheAppsOwn))
        {
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods;
            var keys = isJson(endpoint) ? methods.Where(m => m != HttpMethods.Head) : methods.Where(m => m == HttpMethods.Get);
            var template = Template(endpoint.RoutePattern);
            var values = Values(endpoint);
            entries.AddRange(keys.Select(method => new Entry($"{method} {endpoint.RoutePattern.RawText}", template, values)));
        }

        return [.. entries.DistinctBy(e => e.Key).OrderBy(e => e.Key, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The route as the matcher reads it, for <c>buildEndpointUrl</c>: from the root (an MVC attribute route has no
    /// leading slash), with each literal as written but <c>{</c> and <c>}</c> encoded, so that braces mark only the
    /// values, and <c>%</c>, <c>#</c> and <c>\</c>, which a browser reads otherwise in a path; <c>{name}</c>,
    /// <c>{name?}</c>, <c>{name=default}</c> (the default encoded), <c>{*name}</c> and <c>{**name}</c> without
    /// constraints; and without a trailing slash, which the matcher ignores.
    /// </summary>
    public static string Template(RoutePattern pattern) =>
        "/" + string.Join("/", pattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(Part))));

    private static string Part(RoutePatternPart part) => part switch
    {
        RoutePatternLiteralPart literal => literal.Content.Replace("%", "%25").Replace("{", "%7B").Replace("}", "%7D").Replace("#", "%23").Replace("\\", "%5C"),
        RoutePatternSeparatorPart separator => separator.Content,
        RoutePatternParameterPart parameter => "{"
            + (parameter.IsCatchAll ? parameter.EncodeSlashes ? "*" : "**" : "")
            + parameter.Name
            + (parameter.Default is { } value ? "=" + Uri.EscapeDataString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")
                : parameter.IsOptional ? "?" : "")
            + "}",
        _ => throw new InvalidOperationException($"A route part of type {part.GetType().Name} is not known."),
    };

    /// <summary>
    /// The route parameters, typed from the parameter that binds each one (a string when none does), then the query
    /// string keys the handler binds (<see cref="LoaderBinding.UrlSource"/>). A route value is optional when routing
    /// fills in a default, or when the route makes it optional (<c>{id?}</c>, a catch-all) and no parameter that needs a
    /// value binds it: without one, binding would answer 400. A query value is optional unless binding needs it, and a
    /// list always, since binding gives an empty one. An MVC action reports no parameters: its route values are strings.
    /// </summary>
    private static List<Value> Values(RouteEndpoint endpoint)
    {
        var pattern = endpoint.RoutePattern;
        var routeParameters = pattern.Parameters.Select(p => p.Name).ToList();
        var bindings = endpoint.Metadata.GetOrderedMetadata<IParameterBindingMetadata>()
            .Select(binding => (Binding: binding, Source: LoaderBinding.UrlSource(binding, routeParameters)))
            .ToList();

        var values = new List<Value>();
        foreach (var parameter in pattern.Parameters)
        {
            var binding = bindings.FirstOrDefault(b => string.Equals(b.Source.Route, parameter.Name, StringComparison.OrdinalIgnoreCase)).Binding;
            var optional = parameter.Default is not null || ((parameter.IsOptional || parameter.IsCatchAll) && binding?.IsOptional != false);
            values.Add(new Value(parameter.Name, binding is null ? "string" : TypeOf(binding.ParameterInfo.ParameterType), optional));
        }

        foreach (var (binding, (_, query)) in bindings)
        {
            // Binding reads query string keys in any case, so a second parameter for the same key adds nothing.
            if (query is null || values.Any(v => string.Equals(v.Name, query, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var type = binding.ParameterInfo.ParameterType;
            values.Add(new Value(query, TypeOf(type), binding.IsOptional || IsList(type)));
        }

        return values;
    }

    private static bool IsList(Type type) => type.IsArray || type == typeof(StringValues) || type == typeof(StringValues?);

    private static readonly HashSet<Type> Numbers =
    [
        typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong),
        typeof(float), typeof(double), typeof(decimal), typeof(Half),
    ];

    /// <summary>
    /// What binding accepts for a value of the type, which binds from a string: a <c>boolean</c>; a <c>number</c> for
    /// numbers; for an enum its C# names (case-sensitively, whatever the JSON writes) and its values, or with
    /// <c>[Flags]</c> any string or number; a list for an array or <c>StringValues</c>; a <c>string</c> for the rest.
    /// </summary>
    private static string TypeOf(Type type)
    {
        if (type == typeof(StringValues) || type == typeof(StringValues?))
        {
            return "string | readonly string[]";
        }

        if (type.IsArray)
        {
            var element = TypeOf(type.GetElementType()!);
            return $"readonly {(element.Contains('|') ? $"({element})" : element)}[]";
        }

        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(bool))
        {
            return "boolean";
        }

        if (Numbers.Contains(type))
        {
            return "number";
        }

        if (type.IsEnum)
        {
            var values = Enum.GetValuesAsUnderlyingType(type).Cast<object>()
                .Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return type.IsDefined(typeof(FlagsAttribute), inherit: false) || values.Count == 0
                ? "string | number"
                : string.Join(" | ", [.. Enum.GetNames(type).Select(n => JsonSerializer.Serialize(n)), .. values]);
        }

        return "string";
    }

    public static void Append(StringBuilder output, IReadOnlyList<Entry> entries)
    {
        output.Append(
            """

            /**
             * What the app's endpoints read from their URLs, by method and route as in `api`: the route values, then the
             * query string values the handler binds. A JSON endpoint has a key for each of its methods, and any other
             * endpoint one for GET.
             */

            """.ReplaceLineEndings("\n"));
        if (entries.Count == 0)
        {
            output.Append("export type EndpointParams = Record<never, never>;\n");
        }
        else
        {
            output.Append("export type EndpointParams = {\n");
            foreach (var entry in entries)
            {
                var values = entry.Values.Count == 0
                    ? "Record<string, never>"
                    : $"{{ {string.Join("; ", entry.Values.Select(v => v.Optional ? $"{Key(v.Name)}?: {v.Type} | null | undefined" : $"{Key(v.Name)}: {v.Type}"))} }}";
                output.Append("  ").Append(JsonSerializer.Serialize(entry.Key)).Append(": ").Append(values).Append(";\n");
            }

            output.Append("};\n");
        }

        output.Append("export type EndpointKey = keyof EndpointParams;\n");
        if (entries.Count == 0)
        {
            output.Append("const endpointTemplates: Record<EndpointKey, string> = {};\n");
        }
        else
        {
            output.Append("const endpointTemplates: Record<EndpointKey, string> = {\n");
            foreach (var entry in entries)
            {
                output.Append("  ").Append(JsonSerializer.Serialize(entry.Key)).Append(": ").Append(JsonSerializer.Serialize(entry.Template)).Append(",\n");
            }

            output.Append("};\n");
        }

        output.Append(
            """

            /**
             * The URL of one of the app's endpoints, with its values encoded: `endpointUrl('GET /api/posts/{slug}/likes/', { slug })`.
             * The values the route does not name go in the query string, followed by the third argument. The URL has no base
             * path: pass it through `useHref` for a `fetch` or an `<a href>`.
             */
            export function endpointUrl<K extends EndpointKey>(
              key: K,
              ...args: {} extends EndpointParams[K]
                ? [params?: EndpointParams[K], search?: SearchParamsInput]
                : [params: EndpointParams[K], search?: SearchParamsInput]
            ): string {
              return buildEndpointUrl(endpointTemplates[key], args[0] ?? {}, args[1], key);
            }

            """.ReplaceLineEndings("\n"));
    }

    /// <summary>A property name as TypeScript writes it: as it is, or quoted.</summary>
    private static string Key(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] is '_' or '$') && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '$')
            ? name
            : JsonSerializer.Serialize(name);
}
