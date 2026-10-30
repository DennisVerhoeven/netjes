using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Netjes.Rendering;

namespace Netjes;

/// <summary>
/// Results for server actions mapped with <c>MapAction</c>, the counterpart of Next.js <c>'use server'</c> functions.
/// </summary>
public static class ServerAction
{
    /// <summary>
    /// The action succeeded; <paramref name="result"/> is what the client's action call resolves to. The typed result
    /// names the result type for the generated <c>serverActions</c> references.
    /// </summary>
    public static ServerActionResult<TResult> Ok<TResult>(TResult result) => new(new(ServerActionResult.Outcome.Ok) { Result = result });

    /// <summary>The action succeeded, with no result.</summary>
    public static ServerActionResult Ok() => new(ServerActionResult.Outcome.Ok);

    /// <summary>
    /// Validation failed: status 422 with the errors per field. Field names are written with the app's JSON naming
    /// policy, so they match the zod schema's paths. Without JavaScript the form's page is rendered again with them.
    /// </summary>
    public static ServerActionResult Invalid(IDictionary<string, string[]> errors) =>
        new(ServerActionResult.Outcome.Invalid) { Errors = errors };

    /// <summary>Validation failed for one field: like <see cref="Invalid(IDictionary{string, string[]})"/> with one message.</summary>
    public static ServerActionResult Invalid(string field, string message) =>
        Invalid(new Dictionary<string, string[]> { [field] = [message] });

    /// <summary>
    /// The user may not do this: 403. A client call throws to the nearest error boundary; a form post without
    /// JavaScript gets the error page.
    /// </summary>
    public static ServerActionResult Forbidden() => new(ServerActionResult.Outcome.Status) { Status = StatusCodes.Status403Forbidden };

    /// <summary>What the action works on does not exist: 404, handled like <see cref="Forbidden"/>.</summary>
    public static ServerActionResult NotFound() => new(ServerActionResult.Outcome.Status) { Status = StatusCodes.Status404NotFound };

    /// <summary>The action succeeded; the client navigates to <paramref name="location"/> (relative or http(s)).</summary>
    public static ServerActionResult Redirect(string location) =>
        new(ServerActionResult.Outcome.Redirect) { Location = Page.Redirect(location).Location };
}

/// <summary>
/// What a server action returns: success (with or without a result), validation errors, a redirect or a status. Create
/// one with <see cref="ServerAction"/>; the methods here add cache and query changes to it.
/// </summary>
public sealed partial class ServerActionResult : IResult
{
    internal enum Outcome { Ok, Invalid, Redirect, Status }

    internal const string ReturnField = "__netjes_return";
    internal const string StateKey = "Netjes.ActionState";

    private readonly List<string> _tags = [];
    private readonly List<string> _paths = [];
    private readonly List<IReadOnlyList<object?>> _invalidate = [];
    private readonly List<PageQuery> _queries = [];
    private bool _refresh;

    internal ServerActionResult(Outcome outcome) => Kind = outcome;

    internal Outcome Kind { get; }
    internal object? Result { get; init; }
    internal IDictionary<string, string[]>? Errors { get; init; }
    internal string? Location { get; init; }
    internal int Status { get; init; }

    /// <summary>Evicts output-cached pages with these tags (<c>.CachePage(duration, tags)</c>) and refreshes the client's page.</summary>
    public ServerActionResult Revalidate(params string[] tags)
    {
        _tags.AddRange(tags);
        _refresh = true;
        return this;
    }

    /// <summary>Evicts the output-cached pages at these paths and refreshes the client's page.</summary>
    public ServerActionResult RevalidatePath(params string[] paths)
    {
        _paths.AddRange(paths);
        _refresh = true;
        return this;
    }

    /// <summary>Reloads the client's current page (every loader runs again) once the action completes.</summary>
    public ServerActionResult Refresh()
    {
        _refresh = true;
        return this;
    }

    /// <summary>Invalidates these TanStack Query keys (and those they prefix) on the client, so they are refetched.</summary>
    public ServerActionResult Invalidate(params IReadOnlyList<object?>[] queryKeys)
    {
        _invalidate.AddRange(queryKeys);
        return this;
    }

