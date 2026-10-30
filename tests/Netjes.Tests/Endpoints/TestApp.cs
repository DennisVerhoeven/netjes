using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netjes.Rendering;

namespace Netjes.Tests.Endpoints;

/// <summary>
/// A Netjes app on TestServer, rooted in a temporary directory, with SSR replaced by a recording fake (or, with
/// <c>sidecar</c>, rendered by the real sidecar pool running a test script).
/// </summary>
internal sealed class TestApp : IAsyncDisposable
{
    private TestApp(WebApplication app, string root, FakeSsrRenderer renderer, ListLoggerProvider logs, HttpClient client)
    {
        App = app;
        Root = root;
        Renderer = renderer;
        LogProvider = logs;
        Client = client;
    }

    private ListLoggerProvider LogProvider { get; }

    public IReadOnlyList<LogEntry> Logs => LogProvider.Entries;

    public WebApplication App { get; }
    public string Root { get; }
    public FakeSsrRenderer Renderer { get; }
    public HttpClient Client { get; }

    public static async Task<TestApp> StartAsync(
        IEnumerable<string> files,
        Action<WebApplication>? map = null,
        Action<NetjesOptions>? configure = null,
        Action<IServiceCollection>? services = null,
        string environment = "Development",
        Action<string>? prepareRoot = null,
        Action<IEndpointConventionBuilder>? pages = null,
        Action<WebApplication>? configureApp = null,
        string? pathBase = null,
        bool kestrel = false,
        string? sidecar = null,
        Action<FakeSsrRenderer>? renderer = null,
        Action<WebApplication>? beforeNetjes = null)
    {
        var root = Directory.CreateTempSubdirectory("netjes-test-").FullName;
        WebApplication? app = null;
        try
        {
            foreach (var file in files)
            {
                var path = Path.Combine(root, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, "export default function Page() { return null }");
            }

            prepareRoot?.Invoke(root);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = environment,
                ContentRootPath = root,
            });
            if (kestrel)
            {
                // For what TestServer does not do, such as enforcing request body size limits.
                builder.WebHost.UseUrls("http://127.0.0.1:0");
            }
            else
            {
                builder.WebHost.UseTestServer();
            }

            var logs = new ListLoggerProvider();
            builder.Logging.AddProvider(logs);
            builder.Services.AddNetjes(configure);
            services?.Invoke(builder.Services);
            var fake = new FakeSsrRenderer();
            // Before the app starts: hosted services such as the export render while it does.
            renderer?.Invoke(fake);
            if (sidecar is null)
            {
                builder.Services.AddSingleton<ISsrRenderer>(fake);
            }
            else
            {
                // The real pool, running a script from Sidecar/ that speaks the sidecar protocol.
                builder.Services.Configure<NetjesOptions>(o => o.SidecarScript = Path.Combine(AppContext.BaseDirectory, "Sidecar", sidecar));
            }

            app = builder.Build();
            if (pathBase is not null)
            {
                app.UsePathBase(pathBase);
            }

            beforeNetjes?.Invoke(app);
            app.UseNetjes();
            app.UseAntiforgery();
            configureApp?.Invoke(app);
            map?.Invoke(app);
            var conventions = app.MapPages();
            pages?.Invoke(conventions);
            await app.StartAsync(TestContext.Current.CancellationToken);
            var client = kestrel
                ? new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.First()) }
                : app.GetTestClient();
            return new TestApp(app, root, fake, logs, client);
        }
        catch
        {
            // A failed start has no TestApp to dispose; do not leave its directory behind.
            // Cleanup must never replace the startup exception the test expects.
            try
            {
                if (app is not null)
                {
                    await app.DisposeAsync();
                }

                await DeleteRootAsync(root);
            }
            catch (Exception)
            {
            }

            throw;
        }
    }

    public Task<HttpResponseMessage> GetDocumentAsync(string url) =>
        Client.GetAsync(url, TestContext.Current.CancellationToken);

    public Task<HttpResponseMessage> GetDataAsync(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(NetjesHeaders.Data, "1");
        return Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public async Task<JsonNode> GetDataJsonAsync(string url)
    {
        var response = await GetDataAsync(url);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        // Stopping first ends the file watchers, which can still be writing into the directory until then.
        await App.StopAsync();
        await App.DisposeAsync();
        await DeleteRootAsync(Root);
    }

    /// <summary>Deletes the directory, retrying while something that is still shutting down (a watcher, the sidecar) writes into it.</summary>
    private static async Task DeleteRootAsync(string root)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 50)
            {
                await Task.Delay(100);
            }
        }
    }
}

internal sealed class FakeSsrRenderer : ISsrRenderer
{
    private readonly ConcurrentQueue<string> _payloads = new();

    public IReadOnlyList<string> Payloads => [.. _payloads];

    public JsonNode LastPayload => JsonNode.Parse(_payloads.Last())!;

    public Func<string, string>? OnRender { get; set; }

    public List<SsrStreamOptions> StreamOptions { get; } = [];

    public ConcurrentQueue<(string RenderId, string Id, string? Json, string? Error)> Resolved { get; } = new();

    /// <summary>Runs before each delivery, like the sidecar's HTTP call (which can hang while its process restarts).</summary>
    public Func<CancellationToken, Task>? OnResolve { get; set; }

    public async Task ResolveAsync(string renderId, string id, string? valueJson, string? error, CancellationToken cancellationToken)
    {
        // Like the sidecar's HTTP call: nothing is delivered with a cancelled token.
        cancellationToken.ThrowIfCancellationRequested();
        if (OnResolve is not null)
        {
            await OnResolve(cancellationToken);
        }

        Resolved.Enqueue((renderId, id, valueJson, error));
    }

    /// <summary>Chunks to stream instead of <see cref="OnRender"/>'s document, with a pause before each.</summary>
    public Func<string, IReadOnlyList<string>>? OnStream { get; set; }

    /// <summary>Answers streamed renders in place of everything else, like a sidecar would.</summary>
    public Func<string, SsrStreamOptions, Task<Stream>>? StreamHandler { get; set; }

    public async Task<Stream> RenderStreamAsync(string payloadJson, SsrStreamOptions options, CancellationToken cancellationToken)
    {
        StreamOptions.Add(options);
        if (StreamHandler is not null)
        {
            _payloads.Enqueue(payloadJson);
            return await StreamHandler(payloadJson, options);
        }

        if (OnStream is null)
        {
            return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(await RenderAsync(payloadJson, cancellationToken)));
        }

        _payloads.Enqueue(payloadJson);
        var pipe = new System.IO.Pipelines.Pipe();
        var chunks = OnStream(payloadJson);
        _ = Task.Run(async () =>
        {
            foreach (var chunk in chunks)
            {
                await Task.Delay(50, cancellationToken);
                await pipe.Writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(chunk), cancellationToken);
            }

            await pipe.Writer.CompleteAsync();
        }, cancellationToken);
        return pipe.Reader.AsStream();
    }

    public Task<string> RenderAsync(string payloadJson, CancellationToken cancellationToken)
    {
        _payloads.Enqueue(payloadJson);
        return Task.FromResult(OnRender?.Invoke(payloadJson) ?? "<!DOCTYPE html><html><body>rendered</body></html>");
    }
}

internal sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception = null);

internal sealed class ListLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new ListLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class ListLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), exception));
    }
}

internal static class HttpResponseMessageExtensions
{
    public static MediaTypeHeaderValue? MediaType(this HttpResponseMessage response) => response.Content.Headers.ContentType;
}
