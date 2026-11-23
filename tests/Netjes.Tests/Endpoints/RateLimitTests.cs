using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace Netjes.Tests.Endpoints;

/// <summary>
/// <c>Page.TooManyRequests</c> as the rate limiter's <c>OnRejected</c>: a refused form gets its page again with status 429,
/// a message for the form and the fields it posted, never a secret, and only once its post passed the checks an action's
/// post passes.
/// </summary>
public class RateLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Refuse = "refuse";

    private sealed record Token(string Cookie, string Field, string Header, string Value);

    public sealed record SignInput
    {
        [Required(ErrorMessage = "Tell us who you are.")]
        public string? Name { get; init; }

        public string? Message { get; init; }
    }

    /// <summary>A limiter that refuses every request, saying when to come back: no clock, no counting.</summary>
    private sealed class RefusingLimiter(TimeSpan? retryAfter) : RateLimiter
    {
        public override TimeSpan? IdleDuration => null;

        public override RateLimiterStatistics? GetStatistics() => null;

        protected override RateLimitLease AttemptAcquireCore(int permitCount) => new Refused(retryAfter);

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
            ValueTask.FromResult<RateLimitLease>(new Refused(retryAfter));

        private sealed class Refused(TimeSpan? retryAfter) : RateLimitLease
        {
            public override bool IsAcquired => false;

            public override IEnumerable<string> MetadataNames => retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

            public override bool TryGetMetadata(string metadataName, out object? metadata)
            {
                metadata = metadataName == MetadataName.RetryAfter.Name ? retryAfter : null;
                return metadata is not null;
            }
        }
    }

    private static Task<TestApp> StartAsync(
        List<string> calls, Func<OnRejectedContext, ValueTask>? onRejected = null, bool sayWhen = true, bool limitThePage = false) =>
        TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/guestbook/page.tsx", "app/error.tsx"],
            map: a =>
            {
                var loader = a.MapPageLoader("/guestbook", () => Page.Props(new { Entries = new[] { "First!" } }));
                if (limitThePage)
                {
                    loader.RequireRateLimiting(Refuse);
                }

                a.MapPageAction("/guestbook", ([FromForm] SignInput input, [FromForm] string? mood, [FromForm] string? newPassword) =>
                {
                    calls.Add($"page {input.Name}");
                    return Page.Redirect("/guestbook");
                }).RequireRateLimiting(Refuse);
                a.MapAction("guestbook.sign", ([FromForm] SignInput input, [FromForm] string? newPassword) =>
                {
                    calls.Add($"action {input.Name}");
                    return ServerAction.Ok();
                }).RequireRateLimiting(Refuse);
            },
            services: s => s.AddRateLimiter(limiter =>
            {
                limiter.OnRejected = (context, _) => onRejected is null ? Page.TooManyRequests(context) : onRejected(context);
                limiter.AddPolicy(Refuse, _ => RateLimitPartition.Get("all", _ => new RefusingLimiter(sayWhen ? TimeSpan.FromSeconds(29.2) : null)));
            }),
            configureApp: a => a.UseRateLimiter());

    private static async Task<Token> TokenAsync(TestApp app)
    {
        var response = await app.GetDataAsync("/");
        var antiforgery = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["antiforgery"]!;
        return new Token(
            string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])),
            (string)antiforgery["formFieldName"]!,
            (string)antiforgery["headerName"]!,
            (string)antiforgery["requestToken"]!);
    }

    private static HttpRequestMessage Post(bool javaScript, string url, Token token, params (string Name, string Value)[] fields)
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

    private static async Task<JsonNode> JsonOf(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

    private static async Task<JsonNode> PageOf(bool javaScript, HttpResponseMessage response, TestApp app) =>
        javaScript ? await JsonOf(response) : app.Renderer.LastPayload;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefusedPageAction_RendersThePageAgain_WithStatus429_AndTheFieldsItPosted(bool javaScript)
    {
        var calls = new List<string>();
        await using var app = await StartAsync(calls);
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(Post(javaScript, "/guestbook?from=home", token,
            ("name", "Ada"), ("message", "Hi"), ("mood", "happy"), ("newPassword", "s3cret"), ("note", "undeclared")), Ct);
        var page = await PageOf(javaScript, response, app);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(javaScript ? "application/json" : "text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("30", response.Headers.GetValues("Retry-After").Single());
        Assert.Empty(calls);
        Assert.Equal(429, (int?)page["status"]);
        Assert.Equal("app/guestbook/page.tsx", (string?)page["page"]);
        Assert.Equal("/guestbook?from=home", (string?)page["url"]);
        Assert.Equal("First!", (string?)page["props"]!["entries"]![0]);
        var state = page["actionState"]!;
        Assert.Equal("/guestbook", (string?)state["action"]);
        Assert.Equal(429, (int?)state["status"]);
        Assert.Equal("Too many requests. Try again later.", (string?)state["errors"]![""]![0]);
        Assert.Equal(["name", "message", "mood"], state["values"]!.AsObject().Select(v => v.Key));
        Assert.Equal("Ada", (string?)state["values"]!["name"]);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("s3cret", body);
        Assert.DoesNotContain(app.Renderer.Payloads, p => p.Contains("s3cret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefusedPageAction_WithAMessage_PutsItUnderTheForm(bool javaScript)
    {
        var calls = new List<string>();
        await using var app = await StartAsync(calls, onRejected: context => Page.TooManyRequests(context, "Slow down, " + context.HttpContext.Request.Path));
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(Post(javaScript, "/guestbook", token, ("name", "Ada")), Ct);
        var page = await PageOf(javaScript, response, app);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(["Slow down, /guestbook"], page["actionState"]!["errors"]![""]!.AsArray().Select(m => (string?)m));
    }

    /// <summary>
    /// An empty 429, which Netjes answers with the error page: no form state, and nothing of what was posted in a payload
    /// or in the body.
    /// </summary>
    private static async Task AssertHandsNothingBack(bool javaScript, HttpResponseMessage response, TestApp app)
    {
        var page = await PageOf(javaScript, response, app);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(429, (int?)page["status"]);
        Assert.Equal("app/error.tsx", (string?)page["page"]);
        Assert.Null(page["actionState"]);
        Assert.DoesNotContain(app.Renderer.Payloads, p => p.Contains("actionState", StringComparison.Ordinal) || p.Contains("Ada", StringComparison.Ordinal));
        Assert.DoesNotContain("Ada", await response.Content.ReadAsStringAsync(Ct));
    }

    // A server action's form posted without JavaScript takes its own way back, to the page at its return URL.
    [Theory]
    [InlineData(true, "/guestbook")]
    [InlineData(false, "/guestbook")]
    [InlineData(false, "/_netjes/actions/guestbook.sign")]
    public async Task RefusedPost_WithABadToken_HandsNothingBack(bool javaScript, string url)
    {
        // Nothing of a post is written back into a page before its token has been checked, as for a failed validation.
        var calls = new List<string>();
        await using var app = await StartAsync(calls);
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(
            Post(javaScript, url, token with { Value = "forged" }, ("name", "<b>Ada</b>"), ("__netjes_return", "/guestbook")), Ct);

        await AssertHandsNothingBack(javaScript, response, app);
        Assert.Equal("30", response.Headers.GetValues("Retry-After").Single());
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData(true, "/guestbook")]
    [InlineData(false, "/guestbook")]
    [InlineData(false, "/_netjes/actions/guestbook.sign")]
    public async Task RefusedPost_WithoutAnyToken_HandsNothingBack(bool javaScript, string url)
    {
        var calls = new List<string>();
        await using var app = await StartAsync(calls);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent([new("name", "<b>Ada</b>"), new("__netjes_return", "/guestbook")]),
        };
        if (javaScript)
        {
            request.Headers.Add(NetjesHeaders.Data, "1");
        }

        var response = await app.Client.SendAsync(request, Ct);

        await AssertHandsNothingBack(javaScript, response, app);
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData(true, "/guestbook", "Sec-Fetch-Site", "cross-site")]
    [InlineData(false, "/guestbook", "Sec-Fetch-Site", "cross-site")]
    [InlineData(false, "/guestbook", "Origin", "https://elsewhere.example")]
    [InlineData(false, "/guestbook", "Origin", "null")]
    [InlineData(true, "/_netjes/actions/guestbook.sign", "Origin", "https://elsewhere.example")]
    [InlineData(false, "/_netjes/actions/guestbook.sign", "Sec-Fetch-Site", "cross-site")]
    public async Task RefusedPost_FromAnotherSite_HandsNothingBack(bool javaScript, string url, string header, string value)
    {
        var calls = new List<string>();
        await using var app = await StartAsync(calls);
        var token = await TokenAsync(app);
        var request = Post(javaScript, url, token, ("name", "<b>Ada</b>"), ("__netjes_return", "/guestbook"));
        request.Headers.Add(header, value);

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Empty(calls);
        Assert.DoesNotContain(app.Renderer.Payloads, p => p.Contains("actionState", StringComparison.Ordinal) || p.Contains("Ada", StringComparison.Ordinal));
        Assert.DoesNotContain("Ada", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task RefusedServerActionCall_FailsWithStatus429AndTheMessage()
    {
        var calls = new List<string>();
        await using var app = await StartAsync(calls);
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(Post(javaScript: true, "/_netjes/actions/guestbook.sign", token, ("name", "Ada"), ("newPassword", "s3cret")), Ct);
        var answer = await JsonOf(response);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("30", response.Headers.GetValues("Retry-After").Single());
        Assert.Empty(calls);
        Assert.Equal("action", (string?)answer["kind"]);
        Assert.False((bool)answer["ok"]!);
        Assert.Equal(429, (int?)answer["status"]);
        Assert.Equal("Too many requests. Try again later.", (string?)answer["message"]);
        Assert.Null(answer["errors"]);
        Assert.DoesNotContain("s3cret", answer.ToJsonString());
    }

    [Fact]
    public async Task RefusedServerActionFormWithoutJavaScript_RendersThePageItWasPostedFromAgain()
    {
        var calls = new List<string>();
        await using var app = await StartAsync(calls);
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(Post(javaScript: false, "/_netjes/actions/guestbook.sign", token,
            ("name", "Ada"), ("message", "Hi"), ("newPassword", "s3cret"), ("__netjes_return", "/guestbook?from=home")), Ct);
        var page = app.Renderer.LastPayload;

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Empty(calls);
        Assert.Equal(429, (int?)page["status"]);
        Assert.Equal("app/guestbook/page.tsx", (string?)page["page"]);
        Assert.Equal("/guestbook?from=home", (string?)page["url"]);
        var state = page["actionState"]!;
        Assert.Equal("guestbook.sign", (string?)state["action"]);
        Assert.Equal(429, (int?)state["status"]);
        Assert.Equal("Too many requests. Try again later.", (string?)state["errors"]![""]![0]);
        Assert.Equal(["name", "message"], state["values"]!.AsObject().Select(v => v.Key));
        Assert.DoesNotContain(app.Renderer.Payloads, p => p.Contains("s3cret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefusedPageGet_IsAnEmpty429_ThatNetjesAnswersWithTheErrorPage(bool javaScript)
    {
        var calls = new List<string>();
        await using var app = await StartAsync(calls, limitThePage: true);

        var response = javaScript ? await app.GetDataAsync("/guestbook") : await app.GetDocumentAsync("/guestbook");
        var page = await PageOf(javaScript, response, app);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(429, (int?)page["status"]);
        Assert.Equal("app/error.tsx", (string?)page["page"]);
        Assert.Null(page["actionState"]);
    }

    [Fact]
    public async Task ALeaseThatDoesNotSayWhen_SetsNoRetryAfter()
    {
        var calls = new List<string>();
        await using var app = await StartAsync(calls, sayWhen: false);
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(Post(javaScript: true, "/guestbook", token, ("name", "Ada")), Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.False(response.Headers.Contains("Retry-After"));
    }
}
