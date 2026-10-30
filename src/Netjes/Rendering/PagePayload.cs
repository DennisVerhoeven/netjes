using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Netjes.Routing;

namespace Netjes.Rendering;

// The JSON contract shared with the netjes client runtime (packages/netjes/src/payload.ts). Keep the two in sync.

internal sealed record PagePayload(
    string? Route,
    string? Page,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PageKind,
    IReadOnlyList<SegmentPayload> Segments,
    object Props,
    IReadOnlyDictionary<string, object?> Params,
    object? Shared,
    int Status,
    string Url,
    string Version,
    long RenderedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AntiforgeryPayload? Antiforgery,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<QueryPayload>? Queries,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActionStatePayload? ActionState,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PageMetadata? Metadata,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<InterceptPayload>? Intercepts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool DraftMode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BasePath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Locale,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentPayload? Document)
{
    public string Kind => "page";
}

internal sealed record RedirectPayload(string Location, string Version)
{
    public string Kind => "redirect";
}

internal sealed record DocumentPayload(
    string Lang,
    DocumentAssets Assets,
    SpecialPagePayload? ErrorPage,
    ImageConfigPayload Images,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonObject? PublicConfig,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Nonce,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VitalsEndpoint,
    // No sidecar was ready: .NET wrote the document without the page, and the client entry renders instead of hydrating.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ClientRender = false);

/// <summary>What the <c>&lt;Image&gt;</c> component needs to build <c>srcset</c> URLs.</summary>
/// <param name="BasePath">The app's base path, for images served as is from public/ (the optimizer's url is not prefixed).</param>
internal sealed record ImageConfigPayload(
    string Path,
    IReadOnlyList<int> Widths,
    IReadOnlyList<int> Qualities,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BasePath);

/// <summary>A slot that may show this page instead, on a client navigation from inside its segment.</summary>
/// <param name="Params">The intercepting page's own route values, by position from the URL.</param>
internal sealed record InterceptPayload(
    string Segment,
    string Slot,
    string Page,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, object?>? Params = null);

internal sealed record SpecialPagePayload(string Page, IReadOnlyList<SegmentPayload> Segments);

/// <summary>A folder's segment files the client composes around the page, outermost first, and its loader's props.</summary>
internal sealed record SegmentPayload(
    string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Layout,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Template,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Loading,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Props = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Reused = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string?>? Slots = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Depth = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? SlotParams = null)
{
    /// <summary>The segment's files; its parallel route slots resolved for <paramref name="route"/> (defaults without one).</summary>
    public static SegmentPayload From(RouteSegment segment, PageRoute? route = null) =>
        new(segment.Id, segment.Layout, segment.Template, segment.Loading, segment.Error)
        {
            Slots = segment.Slots.Count == 0
                ? null
                : new SortedDictionary<string, string?>(
                    segment.Slots.ToDictionary(s => s.Key, s => route is null ? s.Value.Default : segment.SlotFor(s.Key, route)),
                    StringComparer.Ordinal),
        };

    /// <summary>The client-side modules, for asset resolution.</summary>
    public IEnumerable<string> Modules() =>
        new[] { Layout, Template, Loading, Error }.Concat(Slots?.Values ?? []).OfType<string>();
}

internal sealed record AntiforgeryPayload(string FormFieldName, string? HeaderName, string? RequestToken);

/// <summary>A TanStack Query cache entry; <paramref name="UpdatedAt"/> is Unix milliseconds, like <c>dataUpdatedAt</c>.</summary>
internal sealed record QueryPayload(IReadOnlyList<object?> Key, object? Data, long UpdatedAt);

internal sealed record ErrorProps(int Status, string Message, string? Stack);