    /// <summary>Sets a TanStack Query entry on the client, e.g. the updated record, without a refetch.</summary>
    public ServerActionResult WithQuery(IReadOnlyList<object?> queryKey, object? data)
    {
        if (queryKey.Count == 0)
        {
            throw new ArgumentException("A query key needs at least one element.", nameof(queryKey));
        }

        _queries.Add(new PageQuery(queryKey, data));
        return this;
    }

    /// <summary>Evicts the output-cached pages it names, then writes the outcome, with the client's query changes, to the response.</summary>
    public async Task ExecuteAsync(HttpContext http)
    {
        var cache = http.RequestServices.GetRequiredService<IPageCache>();
        foreach (var tag in _tags.Distinct(StringComparer.Ordinal))
        {
            await cache.RevalidateTagAsync(tag, http.RequestAborted);
        }

        foreach (var path in _paths.Distinct(StringComparer.Ordinal))
        {
            await cache.RevalidatePathAsync(path, http.RequestAborted);
        }

        if (Kind == Outcome.Status)
        {
            if (PageRenderer.IsDataRequest(http))
            {
                await ServerActionResponses.WriteAsync(http, Status, ServerActionResponses.Failure(Status, ReasonPhrases.GetReasonPhrase(Status)));
            }
            else
            {
                // An empty error response: Netjes's error page middleware renders the page for it.
                http.Response.StatusCode = Status;
            }

            return;
        }

        var json = http.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        var errors = Errors?.ToDictionary(e => json.DictionaryKeyPolicy?.ConvertName(e.Key) ?? json.PropertyNamingPolicy?.ConvertName(e.Key) ?? e.Key, e => e.Value);
        if (Kind == Outcome.Invalid && http.GetEndpoint()?.Metadata.GetMetadata<PageActionMetadata>() is not null)
        {
            // A page action's input failed its checks: its own page again, from its loader, for the client router too,
            // which shows what a page action answers as a page.
            var posted = http.Request.HasFormContentType ? await http.Request.ReadFormAsync(http.RequestAborted) : null;
            // The path as the server read it: written out and read back, an encoded one would change its route values.
            var (path, query) = (http.Request.Path, http.Request.QueryString);
            await RenderAgainAsync(http, json, errors, posted, () => ReExecute.GetAsync(http, path, query));
            return;
        }

        if (PageRenderer.IsDataRequest(http))
        {
            var time = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().ToUnixTimeMilliseconds();
            var status = Kind == Outcome.Invalid ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status200OK;
            // A failed form call: the client hands back what was typed itself, but only what a post without JavaScript would.
            var fields = Kind == Outcome.Invalid && http.Request.HasFormContentType
                ? EchoedNames(EchoedFields(http, await http.Request.ReadFormAsync(http.RequestAborted), json))
                : null;
            await ServerActionResponses.WriteAsync(http, status, new ActionPayload(
                Ok: Kind != Outcome.Invalid,
                Status: status,
                Result: Result,
                Errors: errors,
                Fields: fields,
                Redirect: Location,
                Revalidate: _refresh,
                Invalidate: _invalidate.Count > 0 ? _invalidate : null,
                Queries: _queries.Count > 0 ? [.. _queries.Select(q => new QueryPayload(q.Key, q.Data, time))] : null,
                Message: null));
            return;
        }

        // A plain form post (no JavaScript): finish like a page action would.
        var form = http.Request.HasFormContentType ? await http.Request.ReadFormAsync(http.RequestAborted) : null;
        var returnUrl = LocalOrHome(form?[ReturnField].ToString());
        if (Kind != Outcome.Invalid)
        {
            http.Response.StatusCode = StatusCodes.Status303SeeOther;
            // The app writes paths as if mounted at the root; the return URL already carries the base path.
            http.Response.Headers.Location = PageRenderer.UnderBasePath(http, Location ?? returnUrl);
            return;
        }

        await RenderAgainAsync(http, json, errors, form, () => ReExecute.GetAsync(http, returnUrl));
    }

