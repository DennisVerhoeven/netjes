using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Netjes;
using Netjes.Rendering;
using TaskBoard.Data;
using Testcontainers.PostgreSql;

namespace TaskBoard.Tests;

/// <summary>
/// The real app against a real Postgres (Testcontainers), with server rendering replaced by a fake: the tests talk
/// to it as the Netjes client router does (page payloads as JSON, server action calls) and as plain form posts.
/// Every test class that uses it, or starts a Postgres of its own, carries <c>[Trait("Category", "Docker")]</c>: CI on
/// macOS and Windows, which have no Linux containers, leaves them out with <c>--filter-not-trait Category=Docker</c>.
/// <see cref="DockerTraitTests"/> fails when one does not.
/// </summary>
public sealed class TaskBoardApp : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17.11-alpine").Build();

    public FakeRenderer Renderer { get; } = new();

    /// <summary>The reverse proxy's network in these tests: X-Forwarded-* is believed from these addresses only.</summary>
    public const string ProxyNetwork = "192.0.2.0/24";

    /// <summary>TestServer requests have no client address; one in this header becomes the connection's.</summary>
    public const string RemoteAddressHeader = "X-Test-Remote-Address";

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        await TaskBoard.Infrastructure.Database.MigrateAsync(Services);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseContentRoot(ExampleRoot);
        builder.UseSetting("ConnectionStrings:TaskBoard", _postgres.GetConnectionString());
        // Development mode reads the routes from app/ on disk; nothing is rendered, so no Vite or Node is needed.
        builder.UseSetting("Netjes:Mode", "Development");
        builder.UseSetting("ForwardedHeaders:KnownNetworks:0", ProxyNetwork);
        // TestServer requests have no client address, so every test shares one per-address window. The limit tests
        // run their own app with low limits and real addresses (AuthLimitTests).
        builder.UseSetting("AuthLimits:SignInsPerAddress", "100000");
        builder.UseSetting("AuthLimits:SignUpsPerAddress", "100000");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<ISsrRenderer>(Renderer);
            services.AddTransient<IStartupFilter, RemoteAddressFromHeader>();
        });
    }

    /// <summary>A visitor on https, as in production, where the auth cookie is Secure and never sent over http.</summary>
    public Browser NewBrowser() =>
        new(CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true,
        }));

    public async Task<T> WithDbAsync<T>(Func<TaskBoardDb, Task<T>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<TaskBoardDb>());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    internal static string ExampleRoot
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "examples", "TaskBoard");
                if (File.Exists(Path.Combine(candidate, "TaskBoard.csproj")))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException("examples/TaskBoard not found above the test output directory.");
        }
    }
}

/// <summary>Sets the connection's remote address from <see cref="TaskBoardApp.RemoteAddressHeader"/>, before the app's middleware.</summary>
internal sealed class RemoteAddressFromHeader : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((http, nextMiddleware) =>
        {
            if (IPAddress.TryParse(http.Request.Headers[TaskBoardApp.RemoteAddressHeader].ToString(), out var address))
            {
                http.Connection.RemoteIpAddress = address;
            }

            return nextMiddleware(http);
        });
        next(app);
    };
}

[CollectionDefinition(Name)]
public sealed class TaskBoardCollection : ICollectionFixture<TaskBoardApp>
{
    public const string Name = "TaskBoard";
}

public sealed class FakeRenderer : ISsrRenderer
{
    private volatile string? _last;

    /// <summary>The payload of the last document rendered (tests in the collection run one at a time).</summary>
    public string Last => _last ?? throw new InvalidOperationException("Nothing was rendered.");

    /// <summary>The same, or null when nothing was rendered yet (a test alone in the fixture).</summary>
    public string? LastOrNull => _last;

    public Task<string> RenderAsync(string payloadJson, CancellationToken cancellationToken)
    {
        _last = payloadJson;
        return Task.FromResult("<!DOCTYPE html><html><body>rendered</body></html>");
    }
}

