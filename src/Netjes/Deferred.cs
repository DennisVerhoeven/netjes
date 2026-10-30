using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Serialization;

namespace Netjes;

/// <summary>
/// A loader value that is still being computed, from <see cref="Page.Defer{T}(Task{T})"/>. The page renders without waiting
/// for it; the component reads it with React's <c>use()</c> inside a Suspense boundary (<c>loading.tsx</c>), and the
/// value streams to the server render and the browser when it is ready.
/// </summary>
[JsonConverter(typeof(DeferredJsonConverterFactory))]
public sealed class Deferred<T>
{
    internal Deferred(Task<T> task) => Start = _ => Box(task);

    internal Deferred(Func<IServiceProvider, Task<T>> load) => Start = async scopes =>
    {
        // Its own DI scope: a scoped DbContext is not shared with the loaders still running for this request.
        await using var scope = scopes.CreateAsyncScope();
        return await load(scope.ServiceProvider);
    };

    /// <summary>Starts (or, for a task, observes) the value; called once the payload is written.</summary>
    internal Func<IServiceScopeFactory, Task<object?>> Start { get; }

    private static async Task<object?> Box(Task<T> task) => await task;
}

public static partial class Page
{
    /// <summary>
    /// Sends <paramref name="task"/>'s result to the page when it completes instead of waiting for it, like
    /// Next.js streaming with Suspense. Use it for slow, non-essential data (comments, recommendations).
    /// </summary>
    public static Deferred<T> Defer<T>(Task<T> task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new Deferred<T>(task);
    }

    /// <summary>
    /// Like <see cref="Defer{T}(Task{T})"/>, but the value is computed in its own DI scope, started once the page's
    /// payload is written. Use it when the work needs scoped services such as a DbContext, which must not be used
    /// concurrently with the request's other loaders.
    /// </summary>
    public static Deferred<T> Defer<T>(Func<IServiceProvider, Task<T>> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        return new Deferred<T>(load);
    }
}

/// <summary>The deferred values met while serializing one payload; set for the duration of that serialization.</summary>
internal sealed class DeferredCollector
{
    private static readonly AsyncLocal<DeferredCollector?> CurrentCollector = new();

    public static DeferredCollector? Current
    {
        get => CurrentCollector.Value;
        set => CurrentCollector.Value = value;
    }

    public List<(string Id, Func<IServiceScopeFactory, Task<object?>> Start)> Values { get; } = [];

    public string Register<T>(Deferred<T> deferred)
    {
        var id = $"d{Values.Count}";
        Values.Add((id, deferred.Start));
        return id;
    }
}

internal sealed class DeferredJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Deferred<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(DeferredJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class DeferredJsonConverter<T> : JsonConverter<Deferred<T>>
    {
        public override Deferred<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("Deferred values are only written.");

        public override void Write(Utf8JsonWriter writer, Deferred<T> value, JsonSerializerOptions options)
        {
            var collector = DeferredCollector.Current
                ?? throw new InvalidOperationException("Page.Defer values only work in page and layout props.");
            writer.WriteStartObject();
            writer.WriteString("$netjes", "deferred");
            writer.WriteString("id", collector.Register(value));
            writer.WriteEndObject();
        }
    }
}