    /// <summary>
    /// After a failed form post: the page that <paramref name="render"/> runs the pipeline again for, rendered in its
    /// place in the same request, with the errors and what was typed in the fields the action declares.
    /// </summary>
    private static async Task RenderAgainAsync(
        HttpContext http, JsonSerializerOptions json, Dictionary<string, string[]>? errors, IFormCollection? form, Func<Task> render)
    {
        // Echoed into the page so the form keeps what was typed.
        var values = form is null ? [] : EchoedValues(form, EchoedFields(http, form, json));
        // The page rendered in its place sets 422 itself (setting it here would stop its loaders from running).
        http.Items[StateKey] = new ActionStatePayload(ActionNameOf(http), errors ?? [], values);
        // It holds what was typed: never kept in the browser's cache (a shared machine), whatever the page itself allows.
        NoStore.Force(http, "no-store");
        await render();
    }

    /// <summary>
    /// The submitted fields a failure hands back: only those the action declares (<see cref="ActionFormFields"/>), and
    /// never the token, Netjes's own fields, or a password, card number and the like.
    /// </summary>
    private static List<string> EchoedFields(HttpContext http, IFormCollection form, JsonSerializerOptions json)
    {
        var tokenField = http.RequestServices.GetService<IOptions<AntiforgeryOptions>>()?.Value.FormFieldName;
        var privateWords = http.RequestServices.GetRequiredService<NetjesContext>().Options.PrivateFormFields;
        var declared = http.GetEndpoint()?.Metadata.GetMetadata<ActionFormFields>();
        return [.. form.Keys.Where(key => key != tokenField && !key.StartsWith("__netjes_", StringComparison.Ordinal)
            && declared?.Declares(key, json) == true
            && !IsPrivate(key, privateWords))];
    }

    /// <summary>The longest value a failure without JavaScript writes back into the page; a longer one comes back empty.</summary>
    private const int MaxEchoedValueLength = 16 * 1024;

    /// <summary>
    /// How much a failure hands back in all, names included: without JavaScript its names and values, with JavaScript
    /// the names of the fields the client restores. Fields past it come back empty.
    /// </summary>
    private const int MaxEchoedLength = 64 * 1024;

    // Bounded, so that a large post does not turn into an equally large render.
    private static Dictionary<string, object?> EchoedValues(IFormCollection form, IEnumerable<string> fields)
    {
        var values = new Dictionary<string, object?>();
        var left = MaxEchoedLength;
        foreach (var field in fields)
        {
            var value = form[field];
            var length = field.Length + value.Sum(v => v?.Length ?? 0);
            if (value.Any(v => v?.Length > MaxEchoedValueLength) || length > left)
            {
                continue;
            }

            left -= length;
            values[field] = value.Count == 1 ? value.ToString() : value.ToArray();
        }

        return values;
    }

    // The client keeps the values; the names alone could still make a response as large as the post.
    private static List<string> EchoedNames(IEnumerable<string> fields)
    {
        var names = new List<string>();
        var left = MaxEchoedLength;
        foreach (var field in fields)
        {
            if (field.Length > left)
            {
                continue;
            }

            left -= field.Length;
            names.Add(field);
        }

        return names;
    }

    /// <summary>
    /// Whether a field name marks a secret: a private word of five letters or more anywhere in it, separators aside
    /// (<c>newpassword</c>, <c>password2</c>, <c>api_key</c>), and so is a word written with separators itself
    /// (<c>auth-code</c> also hides <c>authCode</c>); a shorter one as a word of the name, or at the start or end of
    /// one (<c>PINCode</c>, <c>cardnumber</c>, <c>newpwd</c>, but not <c>spinner</c>). Hiding a field by mistake only
    /// costs a retype.
    /// </summary>
    private static bool IsPrivate(string name, IReadOnlyList<string> privateWords)
    {
        var words = WordsOf(name);
        var joined = string.Concat(words);
        return privateWords.Any(word =>
        {
            var bare = string.Concat(WordsOf(word));
            return bare.Length >= 5 || (bare.Length > 0 && bare.Length < word.Length)
                ? joined.Contains(bare, StringComparison.OrdinalIgnoreCase)
                : words.Any(w => w.StartsWith(word, StringComparison.OrdinalIgnoreCase) || w.EndsWith(word, StringComparison.OrdinalIgnoreCase));
        });
    }

