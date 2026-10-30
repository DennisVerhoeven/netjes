using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace Netjes.Rendering;

/// <summary>
/// The <c>Netjes:PublicConfig</c> configuration section as JSON for the browser (<c>usePublicConfig</c>): settings
/// that differ per environment without rebuilding the client, like Next.js's <c>NEXT_PUBLIC_</c> variables. Values
/// are strings, as configuration values are; keys follow the app's JSON naming policy. Everything in it is public.
/// </summary>
internal static class PublicConfig
{
    public const string Section = "Netjes:PublicConfig";

    public static JsonObject? From(IConfiguration configuration, JsonNamingPolicy? naming)
    {
        var section = configuration.GetSection(Section);
        return section.Exists() ? ToNode(section, naming) as JsonObject : null;
    }

    private static JsonNode? ToNode(IConfigurationSection section, JsonNamingPolicy? naming)
    {
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
        {
            return section.Value is null ? null : JsonValue.Create(section.Value);
        }

        // Configuration has no arrays, only keys 0, 1, 2...
        var isArray = children
            .Select((child, i) => child.Key == i.ToString(CultureInfo.InvariantCulture))
            .All(matches => matches);
        if (isArray)
        {
            return new JsonArray([.. children.OrderBy(c => int.Parse(c.Key, CultureInfo.InvariantCulture)).Select(c => ToNode(c, naming))]);
        }

        var result = new JsonObject();
        foreach (var child in children)
        {
            result[naming?.ConvertName(child.Key) ?? child.Key] = ToNode(child, naming);
        }

        return result;
    }
}
