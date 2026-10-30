using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Netjes.Tests.Endpoints;

public class ServerActionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record GreetInput(string Name);

    private sealed record Token(string Cookie, string Field, string Header, string Value);

    private static async Task<Token> TokenAsync(TestApp app, string page = "/")
    {
        var response = await app.GetDataAsync(page);
        var antiforgery = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["antiforgery"]!;
        return new Token(
            string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])),
            (string)antiforgery["formFieldName"]!,
            (string)antiforgery["headerName"]!,
            (string)antiforgery["requestToken"]!);
    }

    private static HttpRequestMessage JsonCall(string action, object body, Token token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/_netjes/actions/{action}") { Content = JsonContent.Create(body) };
        request.Headers.Add(NetjesHeaders.Data, "1");
        request.Headers.Add(token.Header, token.Value);
        request.Headers.Add("Cookie", token.Cookie);
        return request;
    }

    private sealed record RawResponse(HttpStatusCode Status, string ContentType, string Body);

    /// <summary>
    /// Posts an action form whose Content-Length declares <c>declaredBytes</c> but whose body is never sent, over a raw
    /// socket. Kestrel answers 413 from the declared length and closes the connection; uploading a real over-limit
    /// body races that close and fails the client's write with "Broken pipe" before it reads the response.
    /// </summary>
    private static async Task<RawResponse> PostDeclaringBodyAsync(TestApp app, string action, Token token, long declaredBytes, bool data)
    {
        var uri = app.Client.BaseAddress!;
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(uri.Host, uri.Port, Ct);
        await using var stream = tcp.GetStream();
        var head = $"POST /_netjes/actions/{action} HTTP/1.1\r\nHost: {uri.Authority}\r\nConnection: close\r\n"
            + $"Cookie: {token.Cookie}\r\nContent-Type: application/x-www-form-urlencoded\r\nContent-Length: {declaredBytes}\r\n"
            + (data ? $"{NetjesHeaders.Data}: 1\r\n" : "") + "\r\n";
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(head), Ct);

        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        var text = await reader.ReadToEndAsync(Ct);
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var lines = text[..split].Split("\r\n");
        var status = (HttpStatusCode)int.Parse(lines[0].Split(' ')[1]);
        var contentType = lines.Skip(1).Select(l => l.Split(':', 2)).FirstOrDefault(h => h[0].Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?[1].Trim() ?? "";
        return new RawResponse(status, contentType, text[(split + 4)..]);
    }

    private static HttpRequestMessage FormPost(string action, Token token, params (string Name, string Value)[] fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/_netjes/actions/{action}")
        {
            Content = new FormUrlEncodedContent([.. fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)), new(token.Field, token.Value)]),
        };
        request.Headers.Add("Cookie", token.Cookie);
        return request;
    }

    private static async Task<JsonNode> JsonOf(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

    [Fact]
    public async Task Call_ReturnsTheActionResult()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"], map: a => a.MapAction("greet", (GreetInput input) => ServerAction.Ok(new { Greeting = $"Hello, {input.Name}" })));
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(JsonCall("greet", new { name = "Ada" }, token), Ct);
        var json = await JsonOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("action", (string?)json["kind"]);
        Assert.True((bool?)json["ok"]);
        Assert.Equal("Hello, Ada", (string?)json["result"]!["greeting"]);
    }

    [Fact]
    public async Task TokenEndpoint_GivesCachedPagesATokenForActions()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a =>
            {
                a.MapPageLoader("/", () => Page.Props()).CachePage(TimeSpan.FromMinutes(1));
                a.MapAction("noop", () => ServerAction.Ok("done"));
            },
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache());
        Assert.Null((await app.GetDataJsonAsync("/"))["antiforgery"]);

        var response = await app.Client.GetAsync("/_netjes/antiforgery", Ct);
        var json = await JsonOf(response);
        var token = new Token(
            string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])),
            (string)json["formFieldName"]!,
            (string)json["headerName"]!,
            (string)json["requestToken"]!);
        var call = await app.Client.SendAsync(JsonCall("noop", new { }, token), Ct);

        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.OK, call.StatusCode);
    }

    [Fact]
    public async Task Invalid_Returns422WithErrorsKeyedLikeTheJson()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("greet", (GreetInput input) =>
                ServerAction.Invalid(new Dictionary<string, string[]> { ["Name"] = ["Tell us your name."] })));
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(JsonCall("greet", new { name = "" }, token), Ct);
        var json = await JsonOf(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.False((bool?)json["ok"]);
        Assert.Equal("Tell us your name.", (string?)json["errors"]!["name"]![0]);
    }

    [Fact]
    public async Task FormCall_BindsFormFields()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"], map: a => a.MapAction("greet", ([FromForm] string name) => ServerAction.Ok(name.ToUpperInvariant())));
        var token = await TokenAsync(app);
        var request = FormPost("greet", token, ("name", "ada"));
        request.Headers.Add(NetjesHeaders.Data, "1");

        var json = await JsonOf(await app.Client.SendAsync(request, Ct));

        Assert.Equal("ADA", (string?)json["result"]);
    }

    [Fact]
    public async Task Calls_OverTheActionBodySizeLimit_AreRejected()
    {
        var ran = 0;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("greet", ([FromForm] string name) => { Interlocked.Increment(ref ran); return ServerAction.Ok(name.Length); }),
            kestrel: true);
        var token = await TokenAsync(app);

        var response = await PostDeclaringBodyAsync(app, "greet", token, 1024 * 1024 + 1, data: true);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.Status);
        Assert.Equal(0, Volatile.Read(ref ran));
        Assert.Contains("\"kind\":\"action\"", response.Body);
    }

    [Fact]
    public async Task FormPosts_OverTheActionBodySizeLimit_GetTheErrorPage()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/error.tsx"],
            map: a => a.MapAction("greet", ([FromForm] string name) => ServerAction.Ok()),
            kestrel: true);
        var token = await TokenAsync(app);

        var response = await PostDeclaringBodyAsync(app, "greet", token, 1024 * 1024 + 1, data: false);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.Status);
        Assert.StartsWith("text/html", response.ContentType);
    }

    [Fact]
    public async Task AnActionsOwnRequestSizeLimit_ReplacesTheDefault()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a =>
            {
                a.MapAction("attribute", [RequestSizeLimit(4 * 1024 * 1024)] ([FromForm] string name) => ServerAction.Ok(name.Length));
                a.MapAction("metadata", ([FromForm] string name) => ServerAction.Ok(name.Length))
                    .WithMetadata(new RequestSizeLimitAttribute(4 * 1024 * 1024));
            },
            kestrel: true);
        var token = await TokenAsync(app);

        foreach (var action in new[] { "attribute", "metadata" })
        {
            var request = FormPost(action, token, ("name", new string('x', 2 * 1024 * 1024)));
            request.Headers.Add(NetjesHeaders.Data, "1");

            var response = await app.Client.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task ActionBodySizeLimit_Null_LeavesTheServersLimit()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o => o.ActionBodySizeLimit = null,
            map: a => a.MapAction("greet", ([FromForm] string name) => ServerAction.Ok(name.Length)),
            kestrel: true);
        var token = await TokenAsync(app);
        var request = FormPost("greet", token, ("name", new string('x', 2 * 1024 * 1024)));
        request.Headers.Add(NetjesHeaders.Data, "1");

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ActionBodySizeLimit_NeverRaisesAStricterServerLimit()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("greet", ([FromForm] string name) => ServerAction.Ok(name.Length)),
            services: s => s.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o => o.Limits.MaxRequestBodySize = 100_000),
            kestrel: true);
        var token = await TokenAsync(app);

        var response = await PostDeclaringBodyAsync(app, "greet", token, 500 * 1024, data: true);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.Status);
    }

    [Fact]
    public async Task CallWithoutAToken_IsRejected()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], map: a => a.MapAction("noop", () => ServerAction.Ok()));
        var request = new HttpRequestMessage(HttpMethod.Post, "/_netjes/actions/noop") { Content = JsonContent.Create(new { }) };
        request.Headers.Add(NetjesHeaders.Data, "1");

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // The client router sends a call again, with a fresh token, only when this says the server refused its token: then
    // the action did not run. Any other 400 may come from an action that changed data first.
    private static string? AntiforgeryMarker(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Netjes-Antiforgery", out var values) ? string.Join(",", values) : null;

    // A token from another antiforgery cookie: what a page holds after the user signed in or out on another tab.
    private static async Task<Token> MismatchedTokenAsync(TestApp app) =>
        await TokenAsync(app) with { Value = (await TokenAsync(app)).Value };

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task BadToken_InAJsonCall_IsMarkedAsAnAntiforgeryRejection(string environment)
    {
        var ran = 0;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("greet", (GreetInput input) =>
            {
                ran++;
                return ServerAction.Ok();
            }),
            configure: o => o.Mode = NetjesMode.Development,
            environment: environment);
        var token = await MismatchedTokenAsync(app);

        var response = await app.Client.SendAsync(JsonCall("greet", new { name = "Ada" }, token), Ct);
        var json = await JsonOf(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("action", (string?)json["kind"]);
        Assert.Equal("invalid", AntiforgeryMarker(response));
        Assert.Equal(0, ran);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task BadToken_InAFormCall_IsMarkedWhenFormBindingRejectsIt(string environment)
    {
        // Form binding checks the token before the action's filters run: it throws in Development and answers an
        // empty 400 elsewhere.
        var ran = 0;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("greet", ([FromForm] string name) =>
            {
                ran++;
                return ServerAction.Ok();
            }),
            configure: o => o.Mode = NetjesMode.Development,
            environment: environment);
        var request = FormPost("greet", await MismatchedTokenAsync(app), ("name", "Ada"));
        request.Headers.Add(NetjesHeaders.Data, "1");

        var response = await app.Client.SendAsync(request, Ct);
        var json = await JsonOf(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("action", (string?)json["kind"]);
        Assert.Equal("invalid", AntiforgeryMarker(response));
        Assert.Equal(0, ran);
    }

    [Fact]
    public async Task BadToken_IsMarked_WhenOnlyTheActionsFilterJudgedIt()
    {
        // As when the antiforgery middleware did not judge the request (an app that suppresses ASP.NET Core's
        // security-metadata check): there is no verdict, so the filter checks the token itself.
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("noop", () => ServerAction.Ok()),
            configureApp: a => a.Use((http, next) =>
            {
                http.Features.Set<Microsoft.AspNetCore.Antiforgery.IAntiforgeryValidationFeature>(null);
                return next(http);
            }));

        var response = await app.Client.SendAsync(JsonCall("noop", new { }, await MismatchedTokenAsync(app)), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid", AntiforgeryMarker(response));
    }

    [Theory]
    [InlineData("Development", "throws")]
    [InlineData("Production", "throws")]
    [InlineData("Development", "returns")]
    [InlineData("Production", "returns")]
    [InlineData("Development", "binding")]
    [InlineData("Production", "binding")]
    public async Task OtherBadRequests_AreNotMarked(string environment, string action)
    {
        var saved = 0;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a =>
            {
                // Changed data, then answered 400: sending the call again would save twice.
                a.MapAction("throws", Func<ServerActionResult> () =>
                {
                    saved++;
                    throw new BadHttpRequestException("That list is closed.");
                });
                a.MapAction("returns", () =>
                {
                    saved++;
                    return Results.BadRequest();
                });
                a.MapAction("binding", ([FromForm] int count) => ServerAction.Ok(count));
            },
            configure: o => o.Mode = NetjesMode.Development,
            environment: environment);
        var token = await TokenAsync(app);
        var request = FormPost(action, token, ("count", "many"));
        request.Headers.Add(NetjesHeaders.Data, "1");

        var response = await app.Client.SendAsync(request, Ct);
        var json = await JsonOf(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("action", (string?)json["kind"]);
        Assert.Null(AntiforgeryMarker(response));
        Assert.Equal(action == "binding" ? 0 : 1, saved);
    }

    [Theory]
    [InlineData("Origin", "https://evil.example")]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    public async Task CrossSiteCall_IsForbidden(string header, string value)
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], map: a => a.MapAction("noop", () => ServerAction.Ok()));
        var token = await TokenAsync(app);
        var request = JsonCall("noop", new { }, token);
        request.Headers.Add(header, value);

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SameOriginCall_IsAllowed()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], map: a => a.MapAction("noop", () => ServerAction.Ok()));
        var token = await TokenAsync(app);
        var request = JsonCall("noop", new { }, token);
        request.Headers.Add("Origin", app.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("forbidden", 403)]
    [InlineData("missing", 404)]
    public async Task ForbiddenAndNotFound_AnswerWithTheirStatus(string action, int status)
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/error.tsx"],
            map: a =>
            {
                a.MapAction("forbidden", () => ServerAction.Forbidden());
                a.MapAction("missing", () => ServerAction.NotFound());
            });
        var token = await TokenAsync(app);

        var call = await app.Client.SendAsync(JsonCall(action, new { }, token), Ct);
        var json = await JsonOf(call);
        var post = await app.Client.SendAsync(FormPost(action, token, ("__netjes_return", "/")), Ct);

        Assert.Equal(status, (int)call.StatusCode);
        Assert.Equal("action", (string?)json["kind"]);
        Assert.False((bool?)json["ok"]);
        Assert.Equal(status, (int)post.StatusCode);
        Assert.Equal("text/html", post.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Redirect_TellsTheClientWhereToGo()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], map: a => a.MapAction("create", () => ServerAction.Redirect("/posts/1")));
        var token = await TokenAsync(app);

        var json = await JsonOf(await app.Client.SendAsync(JsonCall("create", new { }, token), Ct));

        Assert.True((bool?)json["ok"]);
        Assert.Equal("/posts/1", (string?)json["redirect"]);
    }

    [Fact]
    public async Task Revalidate_EvictsCachedPagesByTagAndByPath_AndTellsTheClientToRefresh()
    {
        var loads = new Dictionary<string, int>();
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/posts/[id]/page.tsx"],
            map: a =>
            {
                a.MapPageLoader("/posts/[id]", (string id) =>
                {
                    loads[id] = loads.GetValueOrDefault(id) + 1;
                    return Page.Props(new { Id = id });
                }).CachePage(TimeSpan.FromMinutes(5), "posts");
                a.MapAction("edit", (EditInput input) =>
                    input.All ? ServerAction.Ok().Revalidate("posts") : ServerAction.Ok().RevalidatePath($"/posts/{input.Id}"));
            },
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache());
        var token = await TokenAsync(app);
        await app.GetDataAsync("/posts/1");
        await app.GetDataAsync("/posts/2");

        var byPath = await JsonOf(await app.Client.SendAsync(JsonCall("edit", new { id = "1", all = false }, token), Ct));
        await app.GetDataAsync("/posts/1");
        await app.GetDataAsync("/posts/2");
        Assert.Equal((2, 1), (loads["1"], loads["2"]));

        await app.Client.SendAsync(JsonCall("edit", new { id = "1", all = true }, token), Ct);
        await app.GetDataAsync("/posts/1");
        await app.GetDataAsync("/posts/2");
        Assert.Equal((3, 2), (loads["1"], loads["2"]));
        Assert.True((bool?)byPath["revalidate"]);
    }

    public sealed record EditInput(string Id, bool All);

    public sealed record SignInput([Required, MinLength(2)] string Name, [Range(1, 5)] int Stars);

    // One AddValidation call site: the .NET 10 validation generator emits a resolver per call site and clashes.
    private static void WithValidation(IServiceCollection services) => services.AddValidation();

    public sealed record ProfileInput(
        [Required, StringLength(20, MinimumLength = 2)] string Name,
        [EmailAddress] string? Email,
        [Range(0, 120)] int Age);

    public sealed class PasswordInput : IValidatableObject
    {
        [Required] public string Password { get; init; } = "";
        public string Confirm { get; init; } = "";

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (Password != Confirm) yield return new ValidationResult("The passwords differ.", [nameof(Confirm)]);
        }
    }

    [Fact]
    public async Task DataAnnotationsOnActionInputs_AreChecked_WithoutAddValidation()
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("profile", ([FromForm] ProfileInput input) =>
            {
                calls++;
                return ServerAction.Ok();
            }));
        var token = await TokenAsync(app);
        var request = FormPost("profile", token, ("Name", "A"), ("Email", "nope"), ("Age", "200"));
        request.Headers.Add(NetjesHeaders.Data, "1");

        var response = await app.Client.SendAsync(request, Ct);
        var json = await JsonOf(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(["age", "email", "name"], json["errors"]!.AsObject().Select(e => e.Key).Order());
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task BindingFailures_ReachTheClientAsActionResponses()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"], map: a => a.MapAction("count", ([FromForm] int count) => ServerAction.Ok(count)));
        var token = await TokenAsync(app);
        var request = FormPost("count", token, ("count", "many"));
        request.Headers.Add(NetjesHeaders.Data, "1");

        var response = await app.Client.SendAsync(request, Ct);
        var json = await JsonOf(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("action", (string?)json["kind"]);
        Assert.False((bool?)json["ok"]);
        Assert.Equal(400, (int?)json["status"]);
    }

    [Fact]
    public async Task ValidInput_ReachesTheAction()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"], map: a => a.MapAction("profile", ([FromForm] ProfileInput input) => ServerAction.Ok(input.Name)));
        var token = await TokenAsync(app);
        var request = FormPost("profile", token, ("Name", "Ada"), ("Email", "ada@example.test"), ("Age", "36"));
        request.Headers.Add(NetjesHeaders.Data, "1");

        var json = await JsonOf(await app.Client.SendAsync(request, Ct));

        Assert.Equal("Ada", (string?)json["result"]);
    }

    [Fact]
    public async Task IValidatableObjectInputs_AreChecked()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"], map: a => a.MapAction("password", ([FromForm] PasswordInput input) => ServerAction.Ok()));
        var token = await TokenAsync(app);
        var request = FormPost("password", token, ("Password", "a"), ("Confirm", "b"));
        request.Headers.Add(NetjesHeaders.Data, "1");

        var json = await JsonOf(await app.Client.SendAsync(request, Ct));

        Assert.Equal("The passwords differ.", (string?)json["errors"]!["confirm"]![0]);
    }

    [JsonDerivedType(typeof(Square), "square")]
    public abstract class Figure;

    public sealed class Square : Figure
    {
        [Required(ErrorMessage = "Give the side.")]
        public string? Side { get; init; }
    }

    [JsonDerivedType(typeof(Circle), "circle")]
    public class Outline
    {
        public string? Label { get; init; }
    }

    public sealed class Circle : Outline
    {
        [Required(ErrorMessage = "Give the radius.")]
        public string? Radius { get; init; }
    }

    [Theory]
    [InlineData("figures.save", """{"$type":"square"}""", "side", "Give the side.")]
    [InlineData("outlines.save", """{"$type":"circle","label":"Wheel"}""", "radius", "Give the radius.")]
    public async Task ADerivedJsonInput_IsCheckedByItsOwnRules_WhenTheDeclaredTypeHasNone(string action, string body, string field, string message)
    {
        var calls = new List<string>();
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a =>
            {
                a.MapAction("figures.save", (Figure input) =>
                {
                    calls.Add(input.GetType().Name);
                    return ServerAction.Ok();
                });
                a.MapAction("outlines.save", (Outline input) =>
                {
                    calls.Add(input.GetType().Name);
                    return ServerAction.Ok();
                });
            });
        var token = await TokenAsync(app);
        var request = JsonCall(action, new { }, token);
        request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(message, (string?)(await JsonOf(response))["errors"]![field]![0]);
        Assert.Empty(calls);
    }

    public sealed record NoteInput
    {
        [Required(ErrorMessage = "Write something first.")]
        public string? Text { get; init; }

        public bool Pinned { get; init; }
    }

    public sealed record PositionalNoteInput([Required(ErrorMessage = "Write something first.")] string? Text, bool Pinned);

    /// <summary>A form call as the client router sends one: the token in a header, the page's URL in the body.</summary>
    private static HttpRequestMessage ClientFormCall(string action, Token token, params (string Name, string Value)[] fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/_netjes/actions/{action}")
        {
            Content = new FormUrlEncodedContent([.. fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)), new("__netjes_return", "/")]),
        };
        request.Headers.Add(NetjesHeaders.Data, "1");
        request.Headers.Add(token.Header, token.Value);
        request.Headers.Add("Cookie", token.Cookie);
        return request;
    }

    /// <summary>A form posted by the client router (<paramref name="javaScript"/>) or by the browser itself, from <c>/</c>.</summary>
    private static HttpRequestMessage FormOf(bool javaScript, string action, Token token, params (string Name, string Value)[] fields) =>
        javaScript ? ClientFormCall(action, token, fields) : FormPost(action, token, [.. fields, ("__netjes_return", "/")]);

    private static async Task<JsonNode> ErrorsOf(bool javaScript, HttpResponseMessage response, TestApp app) =>
        javaScript ? (await JsonOf(response))["errors"]! : app.Renderer.LastPayload["actionState"]!["errors"]!;

    private static void MapNoteActions(WebApplication app, List<string> calls)
    {
        app.MapAction("notes.add", ([FromForm] NoteInput input) =>
        {
            calls.Add(input is null ? "null" : $"{input.Text}|{input.Pinned}");
            return ServerAction.Ok();
        });
        app.MapAction("notes.positional", ([FromForm] PositionalNoteInput input) =>
        {
            calls.Add(input is null ? "null" : $"{input.Text}|{input.Pinned}");
            return ServerAction.Ok();
        });
    }

    [Theory]
    [InlineData("notes.add", true)]
    [InlineData("notes.add", false)]
    [InlineData("notes.positional", true)]
    [InlineData("notes.positional", false)]
    public async Task FormInput_WithNoneOfItsFieldsPosted_ReportsItsRequiredMembers(string action, bool javaScript)
    {
        // Form binding gives no object at all for a post without any of the type's fields.
        var calls = new List<string>();
        await using var app = await TestApp.StartAsync(["app/layout.tsx", "app/page.tsx"], map: a => MapNoteActions(a, calls));
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(FormOf(javaScript, action, token), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("Write something first.", (string?)(await ErrorsOf(javaScript, response, app))["text"]![0]);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task FormInput_WithNoneOfItsFieldsPostedAsMultipart_ReportsItsRequiredMembers()
    {
        // The client sends a form with a file input as multipart; an empty file input posts an empty part.
        var calls = new List<string>();
        await using var app = await TestApp.StartAsync(["app/page.tsx"], map: a => MapNoteActions(a, calls));
        var token = await TokenAsync(app);
        var noFile = new ByteArrayContent([]);
        noFile.Headers.ContentDisposition = new("form-data") { Name = "\"photo\"", FileName = "\"\"" };
        var request = ClientFormCall("notes.add", token);
        request.Content = new MultipartFormDataContent { { new StringContent("/"), "__netjes_return" }, noFile };

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("Write something first.", (string?)(await JsonOf(response))["errors"]!["text"]![0]);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task FormInput_WithNoneOfItsFieldsPosted_AndABadToken_IsRefusedForTheToken()
    {
        var calls = new List<string>();
        await using var app = await TestApp.StartAsync(["app/page.tsx"], map: a => MapNoteActions(a, calls));
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(ClientFormCall("notes.add", token with { Value = "forged" }), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid", response.Headers.GetValues("X-Netjes-Antiforgery").Single());
        Assert.Empty(calls);
    }

    public sealed record SettingsInput
    {
        public bool Newsletter { get; init; }

        public bool Digest { get; init; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FormInput_WithNoneOfItsFieldsPosted_ReachesTheActionEmpty_WhenThatIsValid(bool javaScript)
    {
        // A form of checkboxes posts none of its fields when every box is unchecked.
        var calls = new List<string>();
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx"],
            map: a => a.MapAction("settings.save", ([FromForm] SettingsInput input) =>
            {
                calls.Add(input is null ? "null" : $"{input.Newsletter}|{input.Digest}");
                return ServerAction.Ok();
            }));
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(FormOf(javaScript, "settings.save", token), Ct);

        Assert.Equal(javaScript ? HttpStatusCode.OK : HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal(["False|False"], calls);
    }

    [Fact]
    public async Task NullableFormInput_WithNoneOfItsFieldsPosted_ReachesTheActionAsNull()
    {
        var calls = new List<string>();
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("notes.add", ([FromForm] NoteInput? input) =>
            {
                calls.Add(input is null ? "null" : $"{input.Text}|{input.Pinned}");
                return ServerAction.Ok();
            }));
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(ClientFormCall("notes.add", token), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["null"], calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FormInput_WithInitProperties_TakesAnUncheckedCheckboxAsFalse_AndReportsAMissingText(bool javaScript)
    {
        var calls = new List<string>();
        await using var app = await TestApp.StartAsync(["app/layout.tsx", "app/page.tsx"], map: a => MapNoteActions(a, calls));
        var token = await TokenAsync(app);

        var withoutCheckbox = await app.Client.SendAsync(FormOf(javaScript, "notes.add", token, ("text", "Milk")), Ct);
        var withoutText = await app.Client.SendAsync(FormOf(javaScript, "notes.add", token, ("pinned", "true")), Ct);

        Assert.Equal(javaScript ? HttpStatusCode.OK : HttpStatusCode.SeeOther, withoutCheckbox.StatusCode);
        Assert.Equal(["Milk|False"], calls);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutText.StatusCode);
        Assert.Equal("Write something first.", (string?)(await ErrorsOf(javaScript, withoutText, app))["text"]![0]);
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Development", false)]
    [InlineData("Production", true)]
    [InlineData("Production", false)]
    public async Task PositionalFormInput_WithOneOfItsFieldsLeftOut_IsABadRequest(string environment, bool javaScript)
    {
        // Form binding builds a positional record only from every one of its constructor's fields. It throws in
        // Development; elsewhere it answers 400 once the action's filters have run, which must not take that for an
        // input without fields.
        var calls = new List<string>();
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx"], map: a => MapNoteActions(a, calls), configure: o => o.Mode = NetjesMode.Development, environment: environment);
        var token = await TokenAsync(app);

        var withoutCheckbox = await app.Client.SendAsync(FormOf(javaScript, "notes.positional", token, ("text", "Milk")), Ct);
        var withoutText = await app.Client.SendAsync(FormOf(javaScript, "notes.positional", token, ("pinned", "true")), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, withoutCheckbox.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, withoutText.StatusCode);
        Assert.Empty(calls);
    }

    public sealed record CountedNoteInput
    {
        [Required(ErrorMessage = "Write something first.")]
        public string? Text { get; init; }

        public int Count { get; init; }
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Development", false)]
    [InlineData("Production", true)]
    [InlineData("Production", false)]
    public async Task FormInput_WithAValueFormBindingCannotRead_IsABadRequest_NotAFieldError(string environment, bool javaScript)
    {
        var calls = new List<string>();
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx"],
            map: a => a.MapAction("notes.count", ([FromForm] CountedNoteInput input) =>
            {
                calls.Add(input is null ? "null" : $"{input.Text}|{input.Count}");
                return ServerAction.Ok();
            }),
            configure: o => o.Mode = NetjesMode.Development,
            environment: environment);
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(FormOf(javaScript, "notes.count", token, ("count", "many")), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task FormCall_WithoutABody_IsABadRequest()
    {
        var calls = new List<string>();
        await using var app = await TestApp.StartAsync(["app/page.tsx"], map: a => MapNoteActions(a, calls));
        var token = await TokenAsync(app);
        var request = ClientFormCall("notes.add", token);
        request.Content = new ByteArrayContent([]) { Headers = { ContentType = new("application/x-www-form-urlencoded") } };

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("action", (string?)(await JsonOf(response))["kind"]);
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData("{}", HttpStatusCode.UnprocessableEntity)]
    [InlineData("null", HttpStatusCode.BadRequest)]
    [InlineData("", HttpStatusCode.BadRequest)]
    public async Task JsonInput_EmptyOrNull_NeverReachesTheAction(string body, HttpStatusCode status)
    {
        var calls = new List<string>();
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("notes.add", (PositionalNoteInput input) =>
            {
                calls.Add(input is null ? "null" : $"{input.Text}|{input.Pinned}");
                return ServerAction.Ok();
            }));
        var token = await TokenAsync(app);
        var request = JsonCall("notes.add", new { }, token);
        request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        var response = await app.Client.SendAsync(request, Ct);
        var json = await JsonOf(response);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal("action", (string?)json["kind"]);
        if (status == HttpStatusCode.UnprocessableEntity) Assert.Equal("Write something first.", (string?)json["errors"]!["text"]![0]);
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PageAction_FormInputWithNoneOfItsFieldsPosted_ReachesTheActionEmpty_WhenThatIsValid(bool javaScript)
    {
        var calls = new List<string>();
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/notes/page.tsx"],
            map: a => a.MapPageAction("/notes", ([FromForm] SettingsInput input) =>
            {
                calls.Add(input is null ? "null" : $"{input.Newsletter}|{input.Digest}");
                return Page.Redirect("/notes");
            }));
        var token = await TokenAsync(app, "/notes");
        var request = FormOf(javaScript, "-", token);
        request.RequestUri = new Uri("/notes", UriKind.Relative);

        var response = await app.Client.SendAsync(request, Ct);

        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.SeeOther, $"{response.StatusCode}");
        Assert.Equal(["False|False"], calls);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task PageAction_PositionalFormInputWithOneOfItsFieldsLeftOut_IsABadRequest(string environment)
    {
        var calls = new List<string>();
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/notes/page.tsx"],
            map: a => a.MapPageAction("/notes", ([FromForm] PositionalNoteInput input) =>
            {
                calls.Add(input is null ? "null" : $"{input.Text}|{input.Pinned}");
                return Page.Redirect("/notes");
            }),
            configure: o => o.Mode = NetjesMode.Development,
            environment: environment);
        var token = await TokenAsync(app, "/notes");
        var request = FormOf(javaScript: false, "-", token, ("text", "Milk"));
        request.RequestUri = new Uri("/notes", UriKind.Relative);

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(calls);
    }

    public sealed record GuestbookSignInput
    {
        [Required(ErrorMessage = "Tell us who you are.")]
        public string? Name { get; init; }

        public string? Message { get; init; }
    }

    /// <summary>
    /// A post of a page's <c>&lt;Form method="post"&gt;</c>, with the token in the hidden field it renders: by the client
    /// router (<paramref name="javaScript"/>), or by the browser itself.
    /// </summary>
    private static HttpRequestMessage PageActionPost(bool javaScript, string url, Token token, params (string Name, string Value)[] fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent([new(token.Field, token.Value), .. fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))]),
        };
        request.Headers.Add("Cookie", token.Cookie);
        if (javaScript)
        {
            request.Headers.Add(NetjesHeaders.Data, "1");
        }

        return request;
    }

    /// <summary>The page a page action answered with: the client router's payload, or the one the document was rendered from.</summary>
    private static async Task<JsonNode> PageOf(bool javaScript, HttpResponseMessage response, TestApp app) =>
        javaScript ? await JsonOf(response) : app.Renderer.LastPayload;

    private static void MapGuestbook(WebApplication app, List<string> calls)
    {
        app.MapPageLoader("/guestbook", () => Page.Props(new { Entries = new[] { "First!" } }));
        app.MapPageAction("/guestbook", ([FromForm] GuestbookSignInput input, [FromForm] string? mood, [FromForm] string? newPassword) =>
        {
            calls.Add(input is null ? "null" : $"{input.Name}|{input.Message}");
            return Page.Redirect("/guestbook");
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PageAction_InvalidInput_RendersThePageAgainFromItsLoader_WithTheErrorsAndTheDeclaredValues(bool javaScript)
    {
        var calls = new List<string>();
        await using var app = await TestApp.StartAsync(["app/layout.tsx", "app/page.tsx", "app/guestbook/page.tsx"], map: a => MapGuestbook(a, calls));
        var token = await TokenAsync(app, "/guestbook");

        var response = await app.Client.SendAsync(PageActionPost(javaScript, "/guestbook?from=home", token,
            ("name", ""), ("message", "Hi"), ("mood", "happy"), ("newPassword", "s3cret"), ("note", "undeclared")), Ct);
        var page = await PageOf(javaScript, response, app);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(javaScript ? "application/json" : "text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Empty(calls);
        Assert.Equal("page", (string?)page["kind"]);
        Assert.Equal(422, (int?)page["status"]);
        Assert.Equal("app/guestbook/page.tsx", (string?)page["page"]);
        Assert.Equal("/guestbook?from=home", (string?)page["url"]);
        Assert.Equal("First!", (string?)page["props"]!["entries"]![0]);
        var state = page["actionState"]!;
        Assert.Equal("/guestbook", (string?)state["action"]);
        Assert.Equal("Tell us who you are.", (string?)state["errors"]!["name"]![0]);
        Assert.Equal(["name", "message", "mood"], state["values"]!.AsObject().Select(v => v.Key));
        Assert.Equal("Hi", (string?)state["values"]!["message"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PageAction_ValidInput_ReachesTheAction(bool javaScript)
    {
        var calls = new List<string>();
        await using var app = await TestApp.StartAsync(["app/layout.tsx", "app/page.tsx", "app/guestbook/page.tsx"], map: a => MapGuestbook(a, calls));
        var token = await TokenAsync(app, "/guestbook");

        var response = await app.Client.SendAsync(PageActionPost(javaScript, "/guestbook", token, ("name", "Ada"), ("message", "Hi")), Ct);

        Assert.Equal(javaScript ? HttpStatusCode.OK : HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal(["Ada|Hi"], calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PageAction_InvalidInput_WithABadToken_IsRefusedForTheToken(bool javaScript)
    {
        // Nothing of the post is written back into a page before the token has been checked.
        var calls = new List<string>();
        await using var app = await TestApp.StartAsync(["app/layout.tsx", "app/page.tsx", "app/guestbook/page.tsx"], map: a => MapGuestbook(a, calls));
        var token = await TokenAsync(app, "/guestbook");

        var response = await app.Client.SendAsync(PageActionPost(javaScript, "/guestbook", token with { Value = "forged" }, ("name", ""), ("message", "<b>Hi</b>")), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(calls);
        Assert.DoesNotContain(app.Renderer.Payloads, p => p.Contains("actionState", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, "Sec-Fetch-Site", "cross-site")]
    [InlineData(false, "Sec-Fetch-Site", "cross-site")]
    [InlineData(false, "Origin", "https://elsewhere.example")]
    public async Task PageAction_InvalidInput_FromAnotherSite_IsForbidden(bool javaScript, string header, string value)
    {
        var calls = new List<string>();
        await using var app = await TestApp.StartAsync(["app/layout.tsx", "app/page.tsx", "app/guestbook/page.tsx"], map: a => MapGuestbook(a, calls));
        var token = await TokenAsync(app, "/guestbook");
        var request = PageActionPost(javaScript, "/guestbook", token, ("name", ""), ("message", "<b>Hi</b>"));
        request.Headers.Add(header, value);

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(calls);
        Assert.DoesNotContain(app.Renderer.Payloads, p => p.Contains("actionState", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PageAction_InvalidInput_UnderABasePath_RendersThePageAgain(bool javaScript)
    {
        var calls = new List<string>();
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/guestbook/page.tsx"], map: a => MapGuestbook(a, calls), pathBase: "/docs");
        var token = await TokenAsync(app, "/docs/guestbook");

        var response = await app.Client.SendAsync(PageActionPost(javaScript, "/docs/guestbook", token, ("name", "")), Ct);
        var page = await PageOf(javaScript, response, app);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("/docs/guestbook", (string?)page["url"]);
        Assert.Equal("Tell us who you are.", (string?)page["actionState"]!["errors"]!["name"]![0]);
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData(true, "/notes/caf%C3%A9", "café")]
    [InlineData(false, "/notes/caf%C3%A9", "café")]
    [InlineData(true, "/notes/a%20b?sort=%C3%A9", "a b")]
    public async Task PageAction_InvalidInput_AtAnEncodedUrl_RendersThePageWithItsOwnRouteValues(bool javaScript, string url, string id)
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/notes/[id]/page.tsx"],
            map: a =>
            {
                a.MapPageLoader("/notes/[id]", (string id, string? sort) => Page.Props(new { Id = id, Sort = sort }));
                a.MapPageAction("/notes/[id]", ([FromForm] GuestbookSignInput input) =>
                {
                    calls++;
                    return Page.Redirect("/");
                });
            });
        var token = await TokenAsync(app, url);

        var response = await app.Client.SendAsync(PageActionPost(javaScript, url, token, ("name", "")), Ct);
        var page = await PageOf(javaScript, response, app);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(id, (string?)page["params"]!["id"]);
        Assert.Equal(id, (string?)page["props"]!["id"]);
        Assert.Equal(url.Contains("sort") ? "é" : null, (string?)page["props"]!["sort"]);
        Assert.Equal(url, (string?)page["url"]);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task PageAction_IValidatableObjectInputs_AreChecked()
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/password/page.tsx"],
            map: a => a.MapPageAction("/password", ([FromForm] PasswordInput input) =>
            {
                calls++;
                return Page.Redirect("/");
            }));
        var token = await TokenAsync(app, "/password");

        var response = await app.Client.SendAsync(PageActionPost(true, "/password", token, ("Password", "a"), ("Confirm", "b")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("The passwords differ.", (string?)(await JsonOf(response))["actionState"]!["errors"]!["confirm"]![0]);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PageAction_FormInputWithNoneOfItsFieldsPosted_ReportsItsRequiredMembers(bool javaScript)
    {
        var calls = new List<string>();
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/notes/page.tsx"],
            map: a => a.MapPageAction("/notes", ([FromForm] NoteInput input) =>
            {
                calls.Add(input is null ? "null" : $"{input.Text}|{input.Pinned}");
                return Page.Redirect("/notes");
            }));
        var token = await TokenAsync(app, "/notes");

        var response = await app.Client.SendAsync(PageActionPost(javaScript, "/notes", token), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("Write something first.", (string?)(await PageOf(javaScript, response, app))["actionState"]!["errors"]!["text"]![0]);
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PageAction_AspNetCoreValidationFailures_RenderThePageAgain(bool javaScript)
    {
        // AddValidation() checks inputs before the action's own filters and writes problem details, which a page cannot show.
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/review/page.tsx"],
            map: a => a.MapPageAction("/review", ([FromForm] SignInput input) =>
            {
                calls++;
                return Page.Redirect("/");
            }),
            services: WithValidation);
        var token = await TokenAsync(app, "/review");

        var response = await app.Client.SendAsync(PageActionPost(javaScript, "/review", token, ("Name", "A"), ("Stars", "3")), Ct);
        var page = await PageOf(javaScript, response, app);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("app/review/page.tsx", (string?)page["page"]);
        Assert.NotNull(page["actionState"]!["errors"]!["name"]);
        Assert.Equal("A", (string?)page["actionState"]!["values"]!["Name"]);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task PageAction_AnInputWhoseDataAnnotationsCannotBeRead_FailsAtStartupNamingThePage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(
            ["app/page.tsx", "app/notes/page.tsx"],
            map: a => a.MapPageAction("/notes", ([FromForm] MisconfiguredInput input) => Page.Redirect("/notes"))));

        Assert.Contains("The action of app/notes/page.tsx (/notes) cannot validate parameter 'input'", ex.Message);
        Assert.Contains("MisconfiguredInput.Code", ex.Message);
    }

    [Fact]
    public async Task AspNetCoreValidationFailures_BecomeInvalidResults_KeyedLikeTheJson()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("sign", ([FromForm] SignInput input) => ServerAction.Ok()),
            services: WithValidation);
        var token = await TokenAsync(app);
        var request = FormPost("sign", token, ("Name", ""), ("Stars", "9"));
        request.Headers.Add(NetjesHeaders.Data, "1");

        var response = await app.Client.SendAsync(request, Ct);
        var json = await JsonOf(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("action", (string?)json["kind"]);
        Assert.Contains("required", (string?)json["errors"]!["name"]![0]);
        Assert.NotNull(json["errors"]!["stars"]);
    }

    [Fact]
    public async Task AspNetCoreValidationFailures_WithoutJavaScript_RenderTheFormPageAgain()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/review/page.tsx"],
            map: a => a.MapAction("sign", ([FromForm] SignInput input) => ServerAction.Ok()),
            services: WithValidation);
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(FormPost("sign", token, ("Name", "A"), ("Stars", "3"), ("__netjes_return", "/review")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("app/review/page.tsx", (string?)app.Renderer.LastPayload["page"]);
        Assert.NotNull(app.Renderer.LastPayload["actionState"]!["errors"]!["name"]);
        Assert.Equal("A", (string?)app.Renderer.LastPayload["actionState"]!["values"]!["Name"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidationFailures_OfOtherEndpoints_KeepTheirProblemDetails(bool problemDetails)
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPost("/api/sign", (SignInput input) => "ok"),
            services: s =>
            {
                WithValidation(s);
                if (problemDetails) s.AddProblemDetails();
            });

        var response = await app.Client.PostAsync("/api/sign", JsonContent.Create(new { name = "", stars = 1 }), Ct);
        var json = await JsonOf(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(json["errors"]!["Name"]);
    }

    [Fact]
    public async Task QueryUpdates_TravelWithTheResult()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("like", () => ServerAction.Ok().Invalidate(["likes"]).WithQuery(["likes", "a"], new { Likes = 4 })));
        var token = await TokenAsync(app);

        var json = await JsonOf(await app.Client.SendAsync(JsonCall("like", new { }, token), Ct));

        Assert.Equal("[[\"likes\"]]", json["invalidate"]!.ToJsonString());
        Assert.Equal(4, (int?)json["queries"]![0]!["data"]!["likes"]);
        Assert.False((bool?)json["revalidate"]);
    }

    [Fact]
    public async Task WithoutJavaScript_Success_RedirectsBackToTheReturnUrl()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/guestbook/page.tsx"], map: a => a.MapAction("sign", ([FromForm] string name) => ServerAction.Ok()));
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(FormPost("sign", token, ("name", "Ada"), ("__netjes_return", "/guestbook?page=2")), Ct);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/guestbook?page=2", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    public async Task WithoutJavaScript_ForeignReturnUrl_GoesHome(string returnUrl)
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], map: a => a.MapAction("sign", () => ServerAction.Ok()));
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(FormPost("sign", token, ("__netjes_return", returnUrl)), Ct);

        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("redirect", "/base/posts")]
    [InlineData("back", "/base/guestbook")]
    [InlineData("foreign", "/base/")]
    public async Task WithoutJavaScript_UnderABasePath_RedirectsStayUnderIt(string outcome, string expected)
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/guestbook/page.tsx"],
            pathBase: "/base",
            map: a => a.MapAction("sign", ([FromForm] string kind) => kind == "redirect" ? ServerAction.Redirect("/posts") : ServerAction.Ok()));
        var token = await TokenAsync(app, "/base/");
        // The client posts the page's URL, base path included (the payload's url).
        var returnUrl = outcome == "foreign" ? "https://evil.example/" : "/base/guestbook";
        var request = FormPost("sign", token, ("kind", outcome), ("__netjes_return", returnUrl));
        request.RequestUri = new Uri("/base/_netjes/actions/sign", UriKind.Relative);

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal(expected, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task WithoutJavaScript_Invalid_RendersTheReturnPageWithTheActionState()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/guestbook/page.tsx"],
            map: a =>
            {
                a.MapPageLoader("/guestbook", () => Page.Props(new { Entries = Array.Empty<string>() }));
                a.MapAction("sign", ([FromForm] string? name, [FromForm] string message) =>
                    ServerAction.Invalid(new Dictionary<string, string[]> { ["name"] = ["Tell us your name."] }));
            });
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(
            FormPost("sign", token, ("name", ""), ("message", "Hi"), ("__netjes_return", "/guestbook")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var payload = app.Renderer.LastPayload;
        Assert.Equal("app/guestbook/page.tsx", (string?)payload["page"]);
        Assert.Equal("/guestbook", (string?)payload["url"]);
        var state = payload["actionState"]!;
        Assert.Equal("sign", (string?)state["action"]);
        Assert.Equal("Tell us your name.", (string?)state["errors"]!["name"]![0]);
        Assert.Equal("Hi", (string?)state["values"]!["message"]);
        Assert.Null(state["values"]![token.Field]);
        Assert.Null(state["values"]!["__netjes_return"]);
    }

    [Fact]
    public async Task WithoutJavaScript_Invalid_ThePageWithWhatWasTypedIsNotStored()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/guestbook/page.tsx"],
            map: a => a.MapAction("sign", ([FromForm] string? name) => ServerAction.Invalid("name", "Tell us your name.")));
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(FormPost("sign", token, ("name", "A"), ("__netjes_return", "/guestbook")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task WithoutJavaScript_Invalid_ThePageIsNotStored_EvenWhenItsLoaderAllowsCaching()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/guestbook/page.tsx"],
            map: a =>
            {
                a.MapPageLoader("/guestbook", (Microsoft.AspNetCore.Http.HttpResponse response) =>
                {
                    response.Headers.CacheControl = "public, max-age=60";
                    return Page.Props();
                });
                a.MapAction("sign", ([FromForm] string? name) => ServerAction.Invalid("name", "Tell us your name."));
            });
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(FormPost("sign", token, ("name", "A"), ("__netjes_return", "/guestbook")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    public static TheoryData<string, string> CdnHeaderCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var source in CdnCacheHeaders.Sources)
        {
            foreach (var casing in CdnCacheHeaders.Casings)
            {
                data.Add(source, casing);
            }
        }

        return data;
    }

    /// <summary>What was typed goes out without the headers a CDN reads ahead of <c>Cache-Control</c> too.</summary>
    [Theory]
    [MemberData(nameof(CdnHeaderCases))]
    public async Task WithoutJavaScript_Invalid_ThePageGoesOutWithoutTheHeadersACdnReadsAheadOfCacheControl(string source, string casing)
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/guestbook/page.tsx"],
            map: a =>
            {
                a.MapPageLoader("/guestbook", (HttpContext http) =>
                {
                    CdnCacheHeaders.FromLoader(http, source, casing);
                    return Page.Props();
                });
                a.MapAction("sign", ([FromForm] string? name) => ServerAction.Invalid("name", "Tell us your name."));
            },
            configureApp: a => CdnCacheHeaders.AfterNetjes(a, source, casing),
            beforeNetjes: CdnCacheHeaders.BeforeNetjes(source, casing));
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(FormPost("sign", token, ("name", "A"), ("__netjes_return", "/guestbook")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Empty(CdnCacheHeaders.In(response));
    }

    public static TheoryData<string, string, bool> PageActionCdnHeaderCases()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var source in CdnCacheHeaders.Sources)
        {
            foreach (var casing in CdnCacheHeaders.Casings)
            {
                data.Add(source, casing, true);
                data.Add(source, casing, false);
            }
        }

        return data;
    }

    /// <summary>
    /// A page action's page, rendered again for an input that failed its checks, holds what was typed as well: posted by
    /// the client router, which gets its payload, or by the browser itself.
    /// </summary>
    [Theory]
    [MemberData(nameof(PageActionCdnHeaderCases))]
    public async Task PageAction_InvalidInput_ThePageGoesOutWithoutTheHeadersACdnReadsAheadOfCacheControl(
        string source, string casing, bool javaScript)
    {
        var calls = new List<string>();
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/guestbook/page.tsx"],
            map: a =>
            {
                a.MapPageLoader("/guestbook", (HttpContext http) =>
                {
                    CdnCacheHeaders.FromLoader(http, source, casing);
                    return Page.Props();
                });
                a.MapPageAction("/guestbook", ([FromForm] GuestbookSignInput input) =>
                {
                    calls.Add($"{input.Name}");
                    return Page.Redirect("/guestbook");
                });
            },
            configureApp: a => CdnCacheHeaders.AfterNetjes(a, source, casing),
            beforeNetjes: CdnCacheHeaders.BeforeNetjes(source, casing));
        var token = await TokenAsync(app, "/guestbook");

        var response = await app.Client.SendAsync(PageActionPost(javaScript, "/guestbook", token, ("name", ""), ("message", "Hi")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(javaScript ? "application/json" : "text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Empty(CdnCacheHeaders.In(response));
        Assert.Empty(calls);
    }

    public static TheoryData<string, string> MiddlewareCdnHeaderCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var source in CdnCacheHeaders.Sources.Where(s => s.StartsWith("middleware", StringComparison.Ordinal)))
        {
            foreach (var casing in CdnCacheHeaders.Casings)
            {
                data.Add(source, casing);
            }
        }

        return data;
    }

    /// <summary>
    /// An answer and a token are one visitor's: a CDN that reads its own header ahead of <c>Cache-Control</c> must not
    /// keep them either, whatever a middleware set for every response.
    /// </summary>
    [Theory]
    [MemberData(nameof(MiddlewareCdnHeaderCases))]
    public async Task AnswersAndTokens_GoOutWithoutTheHeadersACdnReadsAheadOfCacheControl(string source, string casing)
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("noop", () => ServerAction.Ok("done")),
            configureApp: a => CdnCacheHeaders.AfterNetjes(a, source, casing),
            beforeNetjes: CdnCacheHeaders.BeforeNetjes(source, casing));
        var token = await TokenAsync(app);

        var tokenResponse = await app.Client.GetAsync("/_netjes/antiforgery", Ct);
        var answer = await app.Client.SendAsync(JsonCall("noop", new { }, token), Ct);

        foreach (var response in new[] { tokenResponse, answer })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Empty(CdnCacheHeaders.In(response));
        }
    }

    public sealed class GuestbookInput
    {
        public string? Name { get; init; }
        public string? Message { get; init; }
    }

    [Fact]
    public async Task WithoutJavaScript_Invalid_EchoesOnlyTheFieldsTheActionDeclares()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/guestbook/page.tsx"],
            map: a => a.MapAction("sign", ([FromForm] GuestbookInput input, [FromForm] string? mood) => ServerAction.Invalid("name", "Tell us your name.")));
        var token = await TokenAsync(app);

        await app.Client.SendAsync(FormPost("sign", token,
            ("name", ""), ("message", "Hi"), ("mood", "happy"), ("note", new string('x', 512 * 1024)), ("__netjes_return", "/guestbook")), Ct);

        var values = app.Renderer.LastPayload["actionState"]!["values"]!.AsObject();
        Assert.Equal(["name", "message", "mood"], values.Select(v => v.Key));
    }

    [Fact]
    public async Task WithoutJavaScript_Invalid_LeavesOutValuesOver16KB()
    {
        // TaskBoard's task descriptions take 10,000 characters: one that is too long still comes back to be trimmed.
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/guestbook/page.tsx"],
            map: a => a.MapAction("sign", ([FromForm] GuestbookInput input) => ServerAction.Invalid("name", "Tell us your name.")));
        var token = await TokenAsync(app);

        await app.Client.SendAsync(FormPost("sign", token,
            ("name", new string('n', 16 * 1024)), ("message", new string('m', 16 * 1024 + 1)), ("__netjes_return", "/guestbook")), Ct);

        var values = app.Renderer.LastPayload["actionState"]!["values"]!.AsObject();
        Assert.Equal(["name"], values.Select(v => v.Key));
    }

    [Fact]
    public async Task FormCall_Invalid_NamesTheFieldsTheClientMayHandBack()
    {
        // The client keeps what was typed itself (React resets the form), with the same filter as without JavaScript.
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("sign", ([FromForm] GuestbookInput input, [FromForm] string? password) => ServerAction.Invalid("name", "Tell us your name.")));
        var token = await TokenAsync(app);
        var request = FormPost("sign", token, ("name", ""), ("message", "Hi"), ("password", "x"), ("note", "undeclared"), ("__netjes_return", "/"));
        request.Headers.Add(NetjesHeaders.Data, "1");

        var json = await JsonOf(await app.Client.SendAsync(request, Ct));

        Assert.Equal(["name", "message"], json["fields"]?.AsArray().Select(f => (string?)f) ?? []);
    }

    public sealed class ShippingAddress
    {
        public string? Street { get; init; }
    }

    public sealed class OrderLine
    {
        public string? Product { get; init; }
    }

    public sealed class OrderInput
    {
        public string? Email { get; init; }
        public ShippingAddress? Address { get; init; }
        public List<string>? Tags { get; init; }
        public List<OrderLine>? Lines { get; init; }
        public Dictionary<string, string>? Notes { get; init; }
    }

    [Fact]
    public async Task FormCall_Invalid_NamesTheNestedAndIndexedFieldsTheActionDeclares()
    {
        // Form binding reads address.street, tags[0], lines[0].product and notes[gift] into the members they name.
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("order", ([FromForm] OrderInput input) => ServerAction.Invalid("email", "Required.")));
        var token = await TokenAsync(app);
        var request = FormPost("order", token,
            ("email", ""), ("address.street", "Main 1"), ("address.note", "x"), ("address", "x"), ("tags[0]", "gift"), ("tags", "gift"),
            ("lines[0].product", "tea"), ("lines[0].price", "0"), ("notes[gift]", "yes"), ("email.length", "x"), ("__netjes_return", "/"));
        request.Headers.Add(NetjesHeaders.Data, "1");

        var json = await JsonOf(await app.Client.SendAsync(request, Ct));

        Assert.Equal(
            ["email", "address.street", "tags[0]", "tags", "lines[0].product", "notes[gift]"],
            json["fields"]?.AsArray().Select(f => (string?)f) ?? []);
    }

    /// <summary>An order number that implements <see cref="IParsable{TSelf}"/> explicitly, with one constructor.</summary>
    public readonly record struct OrderNumber(int Value) : IParsable<OrderNumber>
    {
        static OrderNumber IParsable<OrderNumber>.Parse(string s, IFormatProvider? provider) => new(int.Parse(s, provider));

        static bool IParsable<OrderNumber>.TryParse(string? s, IFormatProvider? provider, out OrderNumber result)
        {
            var parsed = int.TryParse(s, NumberStyles.Integer, provider, out var value);
            result = new OrderNumber(value);
            return parsed;
        }
    }

    public sealed record NumberedOrderInput
    {
        public OrderNumber Number { get; init; }

        public string? Email { get; init; }
    }

    [Fact]
    public async Task FormCall_Invalid_NamesAFieldReadThroughIParsable()
    {
        // Form binding reads a type that implements IParsable<T> from one field, as it reads a number.
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("order", ([FromForm] NumberedOrderInput input) => ServerAction.Invalid("email", "Required.")));
        var token = await TokenAsync(app);
        var request = FormPost("order", token, ("number", "7"), ("email", ""), ("__netjes_return", "/"));
        request.Headers.Add(NetjesHeaders.Data, "1");

        var json = await JsonOf(await app.Client.SendAsync(request, Ct));

        Assert.Equal(["number", "email"], json["fields"]?.AsArray().Select(f => (string?)f) ?? []);
    }

    [Fact]
    public async Task FormCall_Invalid_NamesNoMembersOfAListOrDictionaryItself()
    {
        // A list or dictionary parameter binds from [0].product or [gift]; Count, Capacity or Keys are no fields.
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a =>
            {
                a.MapAction("lines", ([FromForm] List<OrderLine> lines) => ServerAction.Invalid("lines", "Required."));
                a.MapAction("notes", ([FromForm] Dictionary<string, string> notes) => ServerAction.Invalid("notes", "Required."));
            });
        var token = await TokenAsync(app);

        var fields = new List<string?>();
        foreach (var (action, field) in new[] { ("lines", "[0].product"), ("notes", "[gift]") })
        {
            var request = FormPost(action, token, (field, "x"), ("count", "1"), ("capacity", "1"), ("keys", "x"), ("comparer", "x"));
            request.Headers.Add(NetjesHeaders.Data, "1");
            fields.AddRange((await JsonOf(await app.Client.SendAsync(request, Ct)))["fields"]?.AsArray().Select(f => (string?)f) ?? []);
        }

        Assert.Equal(["[0].product", "[gift]"], fields);
    }

    public sealed class StoryInput
    {
        public string? Part1 { get; init; }
        public string? Part2 { get; init; }
        public string? Part3 { get; init; }
        public string? Part4 { get; init; }
        public string? Part5 { get; init; }
        public string? Part6 { get; init; }
        public string? Part7 { get; init; }
        public string? Part8 { get; init; }
        public string? Part9 { get; init; }
    }

    [Fact]
    public async Task WithoutJavaScript_Invalid_EchoesAt64KBInAll()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/story/page.tsx"],
            map: a => a.MapAction("story", ([FromForm] StoryInput input) => ServerAction.Invalid("part1", "Too long.")));
        var token = await TokenAsync(app);
        // 8 KB each, name and value together: eight fit.
        var parts = Enumerable.Range(1, 9).Select(i => ($"part{i}", new string('x', 8 * 1024 - $"part{i}".Length)));

        await app.Client.SendAsync(FormPost("story", token, [.. parts, ("__netjes_return", "/story")]), Ct);

        var values = app.Renderer.LastPayload["actionState"]!["values"]!.AsObject();
        Assert.Equal([.. Enumerable.Range(1, 8).Select(i => $"part{i}")], values.Select(v => v.Key));
    }

    /// <summary>
    /// Field names a dictionary binds, which the post chooses: 900 of 1,011 characters with empty values, under the
    /// 1 MB body limit.
    /// </summary>
    private static IEnumerable<(string Name, string Value)> LongNotes() =>
        Enumerable.Range(0, 900).Select(i => ($"notes[{i:D4}{new string('k', 1000)}]", ""));

    [Fact]
    public async Task WithoutJavaScript_Invalid_CountsTheFieldNamesInThe64KB()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/order/page.tsx"],
            map: a => a.MapAction("order", ([FromForm] OrderInput input) => ServerAction.Invalid("email", "Required.")));
        var token = await TokenAsync(app);

        await app.Client.SendAsync(FormPost("order", token, [.. LongNotes(), ("__netjes_return", "/order")]), Ct);

        var values = app.Renderer.LastPayload["actionState"]!["values"]!.AsObject();
        Assert.Equal(64, values.Count);
        Assert.InRange(values.Sum(v => v.Key.Length + ((string?)v.Value)!.Length), 1, 64 * 1024);
    }

    [Fact]
    public async Task FormCall_Invalid_NamesFieldsUpTo64KB()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("order", ([FromForm] OrderInput input) => ServerAction.Invalid("email", "Required.")));
        var token = await TokenAsync(app);
        var request = FormPost("order", token, [.. LongNotes(), ("__netjes_return", "/")]);
        request.Headers.Add(NetjesHeaders.Data, "1");

        var json = await JsonOf(await app.Client.SendAsync(request, Ct));

        var fields = json["fields"]?.AsArray().Select(f => (string)f!).ToList() ?? [];
        Assert.Equal(64, fields.Count);
        Assert.InRange(fields.Sum(f => f.Length), 1, 64 * 1024);
    }

    public sealed class ProfileForm
    {
        public string? FirstName { get; init; }
        [JsonPropertyName("nick")] public string? Nickname { get; init; }
    }

    [Fact]
    public async Task WithoutJavaScript_Invalid_EchoesDeclaredFieldsByTheNamesTheFormUses()
    {
        // Generated form types name fields with the JSON naming policy and [JsonPropertyName].
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/profile/page.tsx"],
            map: a => a.MapAction("profile", ([FromForm] ProfileForm input, [FromForm(Name = "mood_today")] string? mood) =>
                ServerAction.Invalid("first_name", "Too short.")),
            services: s => s.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower));
        var page = await app.GetDataAsync("/");
        var antiforgery = (await JsonOf(page))["antiforgery"]!;
        var token = new Token(
            string.Join("; ", page.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])),
            (string)antiforgery["form_field_name"]!,
            (string)antiforgery["header_name"]!,
            (string)antiforgery["request_token"]!);

        await app.Client.SendAsync(FormPost("profile", token,
            ("first_name", "A"), ("nick", "ada"), ("mood_today", "happy"), ("mood", "sad"), ("__netjes_return", "/profile")), Ct);

        var values = app.Renderer.LastPayload["action_state"]!["values"]!.AsObject();
        Assert.Equal(["first_name", "nick", "mood_today"], values.Select(v => v.Key));
    }

    public sealed class SignParameters
    {
        [FromForm] public string? Name { get; init; }
        public string? Mood { get; init; }
    }

    [Fact]
    public async Task WithoutJavaScript_Invalid_EchoesTheFormFieldsOfAsParameters()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/guestbook/page.tsx"],
            map: a => a.MapAction("sign", ([Microsoft.AspNetCore.Http.AsParameters] SignParameters input) => ServerAction.Invalid("name", "Tell us your name.")));
        var token = await TokenAsync(app);

        await app.Client.SendAsync(FormPost("sign", token, ("name", "A"), ("mood", "happy"), ("__netjes_return", "/guestbook")), Ct);

        var values = app.Renderer.LastPayload["actionState"]!["values"]!.AsObject();
        Assert.Equal(["name"], values.Select(v => v.Key));
    }

    [Fact]
    public async Task Exception_InACall_IsA500ActionResponse()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"], map: a => a.MapAction("boom", Func<ServerActionResult> () => throw new InvalidOperationException("db down")));
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(JsonCall("boom", new { }, token), Ct);
        var json = await JsonOf(response);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False((bool?)json["ok"]);
        Assert.Equal(500, (int?)json["status"]);
        Assert.Equal("db down", (string?)json["message"]);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("")]
    [InlineData("a/b")]
    public async Task InvalidName_FailsAtStartup(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => TestApp.StartAsync(["app/page.tsx"], map: a => a.MapAction(name, () => ServerAction.Ok())));
    }

    [Fact]
    public async Task DuplicateName_FailsAtStartup()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(
            ["app/page.tsx"],
            map: a =>
            {
                a.MapAction("same", () => ServerAction.Ok());
                a.MapAction("same", () => ServerAction.Ok());
            }));

        Assert.Contains("same", ex.Message);
    }

    public sealed record TagInput
    {
        public string[] Tags { get; init; } = [];
    }

    [Fact]
    public async Task FormCall_AStringArrayProperty_BindsRepeatedAndIndexedFields()
    {
        // A checkbox group posts several fields with one name; the property takes them all, and keeps its default when none is posted.
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"], map: a => a.MapAction("tags", ([FromForm] TagInput input) => ServerAction.Ok($"[{string.Join(",", input.Tags)}]")));
        var token = await TokenAsync(app);

        var repeated = FormPost("tags", token, ("Tags", "a"), ("Tags", "b"));
        repeated.Headers.Add(NetjesHeaders.Data, "1");
        var indexed = FormPost("tags", token, ("Tags[0]", "c"), ("Tags[1]", "d"));
        indexed.Headers.Add(NetjesHeaders.Data, "1");
        var none = FormPost("tags", token, ("other", "x"));
        none.Headers.Add(NetjesHeaders.Data, "1");

        Assert.Equal("[a,b]", (string?)(await JsonOf(await app.Client.SendAsync(repeated, Ct)))["result"]);
        Assert.Equal("[c,d]", (string?)(await JsonOf(await app.Client.SendAsync(indexed, Ct)))["result"]);
        Assert.Equal("[]", (string?)(await JsonOf(await app.Client.SendAsync(none, Ct)))["result"]);
    }

    public static TheoryData<string, Action<WebApplication>, string> ActionsMinimalApisRefuse => new()
    {
        { "two inferred bodies", a => a.MapAction("posts.save", (GreetInput one, GreetInput two) => ServerAction.Ok()), "Failure to infer" },
        { "a form and a JSON body", a => a.MapAction("posts.save", ([FromForm] string title, GreetInput input) => ServerAction.Ok()), "both form and JSON body" },
        { "a top-level [FromForm] string[]", a => a.MapAction("posts.save", ([FromForm] string[] tags) => ServerAction.Ok()), "TryParse method found on string with incorrect format" },
        { "[FromRoute] naming no route parameter", a => a.MapAction("posts.save", ([FromRoute] string id) => ServerAction.Ok()), "'id' is not a route parameter" },
    };

    [Theory]
    [MemberData(nameof(ActionsMinimalApisRefuse))]
    public async Task AParameterMinimalApisRefuse_FailsAtStartupNamingTheActionAndWhy(string name, Action<WebApplication> map, string why)
    {
        // Refused when the endpoints were first built, which failed every request of the app.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(["app/page.tsx"], map: map));

        Assert.Contains("The server action 'posts.save' cannot bind its parameters", ex.Message);
        Assert.True(ex.Message.Contains(why, StringComparison.Ordinal), name);
    }

    public sealed class TwoConstructorsInput
    {
        public TwoConstructorsInput(string name) => Name = name;

        public TwoConstructorsInput(string name, int age) => (Name, Age) = (name, age);

        public string Name { get; }

        public int Age { get; }
    }

    public sealed class ParameterlessAndAnotherInput
    {
        public ParameterlessAndAnotherInput()
        {
        }

        public ParameterlessAndAnotherInput(string name) => Name = name;

        public string? Name { get; init; }
    }

    public sealed record PositionalWithParameterlessInput(string Name)
    {
        public PositionalWithParameterlessInput() : this("")
        {
        }
    }

    public sealed class NoPublicConstructorInput
    {
        private NoPublicConstructorInput()
        {
        }

        public string? Name { get; init; }
    }

    public sealed class TwoConstructorsAddress
    {
        public TwoConstructorsAddress()
        {
        }

        public TwoConstructorsAddress(string street) => Street = street;

        public string? Street { get; init; }
    }

    public sealed record AddressedInput
    {
        public string? Name { get; init; }

        public TwoConstructorsAddress? Address { get; init; }
    }

    public sealed record OrderLinesInput
    {
        public List<TwoConstructorsAddress> Lines { get; init; } = [];
    }

    public readonly struct TwoConstructorsPoint
    {
        public TwoConstructorsPoint(int x) => X = x;

        public TwoConstructorsPoint(int x, int y) => (X, Y) = (x, y);

        public int X { get; init; }

        public int Y { get; init; }
    }

    public interface IFormShape
    {
        string? Kind { get; }
    }

    public abstract class FormFigure
    {
        public string? Kind { get; init; }
    }

    public sealed record ShapedInput
    {
        public string? Name { get; init; }

        public IFormShape? Shape { get; init; }
    }

    public sealed record FiguredInput
    {
        public string? Name { get; init; }

        public FormFigure? Figure { get; init; }
    }

    private const string ConcreteType = "a class, struct or record type with one public constructor.";

    private const string OneConstructor = "one public constructor (a parameterless one with init properties, or a positional record's)";

    public static TheoryData<string, Action<WebApplication>, string, string, string> FormInputsFormBindingCannotBuild => new()
    {
        { "two public constructors", a => a.MapAction("posts.save", ([FromForm] TwoConstructorsInput input) => ServerAction.Ok()), "TwoConstructorsInput", "has 2", $"Give TwoConstructorsInput {OneConstructor}." },
        { "a parameterless constructor and another", a => a.MapAction("posts.save", ([FromForm] ParameterlessAndAnotherInput input) => ServerAction.Ok()), "ParameterlessAndAnotherInput", "has 2", $"Give ParameterlessAndAnotherInput {OneConstructor}." },
        { "a positional record with a parameterless constructor", a => a.MapAction("posts.save", ([FromForm] PositionalWithParameterlessInput input) => ServerAction.Ok()), "PositionalWithParameterlessInput", "has 2", $"Give PositionalWithParameterlessInput {OneConstructor}." },
        { "no public constructor", a => a.MapAction("posts.save", ([FromForm] NoPublicConstructorInput input) => ServerAction.Ok()), "NoPublicConstructorInput", "has none", $"Give NoPublicConstructorInput {OneConstructor}." },
        { "a nullable input", a => a.MapAction("posts.save", ([FromForm] TwoConstructorsInput? input) => ServerAction.Ok()), "TwoConstructorsInput", "has 2", $"Give TwoConstructorsInput {OneConstructor}." },
        { "a struct", a => a.MapAction("posts.save", ([FromForm] TwoConstructorsPoint input) => ServerAction.Ok()), "TwoConstructorsPoint", "has 2", $"Give TwoConstructorsPoint {OneConstructor}." },
        { "a property's type", a => a.MapAction("posts.save", ([FromForm] AddressedInput input) => ServerAction.Ok()), "TwoConstructorsAddress (AddressedInput.Address)", "has 2", $"Give TwoConstructorsAddress {OneConstructor}, or give AddressedInput.Address a type form binding can build." },
        { "a list's items", a => a.MapAction("posts.save", ([FromForm] OrderLinesInput input) => ServerAction.Ok()), "TwoConstructorsAddress (OrderLinesInput.Lines)", "has 2", $"Give TwoConstructorsAddress {OneConstructor}, or give OrderLinesInput.Lines a type form binding can build." },
        { "an app's type in a namespace that starts with System", a => a.MapAction("posts.save", ([FromForm] SystemsShop.Forms.TwoConstructorsOrder input) => ServerAction.Ok()), "TwoConstructorsOrder", "has 2", $"Give TwoConstructorsOrder {OneConstructor}." },
        { "an abstract input", a => a.MapAction("posts.save", ([FromForm] FormFigure input) => ServerAction.Ok()), "FormFigure", "is abstract", $"Give parameter 'input' {ConcreteType}" },
        { "an abstract property's type", a => a.MapAction("posts.save", ([FromForm] FiguredInput input) => ServerAction.Ok()), "FormFigure (FiguredInput.Figure)", "is abstract", $"Give FiguredInput.Figure {ConcreteType}" },
        { "an interface property's type", a => a.MapAction("posts.save", ([FromForm] ShapedInput input) => ServerAction.Ok()), "IFormShape (ShapedInput.Shape)", "is an interface", $"Give ShapedInput.Shape {ConcreteType}" },
    };

    [Theory]
    [MemberData(nameof(FormInputsFormBindingCannotBuild))]
    public async Task AFormInputFormBindingCannotBuild_FailsAtStartupNamingTheActionTheTypeAndTheFix(
        string name, Action<WebApplication> map, string type, string problem, string fix)
    {
        // Form binding builds a type only through its one public constructor; for any other it binds null, silently.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(["app/page.tsx"], map: map));

        Assert.Contains("The server action 'posts.save' cannot bind parameter 'input' from a form", ex.Message);
        Assert.True(ex.Message.Contains(type, StringComparison.Ordinal), $"{name}: {ex.Message}");
        Assert.Contains("form binding builds a class, struct or record only through its one public constructor", ex.Message);
        Assert.True(ex.Message.Contains($"{type} {problem}, so a post would never fill it.", StringComparison.Ordinal), $"{name}: {ex.Message}");
        Assert.True(ex.Message.EndsWith(fix, StringComparison.Ordinal), $"{name}: {ex.Message}");
    }

    public sealed class IgnoredAddressInput
    {
        public string? Name { get; init; }

        [IgnoreDataMember]
        public TwoConstructorsAddress? Address { get; set; }
    }

    public sealed class HiddenAddressInput
    {
        public string? Name { get; init; }

        public TwoConstructorsAddress? Address { private get; set; }
    }

    /// <summary>A strongly-typed id that implements <see cref="IParsable{TSelf}"/> explicitly: form binding reads it from one field.</summary>
    public readonly struct ExplicitlyParsedId : IParsable<ExplicitlyParsedId>
    {
        public ExplicitlyParsedId(int value) => Value = value;

        public ExplicitlyParsedId(string value) => Value = int.Parse(value, CultureInfo.InvariantCulture);

        public int Value { get; }

        public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

        static ExplicitlyParsedId IParsable<ExplicitlyParsedId>.Parse(string s, IFormatProvider? provider) => new(s);

        static bool IParsable<ExplicitlyParsedId>.TryParse(string? s, IFormatProvider? provider, out ExplicitlyParsedId result)
        {
            var parsed = int.TryParse(s, NumberStyles.Integer, provider, out var value);
            result = new ExplicitlyParsedId(value);
            return parsed;
        }
    }

    public sealed record IdentifiedInput
    {
        public ExplicitlyParsedId Id { get; init; }

        public string? Name { get; init; }
    }

    public sealed record LinkInput
    {
        public Uri? Site { get; init; }
    }

    public static TheoryData<string, Action<WebApplication>, string> FormInputsFormBindingBuilds => new()
    {
        { "a property form binding ignores", a => a.MapAction("probe", ([FromForm] IgnoredAddressInput input) => ServerAction.Ok(input.Name)), "Ada" },
        { "a property without a public getter", a => a.MapAction("probe", ([FromForm] HiddenAddressInput input) => ServerAction.Ok(input.Name)), "Ada" },
        { "a property read through IParsable<T>", a => a.MapAction("probe", ([FromForm] IdentifiedInput input) => ServerAction.Ok($"{input.Id}")), "5" },
        { "a parameter read through IParsable<T>", a => a.MapAction("probe", ([FromForm] ExplicitlyParsedId id) => ServerAction.Ok($"{id}")), "5" },
        { "a property of a .NET type with several constructors", a => a.MapAction("probe", ([FromForm] LinkInput input) => ServerAction.Ok($"{input.Site}")), "https://example.com/" },
    };

    [Theory]
    [MemberData(nameof(FormInputsFormBindingBuilds))]
    public async Task AFormInputFormBindingCanBuild_StartsAndBinds(string name, Action<WebApplication> map, string result)
    {
        // Form binding fills only what it maps: a member it skips can have any type.
        await using var app = await TestApp.StartAsync(["app/page.tsx"], map: map);
        var token = await TokenAsync(app);
        var request = FormPost("probe", token, ("Name", "Ada"), ("Address.Street", "Main 1"), ("Id", "5"), ("Site", "https://example.com/"));
        request.Headers.Add(NetjesHeaders.Data, "1");

        var json = await JsonOf(await app.Client.SendAsync(request, Ct));

        Assert.True(result == (string?)json["result"], $"{name}: {json.ToJsonString()}");
    }

    [Fact]
    public async Task APageActionsFormInputFormBindingCannotBuild_FailsAtStartupNamingThePage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(
            ["app/page.tsx", "app/notes/page.tsx"],
            map: a => a.MapPageAction("/notes", ([FromForm] TwoConstructorsInput input) => Page.Redirect("/notes"))));

        Assert.Contains("The action of app/notes/page.tsx (/notes) cannot bind parameter 'input' from a form", ex.Message);
        Assert.Contains("TwoConstructorsInput", ex.Message);
    }

    public sealed class Labels
    {
        public static string Title => "Title";
    }

    public sealed class UnlocalizedInput
    {
        [Display(Name = "Heading", ResourceType = typeof(Labels))]
        public string Title { get; init; } = "";
    }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
    public sealed class MisconfiguredAttribute : ValidationAttribute
    {
        public MisconfiguredAttribute() => throw new InvalidOperationException("The pattern is missing.");
    }

    public sealed class MisconfiguredInput
    {
        [Misconfigured]
        public string Code { get; init; } = "";
    }

    public sealed record MisconfiguredRecord([Misconfigured] string Code);

    [JsonDerivedType(typeof(MisconfiguredCircle), "circle")]
    public class Shape
    {
        [Required]
        public string? Name { get; init; }
    }

    public sealed class MisconfiguredCircle : Shape
    {
        [Misconfigured]
        public string? Radius { get; init; }
    }

    [JsonDerivedType(typeof(MisconfiguredSquare), "square")]
    public abstract class AbstractShape;

    public sealed class MisconfiguredSquare : AbstractShape
    {
        [Misconfigured]
        public string? Side { get; init; }
    }

    public static TheoryData<string, Action<WebApplication>, string, string> InputsThatCannotBeValidated => new()
    {
        { "a [Display] whose resource has no such name", a => a.MapAction("posts.save", (UnlocalizedInput input) => ServerAction.Ok()), "UnlocalizedInput.Title", "localization failed" },
        { "a validation attribute that throws", a => a.MapAction("posts.save", (MisconfiguredInput input) => ServerAction.Ok()), "MisconfiguredInput.Code", "The pattern is missing." },
        { "one on a record's constructor parameter", a => a.MapAction("posts.save", (MisconfiguredRecord input) => ServerAction.Ok()), "MisconfiguredRecord.Code", "The pattern is missing." },
        { "one on a derived type the input lists", a => a.MapAction("posts.save", (Shape input) => ServerAction.Ok()), "MisconfiguredCircle.Radius", "The pattern is missing." },
        { "one on a derived type an abstract input lists", a => a.MapAction("posts.save", (AbstractShape input) => ServerAction.Ok()), "MisconfiguredSquare.Side", "The pattern is missing." },
    };

    [Theory]
    [MemberData(nameof(InputsThatCannotBeValidated))]
    public async Task AnInputWhoseDataAnnotationsCannotBeRead_FailsAtStartupNamingTheActionAndWhy(
        string name, Action<WebApplication> map, string property, string why)
    {
        // Read when the endpoints were first built, which failed every request of the app.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(["app/page.tsx"], map: map));

        Assert.Contains("The server action 'posts.save' cannot validate parameter 'input'", ex.Message);
        Assert.True(ex.Message.Contains(property, StringComparison.Ordinal), name);
        Assert.True(ex.Message.Contains(why, StringComparison.Ordinal), name);
    }
}