    /// <summary>The words of a field name: <c>newPwd</c>, <c>pin_code</c>, <c>card-cvc</c>, <c>PINCode</c>, <c>x1Pin</c>.</summary>
    private static string[] WordsOf(string name) =>
        FieldWordBoundary().Split(name).Where(word => word.Length > 0).ToArray();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=[0-9])|(?<=[0-9])(?=[A-Za-z])|[\s_.\-\[\]]+")]
    private static partial System.Text.RegularExpressions.Regex FieldWordBoundary();

    /// <summary>What a failure left in the page names the action: a server action by its name, a page action by its page's pattern.</summary>
    private static string ActionNameOf(HttpContext http) =>
        http.GetEndpoint()?.Metadata is { } metadata
            ? metadata.GetMetadata<ServerActionMetadata>()?.Name ?? metadata.GetMetadata<PageActionMetadata>()?.Pattern ?? ""
            : "";

    // Same rules as Page.LocalRedirect: "/path" but not "//host" or "/\host", after dropping what browsers drop.
    private static string LocalOrHome(string? url)
    {
        var normalized = Page.Normalize(url ?? "");
        return normalized is { Length: > 0 } && normalized[0] == '/' && (normalized.Length == 1 || (normalized[1] != '/' && normalized[1] != '\\'))
            ? normalized
            : "/";
    }
}

/// <summary>
/// A server action result typed by its result: what <see cref="ServerAction.Ok{TResult}"/> returns. The untyped
/// results (<see cref="ServerAction.Invalid(IDictionary{string, string[]})"/>, <see cref="ServerAction.Redirect"/>)
/// convert to it, so one action can return either.
/// </summary>
public sealed class ServerActionResult<TResult> : IResult
{
    internal ServerActionResult(ServerActionResult inner) => Inner = inner;

    internal ServerActionResult Inner { get; }

    /// <inheritdoc cref="ServerActionResult.Revalidate"/>
    public ServerActionResult<TResult> Revalidate(params string[] tags) => new(Inner.Revalidate(tags));

    /// <inheritdoc cref="ServerActionResult.RevalidatePath"/>
    public ServerActionResult<TResult> RevalidatePath(params string[] paths) => new(Inner.RevalidatePath(paths));

    /// <inheritdoc cref="ServerActionResult.Refresh"/>
    public ServerActionResult<TResult> Refresh() => new(Inner.Refresh());

    /// <inheritdoc cref="ServerActionResult.Invalidate"/>
    public ServerActionResult<TResult> Invalidate(params IReadOnlyList<object?>[] queryKeys) => new(Inner.Invalidate(queryKeys));

    /// <inheritdoc cref="ServerActionResult.WithQuery"/>
    public ServerActionResult<TResult> WithQuery(IReadOnlyList<object?> queryKey, object? data) => new(Inner.WithQuery(queryKey, data));

    /// <inheritdoc cref="ServerActionResult.ExecuteAsync"/>
    public Task ExecuteAsync(HttpContext httpContext) => Inner.ExecuteAsync(httpContext);

    /// <summary>Lets an action return an untyped result, such as <see cref="ServerAction.Forbidden"/>, where a typed one is expected.</summary>
    public static implicit operator ServerActionResult<TResult>(ServerActionResult result) => new(result);
}

internal sealed record ServerActionMetadata(string Name);

/// <summary>Marks a page action (<c>MapPageAction</c>) with the pattern of its page.</summary>
internal sealed record PageActionMetadata(string Pattern);

/// <summary>
/// <see cref="NetjesOptions.ActionBodySizeLimit"/>, applied after routing to an action without a request size limit of
/// its own (<c>[RequestSizeLimit]</c> on its delegate or group, <c>WithMetadata</c>). Unlike such a limit, which
/// replaces the server's, it only lowers it: an app that set a stricter one keeps it.
/// </summary>
internal static class ActionBodySizeLimit
{
    public static void Apply(HttpContext http, NetjesOptions options)
    {
        if (options.ActionBodySizeLimit is { } limit
            && http.GetEndpoint()?.Metadata is { } metadata
            && metadata.GetMetadata<ServerActionMetadata>() is not null
            && metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>() is null
            && http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature
            && (feature.MaxRequestBodySize is null || feature.MaxRequestBodySize > limit))
        {
            feature.MaxRequestBodySize = limit;
        }
    }
}

