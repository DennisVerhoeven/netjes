using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Netjes.Tests.Endpoints;

/// <summary>A failed no-JavaScript form post renders its return page in the same request, and only a page.</summary>
public class ActionReExecuteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Token(string Cookie, string Field, string Value);

    private static async Task<Token> TokenAsync(TestApp app, string url = "/")
    {
        var page = await app.GetDataAsync(url);
        var antiforgery = JsonNode.Parse(await page.Content.ReadAsStringAsync(Ct))!["antiforgery"]!;
        return new Token(
            string.Join("; ", page.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])),
            (string)antiforgery["formFieldName"]!,
            (string)antiforgery["requestToken"]!);
    }

    private static HttpRequestMessage NoJsPost(string action, Token token, params (string Name, string Value)[] fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/_netjes/actions/{action}")
        {
            Content = new FormUrlEncodedContent([.. fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)), new(token.Field, token.Value)]),
        };
        request.Headers.Add("Cookie", token.Cookie);
        return request;
    }

    /// <summary>Middleware before <c>UseNetjes</c> that refuses a path, like an IP allow-list.</summary>
    private sealed class Guard(string path) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, inner) =>
            {
                if (http.Request.Path.StartsWithSegments(path))
                {
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }

                return inner(http);
            });
            next(app);
        };
    }

    private static void Guarded(IServiceCollection services, string path) => services.AddTransient<IStartupFilter>(_ => new Guard(path));

    [Fact]
    public async Task ReturnPathOfAnotherEndpoint_RendersTheHomePageInstead()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx"],
            map: a =>
            {
                a.MapGet("/internal/status", () => "internal status");
                a.MapAction("sign", ([FromForm] string? name) => ServerAction.Invalid("name", "Tell us your name."));
            },
            services: s => Guarded(s, "/internal"));
        var token = await TokenAsync(app);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Client.GetAsync("/internal/status", Ct)).StatusCode);

        var response = await app.Client.SendAsync(NoJsPost("sign", token, ("name", ""), ("__netjes_return", "/internal/status")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.DoesNotContain("internal status", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("app/page.tsx", (string?)app.Renderer.LastPayload["page"]);
        Assert.Equal("sign", (string?)app.Renderer.LastPayload["actionState"]!["action"]);
    }

    [Fact]
    public async Task ReturnPathElsewhere_WhenTheHomeIsNoPage_RendersTheErrorPageNotTheHomeEndpoint()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/form/page.tsx", "app/error.tsx"],
            map: a =>
            {
                a.MapGet("/", () => "root endpoint");
                a.MapAction("sign", ([FromForm] string? name) => ServerAction.Invalid("name", "Tell us your name."));
            });
        var token = await TokenAsync(app, "/form");

        var response = await app.Client.SendAsync(NoJsPost("sign", token, ("name", ""), ("__netjes_return", "/other")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.DoesNotContain("root endpoint", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("sign", (string?)app.Renderer.LastPayload["actionState"]!["action"]);
    }

    [Fact]
    public async Task ReturnPathOfAShortCircuitEndpoint_RendersTheHomePageInstead()
    {
        // Routing runs a short-circuit endpoint itself, before anything after it could object.
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx"],
            map: a =>
            {
                a.MapGet("/internal/{check}", (string check) => $"internal {check}").ShortCircuit();
                a.MapAction("sign", ([FromForm] string? name) => ServerAction.Invalid("name", "Tell us your name."));
            },
            services: s => Guarded(s, "/internal"));
        var token = await TokenAsync(app);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Client.GetAsync("/internal/status", Ct)).StatusCode);

        var response = await app.Client.SendAsync(NoJsPost("sign", token, ("name", ""), ("__netjes_return", "/internal/status?x=1")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.DoesNotContain("internal status", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("app/page.tsx", (string?)app.Renderer.LastPayload["page"]);
    }

    [Fact]
    public async Task ReturnPathOfAStaticFile_RendersTheHomePageInstead()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx"],
            map: a => a.MapAction("sign", ([FromForm] string? name) => ServerAction.Invalid("name", "Tell us your name.")),
            services: s => Guarded(s, "/reports"),
            prepareRoot: root =>
            {
                Directory.CreateDirectory(Path.Combine(root, "public", "reports"));
                File.WriteAllText(Path.Combine(root, "public", "reports", "q3.txt"), "quarterly numbers");
            });
        var token = await TokenAsync(app);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Client.GetAsync("/reports/q3.txt", Ct)).StatusCode);

        var response = await app.Client.SendAsync(NoJsPost("sign", token, ("name", ""), ("__netjes_return", "/reports/q3.txt")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.DoesNotContain("quarterly numbers", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("app/page.tsx", (string?)app.Renderer.LastPayload["page"]);
    }
}