/// <summary>One visitor: a cookie jar, the last page payload (for its antiforgery token), and the Netjes protocol.</summary>
public sealed class Browser(HttpClient http)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private JsonNode? _antiforgery;

    /// <summary>
    /// A visitor of <paramref name="app"/> at the address <paramref name="from"/>, or, with <paramref name="via"/>,
    /// one at <paramref name="from"/> coming through the proxy at <paramref name="via"/>.
    /// </summary>
    public static Browser At(WebApplicationFactory<Program> app, string from, string? via = null)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        client.DefaultRequestHeaders.Add(TaskBoardApp.RemoteAddressHeader, via ?? from);
        if (via is not null)
        {
            client.DefaultRequestHeaders.Add("X-Forwarded-For", from);
            client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        }

        return new Browser(client);
    }

    public HttpClient Http { get; } = http;

    /// <summary>A client navigation: the page payload as JSON.</summary>
    public async Task<(HttpStatusCode Status, JsonNode Payload)> PageAsync(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(NetjesHeaders.Data, "1");
        var response = await Http.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        // Pages with Page.Defer values stream NDJSON: the payload is the first line.
        var payload = JsonNode.Parse(text.Split('\n', 2)[0])!;
        if (payload["antiforgery"] is { } token)
        {
            _antiforgery = token.DeepClone();
        }

        return (response.StatusCode, payload);
    }

    /// <summary>
    /// A page request as it is sent, for tests that read the raw answer: a document (<paramref name="data"/> false) or a
    /// client navigation, which may come from a page that holds the layout segments with <paramref name="heldSegments"/>
    /// keys (the router sends them so the server can skip those loaders).
    /// </summary>
    public async Task<(HttpStatusCode Status, string? ContentType, string Body)> RequestPageAsync(
        string url, bool data, IEnumerable<string>? heldSegments = null, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        if (data)
        {
            request.Headers.Add(NetjesHeaders.Data, "1");
        }

        if (heldSegments is not null)
        {
            request.Headers.Add(NetjesHeaders.Segments, string.Join(",", heldSegments));
        }

        var response = await Http.SendAsync(request, Ct);
        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>The deferred values of a streamed page, by id.</summary>
    public async Task<Dictionary<string, JsonNode?>> DeferredAsync(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(NetjesHeaders.Data, "1");
        var text = await (await Http.SendAsync(request, Ct)).Content.ReadAsStringAsync(Ct);
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(line => JsonNode.Parse(line)!)
            .ToDictionary(line => (string)line["id"]!, line => line["value"]?.DeepClone());
    }

    /// <summary>
    /// A server action call as <c>useAction</c> makes it from a form: the fields form-encoded (null ones left out),
    /// the token as a header.
    /// </summary>
    public Task<(HttpStatusCode Status, JsonNode Response)> ActionAsync(string name, object input) =>
        ActionAsync(name, [.. input.GetType().GetProperties()
            .Select(p => (p.Name, Value: p.GetValue(input)))
            .Where(f => f.Value is not null)
            .Select(f => (f.Name, Convert.ToString(f.Value, System.Globalization.CultureInfo.InvariantCulture)!))]);

    /// <summary>A server action call with these form fields, as <see cref="ActionAsync(string, object)"/> sends them.</summary>
    public async Task<(HttpStatusCode Status, JsonNode Response)> ActionAsync(string name, (string Name, string Value)[] input)
    {
        var token = await TokenAsync();
        var fields = input.Select(f => new KeyValuePair<string, string>(f.Name, f.Value));
        var request = new HttpRequestMessage(HttpMethod.Post, $"/_netjes/actions/{name}") { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Add(NetjesHeaders.Data, "1");
        request.Headers.Add((string)token["headerName"]!, (string)token["requestToken"]!);
        var response = await Http.SendAsync(request, Ct);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!);
    }

    /// <summary>A plain form post to a server action, as a browser without JavaScript sends it.</summary>
    public async Task<HttpResponseMessage> PostFormAsync(string name, params (string Name, string Value)[] fields)
    {
        var token = await TokenAsync();
        return await Http.PostAsync(
            $"/_netjes/actions/{name}",
            new FormUrlEncodedContent([
                .. fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)),
                new((string)token["formFieldName"]!, (string)token["requestToken"]!),
            ]),
            Ct);
    }

    /// <summary>Registers and signs in a fresh user; returns their email.</summary>
    public async Task<string> SignUpAsync(string? name = null)
    {
        var email = $"user-{Guid.NewGuid():N}@taskboard.test";
        await PageAsync("/register");
        var (status, response) = await ActionAsync("auth.register", new { name = name ?? "Test User", email, password = "correct horse battery" });
        Assert.True(status == HttpStatusCode.OK, response.ToJsonString());
        // Antiforgery tokens belong to a user: the next page carries a new one.
        await PageAsync("/projects");
        return email;
    }

    private async Task<JsonNode> TokenAsync()
    {
        if (_antiforgery is null)
        {
            var response = await Http.GetAsync("/_netjes/antiforgery", Ct);
            _antiforgery = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        }

        return _antiforgery;
    }
}