/// <summary>
/// Marks a client call's response with <see cref="NetjesHeaders.Cookies"/> when the action set or deleted a cookie,
/// like Next.js, where cookies an action sets invalidate the client router's cache. Script cannot read Set-Cookie,
/// so the server tells the client router, which then forgets the pages it keeps for back and forward.
/// </summary>
internal static class CookieChanges
{
    /// <summary>
    /// Checked as the response starts, so it sees what the action wrote on every path, a failure and the action's own
    /// <c>OnStarting</c> callbacks included (they run before this one). Cookies on the response before the action ran
    /// (a middleware's), those written by callbacks registered before it (cookie authentication's sliding renewal),
    /// and the antiforgery cookie, which names no user, do not count.
    /// </summary>
    public static ValueTask<object?> Mark(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (PageRenderer.IsDataRequest(http))
        {
            var before = http.Response.Headers.SetCookie;
            http.Response.OnStarting(() =>
            {
                if (Changed(http, before))
                {
                    http.Response.Headers[NetjesHeaders.Cookies] = "changed";
                }

                return Task.CompletedTask;
            });
        }

        return next(context);
    }

    private static bool Changed(HttpContext http, StringValues before)
    {
        var earlier = before.ToList();
        var antiforgery = http.RequestServices.GetService<IOptions<AntiforgeryOptions>>()?.Value.Cookie.Name;
        foreach (var cookie in http.Response.Headers.SetCookie)
        {
            if (cookie is null || earlier.Remove(cookie))
            {
                continue;
            }

            if (antiforgery is null || !string.Equals(NameOf(cookie), antiforgery, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // As browsers read it (RFC 6265 5.2): up to the first '=' of the part before the first ';', without the spaces
    // and tabs around it; no '=' at all is a cookie with an empty name.
    private static string NameOf(string setCookie)
    {
        var pair = setCookie.AsSpan();
        var semicolon = pair.IndexOf(';');
        if (semicolon >= 0)
        {
            pair = pair[..semicolon];
        }

        var equals = pair.IndexOf('=');
        return equals < 0 ? "" : pair[..equals].Trim(" \t").ToString();
    }
}

/// <summary>
/// The result type a server action's delegate names through its <see cref="ServerActionResult{TResult}"/> return type;
/// null when it returns the untyped <see cref="ServerActionResult"/>, which sends no result.
/// </summary>
internal sealed record ServerActionResultMetadata(Type? ResultType)
{
    public static ServerActionResultMetadata? For(Delegate handler)
    {
        var type = PagePropsMetadata.UnwrapAwaitable(handler.Method.ReturnType);
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ServerActionResult<>)
            ? new ServerActionResultMetadata(type.GetGenericArguments()[0])
            : type == typeof(ServerActionResult) ? new ServerActionResultMetadata(ResultType: null) : null;
    }
}

/// <summary>
/// The JSON a server action call answers with. <c>Fields</c>: after a failed form call, the submitted fields the client
/// may hand back in the failure state's values.
/// </summary>
internal sealed record ActionPayload(
    bool Ok,
    int Status,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] object? Result,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IDictionary<string, string[]>? Errors,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Fields,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Redirect,
    bool Revalidate,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<IReadOnlyList<object?>>? Invalidate,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<QueryPayload>? Queries,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Message)
{
    public string Kind => "action";
}

/// <summary>What a failed no-JavaScript form post leaves for the page rendered in its place.</summary>
internal sealed record ActionStatePayload(string Action, IDictionary<string, string[]> Errors, IDictionary<string, object?> Values);

internal static class ServerActionResponses
{
    public static async Task WriteAsync(HttpContext http, int status, ActionPayload payload)
    {
        var json = http.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json; charset=utf-8";
        NoStore.Force(http, "no-store");
        if (status == StatusCodes.Status400BadRequest && http.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: false })
        {
            // A refused token stops the call before the action (in form binding or the antiforgery filter), so the
            // client may send it again with a fresh token. Set here, after any Response.Clear() on the error paths.
            http.Response.Headers[NetjesHeaders.Antiforgery] = "invalid";
        }

        await http.Response.WriteAsync(JsonSerializer.Serialize(payload, json), http.RequestAborted);
    }

    public static ActionPayload Failure(int status, string? message) =>
        new(false, status, null, null, null, null, false, null, null, message);
}
