using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Netjes.Tests.Endpoints;

/// <summary>
/// Regression tests across the app router: layout loaders and their boundaries, signed segment keys, output caching
/// under a base policy, deferred values, forms without JavaScript and what they hand back, and the origin check on actions.
/// </summary>
public class AppRouterRegressionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class NobodyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }

    private static void Nobody(IServiceCollection services) =>
        services.AddAuthorization().AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, NobodyHandler>("Test", null);

    private sealed class AdaHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "ada")], "Test")), "Test")));
    }

    /// <summary>Signed in as Ada, who has no roles.</summary>
    private static void Ada(IServiceCollection services) =>
        services.AddAuthorization().AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, AdaHandler>("Test", null);

    private sealed record Token(string Cookie, string Field, string Value);

    private static async Task<Token> TokenAsync(TestApp app)
    {
        var page = await app.GetDataAsync("/");
        var antiforgery = JsonNode.Parse(await page.Content.ReadAsStringAsync(Ct))!["antiforgery"]!;
        return new Token(
            string.Join("; ", page.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])),
            (string)antiforgery["formFieldName"]!,
            (string)antiforgery["requestToken"]!);
    }

    /// <summary>From the token endpoint, as the client gets one for a page that carries none (a page the output cache stores).</summary>
    private static async Task<Token> FetchedTokenAsync(TestApp app)
    {
        var response = await app.Client.GetAsync("/_netjes/antiforgery", Ct);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        return new Token(
            string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])),
            (string)json["formFieldName"]!,
            (string)json["requestToken"]!);
    }

    private static HttpRequestMessage NoJsPost(string action, Token token, params (string, string)[] fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/_netjes/actions/{action}")
        {
            Content = new FormUrlEncodedContent([.. fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)), new(token.Field, token.Value)]),
        };
        request.Headers.Add("Cookie", token.Cookie);
        return request;
    }

    [Fact]
    public async Task NoJsValidationFailure_WithARootLayoutLoader_RendersThePageWith422()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/guestbook/page.tsx"],
            map: a =>
            {
                a.MapLayoutLoader("/", () => Page.Props(new { Site = "x" }));
                a.MapAction("sign", ([FromForm] string? name) => ServerAction.Invalid("name", "required"));
            });
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(NoJsPost("sign", token, ("name", ""), ("__netjes_return", "/guestbook")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("app/guestbook/page.tsx", (string?)app.Renderer.LastPayload["page"]);
        Assert.Equal("x", (string?)app.Renderer.LastPayload["segments"]![0]!["props"]!["site"]);
    }

    [Fact]
    public async Task Empty401_WithARootLayoutLoader_RendersTheUnauthorizedPageInsideTheRootLayout()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/error.tsx", "app/unauthorized.tsx", "app/admin/page.tsx"],
            map: a => a.MapLayoutLoader("/", () => Page.Props(new { Site = "x" })),
            services: Nobody,
            configureApp: a => a.UseAuthorization(),
            pages: p => p.RequireAuthorization());

        var response = await app.GetDocumentAsync("/admin");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("app/unauthorized.tsx", (string?)app.Renderer.LastPayload["page"]);
        Assert.Equal("x", (string?)app.Renderer.LastPayload["segments"]![0]!["props"]!["site"]);
    }

    [Fact]
    public async Task DeniedUser_NeverRunsTheProtectedSectionsLayoutLoader_AndGetsItsBoundaryWithoutItsLayout()
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/error.tsx", "app/admin/layout.tsx", "app/admin/unauthorized.tsx", "app/admin/page.tsx"],
            map: a => a.MapLayoutLoader("/admin", () =>
            {
                calls++;
                return Page.Props(new { Secret = "s3cret" });
            }).RequireAuthorization(),
            services: Nobody,
            configureApp: a => a.UseAuthorization());

        var response = await app.GetDataAsync("/admin");
        var body = await response.Content.ReadAsStringAsync(Ct);
        var payload = JsonNode.Parse(body)!;

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, calls);
        Assert.DoesNotContain("s3cret", body);
        // The section's own unauthorized page, inside the layouts above the section (its own loader cannot run).
        Assert.Equal("app/admin/unauthorized.tsx", (string?)payload["page"]);
        Assert.Equal(["app"], payload["segments"]!.AsArray().Select(s => (string?)s!["id"]));
    }

    [Fact]
    public async Task SignedInUserWithoutTheRole_GetsTheForbiddenPageInsideTheLayoutsTheyMayUse()
    {
        var adminCalls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/error.tsx", "app/forbidden.tsx", "app/admin/layout.tsx", "app/admin/page.tsx"],
            map: a =>
            {
                a.MapLayoutLoader("/", () => Page.Props(new { Site = "x" })).RequireAuthorization();
                a.MapLayoutLoader("/admin", () =>
                {
                    adminCalls++;
                    return Page.Props(new { Secret = "s3cret" });
                }).RequireAuthorization(p => p.RequireRole("Admin"));
            },
            services: Ada,
            configureApp: a => a.UseAuthorization());

        var response = await app.GetDocumentAsync("/admin");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, adminCalls);
        Assert.Equal("app/forbidden.tsx", (string?)app.Renderer.LastPayload["page"]);
        var segments = app.Renderer.LastPayload["segments"]!.AsArray();
        Assert.Equal(["app"], segments.Select(s => (string?)s!["id"]));
        Assert.Equal("x", (string?)segments[0]!["props"]!["site"]);
    }

    [Fact]
    public async Task AnonymousVisitor_OfAProtectedRoot_GetsTheRootUnauthorizedPageWithoutLayouts()
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/error.tsx", "app/unauthorized.tsx", "app/page.tsx"],
            map: a => a.MapLayoutLoader("/", () =>
            {
                calls++;
                return Page.Props(new { Site = "x" });
            }).RequireAuthorization(),
            services: Nobody,
            configureApp: a => a.UseAuthorization());

        var response = await app.GetDocumentAsync("/");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, calls);
        Assert.Equal("app/unauthorized.tsx", (string?)app.Renderer.LastPayload["page"]);
        Assert.Empty(app.Renderer.LastPayload["segments"]!.AsArray());
    }

    [Fact]
    public async Task ForgedSegmentKey_DoesNotSkipAGuardingLayoutLoader()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/error.tsx", "app/[team]/layout.tsx", "app/[team]/page.tsx", "app/[team]/settings/page.tsx"],
            map: a =>
            {
                a.MapLayoutLoader("/[team]", (string team) => team == "mine" ? Page.Props(new { Team = team }) : Page.Forbidden());
                a.MapPageLoader("/[team]/settings", (string team) => Page.Props(new { Billing = "billing of " + team }));
            });
        var version = (string)(await app.GetDataJsonAsync("/mine"))["version"]!;
        // What the key was before it was signed: anyone could compute it.
        var text = "app/[team]\n" + version + "\n\n\ntheirs";
        var forgedKey = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var request = new HttpRequestMessage(HttpMethod.Get, "/theirs/settings");
        request.Headers.Add(NetjesHeaders.Data, "1");
        request.Headers.Add(NetjesHeaders.Segments, forgedKey);

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("billing", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task SegmentKey_ForRouteValuesWithALineBreak_DoesNotSkipAGuardWhereTheLineBreakFallsElsewhere()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/error.tsx", "app/[team]/[project]/layout.tsx", "app/[team]/[project]/page.tsx"],
            map: a =>
            {
                // Team 'a' is someone else's; the projects of any other team are open.
                a.MapLayoutLoader("/[team]/[project]", (string team, string project) => team == "a" ? Page.Forbidden() : Page.Props(new { Team = team }));
                a.MapPageLoader("/[team]/[project]", (string team, string project) => Page.Props(new { Billing = "billing of " + team }));
            });
        // %0A decodes to a line break in the route value: team "a\nb", project "c".
        var key = (string)(await app.GetDataJsonAsync("/a%0Ab/c"))["segments"]![1]!["key"]!;
        var request = new HttpRequestMessage(HttpMethod.Get, "/a/b%0Ac");
        request.Headers.Add(NetjesHeaders.Data, "1");
        request.Headers.Add(NetjesHeaders.Segments, key);

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("billing", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task NoJsValidationFailure_UnderAnOutputCacheBasePolicy_IsNotAServerError()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/guestbook/page.tsx"],
            map: a => a.MapAction("sign", ([FromForm] string? name) => ServerAction.Invalid("name", "required")),
            services: s => s.AddOutputCache(o => o.AddBasePolicy(b => b.Expire(TimeSpan.FromMinutes(1)))),
            configureApp: a => a.UseOutputCache());
        var token = await FetchedTokenAsync(app);

        var response = await app.Client.SendAsync(NoJsPost("sign", token, ("name", ""), ("__netjes_return", "/guestbook")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task TokenEndpoint_IsNeverServedFromAnOutputCacheBasePolicy()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            services: s => s.AddOutputCache(o => o.AddBasePolicy(b => b.Expire(TimeSpan.FromMinutes(1)))),
            configureApp: a => a.UseOutputCache());

        var first = await app.Client.GetAsync("/_netjes/antiforgery", Ct);
        var second = await app.Client.GetAsync("/_netjes/antiforgery", Ct);

        Assert.True(second.Headers.Contains("Set-Cookie"));
        Assert.Null(second.Headers.Age);
        Assert.NotEqual(await first.Content.ReadAsStringAsync(Ct), await second.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Document_DoesNotWaitForDeferredValuesThePageNeverRead()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props(new { Never = Page.Defer(new TaskCompletionSource<int>().Task) })));

        var response = await app.GetDocumentAsync("/").WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Document_IsNotHeldOpenByADeliveryToASidecarThatStoppedAnswering()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props(new { Late = Page.Defer(Task.Delay(50).ContinueWith(_ => 1)) })));
        // The page never reads the value, so the document is complete at once; the value's delivery then hangs, as it
        // would while the sidecar process restarts.
        app.Renderer.OnResolve = cancellationToken => Task.Delay(Timeout.Infinite, cancellationToken);
        app.Renderer.StreamHandler = async (_, _) =>
        {
            await Task.Delay(200, Ct);
            return new MemoryStream("<!DOCTYPE html><html><body>done</body></html>"u8.ToArray());
        };

        var response = await app.GetDocumentAsync("/").WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Document_IsNotBrokenByADeliveryThatFails_OnceItHasBeenSent()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props(new { Late = Page.Defer(Task.Delay(20).ContinueWith(_ => 1)) })));
        // A crashed sidecar process: the delivery fails outright.
        app.Renderer.OnResolve = _ => throw new HttpRequestException("Connection refused");
        app.Renderer.StreamHandler = async (_, _) =>
        {
            await Task.Delay(200, Ct);
            return new MemoryStream("<!DOCTYPE html><html><body>done</body></html>"u8.ToArray());
        };

        var response = await app.GetDocumentAsync("/").WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("done", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task DataStream_EndsAfterTheStreamTimeout_ReportingWhatNeverArrived()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o => o.StreamTimeout = TimeSpan.FromMilliseconds(300),
            map: a => a.MapPageLoader("/", () => Page.Props(new { Never = Page.Defer(new TaskCompletionSource<int>().Task) })));

        var response = await app.GetDataAsync("/").WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var lines = (await response.Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Equal("d0", (string?)JsonNode.Parse(lines[1])!["id"]);
        Assert.NotNull((string?)JsonNode.Parse(lines[1])!["error"]);
    }

    [Fact]
    public async Task CrawlerDocument_WithAValueThatNeverArrives_IsStillComplete_BeforeTheSidecarGivesUp()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o => o.StreamTimeout = TimeSpan.FromMilliseconds(600),
            map: a => a.MapPageLoader("/", () => Page.Props(new { Never = Page.Defer(new TaskCompletionSource<int>().Task) })));
        app.Renderer.StreamHandler = async (_, options) =>
        {
            // Like the sidecar for a crawler: the document once every value has settled, aborted at its StreamTimeout.
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (!app.Renderer.Resolved.Any(r => r.RenderId == options.RenderId && r.Id == "d0"))
            {
                if (waited.Elapsed >= TimeSpan.FromMilliseconds(600))
                {
                    throw new SsrRenderException("Server rendering did not finish within 1s.", null);
                }

                await Task.Delay(5, Ct);
            }

            return new MemoryStream("<!DOCTYPE html><html><body>failed boundary</body></html>"u8.ToArray());
        };
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)");

        var response = await app.Client.SendAsync(request, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("failed boundary", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("The value did not arrive in time", Assert.Single(app.Renderer.Resolved).Error);
    }

    public sealed class Node
    {
        public Node? Next { get; set; }
    }

    [Fact]
    public async Task DeferredValueThatCannotBeSerialized_FailsAlone()
    {
        var cycle = new Node();
        cycle.Next = cycle;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props(new
            {
                Broken = Page.Defer(Task.FromResult(cycle)),
                Fine = Page.Defer(Task.Delay(100).ContinueWith(_ => 42)),
            })));

        var response = await app.GetDataAsync("/").WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var lines = (await response.Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(l => JsonNode.Parse(l)!).ToList();

        Assert.Equal(["d0", "d1"], lines.Select(l => (string?)l["id"]));
        Assert.NotNull((string?)lines[0]["error"]);
        Assert.Equal(42, (int?)lines[1]["value"]);
        Assert.Contains(app.Logs, l => l.Level >= LogLevel.Warning && l.Message.Contains("d0"));
    }

    public sealed class DisposedProxy
    {
        // What a lazy-loading proxy does once Page.Defer(sp => ...) has disposed its DbContext.
        public int Count => throw new ObjectDisposedException("TaskBoardDb");
    }

    public sealed class Unwritable
    {
        public Type Kind { get; } = typeof(int);
    }

    public sealed class CancelledGetter
    {
        public int Count => throw new OperationCanceledException("the query was cancelled");
    }

    [Theory]
    [InlineData("getter")]
    [InlineData("type")]
    [InlineData("cancelled")]
    public async Task DeferredValueWhoseSerializationThrows_FailsAlone(string kind)
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props(new
            {
                Broken = Page.Defer(Task.FromResult<object>(kind switch
                {
                    "getter" => new DisposedProxy(),
                    "cancelled" => new CancelledGetter(),
                    _ => new Unwritable(),
                })),
                Fine = Page.Defer(Task.Delay(100).ContinueWith(_ => 42)),
            })));

        var response = await app.GetDataAsync("/").WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var lines = (await response.Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(l => JsonNode.Parse(l)!).ToList();

        Assert.Equal(["d0", "d1"], lines.Select(l => (string?)l["id"]));
        Assert.NotNull((string?)lines[0]["error"]);
        Assert.Equal(42, (int?)lines[1]["value"]);
    }

    [Fact]
    public async Task FailedDeferredValues_AreLogged()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props(new { Broken = Page.Defer(Task.FromException<int>(new InvalidOperationException("comments down"))) })));

        await (await app.GetDataAsync("/")).Content.ReadAsStringAsync(Ct);

        Assert.Contains(app.Logs, l => l.Level >= LogLevel.Warning && l.Message.Contains("comments down"));
    }

    public sealed class ScopedThing
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    [Fact]
    public async Task DeferWithServices_RunsInItsOwnScope()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", (ScopedThing requestThing) => Page.Props(new
            {
                Request = requestThing.Id,
                Deferred = Page.Defer(async services =>
                {
                    await Task.Yield();
                    return services.GetRequiredService<ScopedThing>().Id;
                }),
            })),
            services: s => s.AddScoped<ScopedThing>());

        var lines = (await (await app.GetDataAsync("/")).Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.NotEqual((string?)JsonNode.Parse(lines[0])!["props"]!["request"], (string?)JsonNode.Parse(lines[1])!["value"]);
    }

    [Theory]
    [InlineData("/\t/evil.example")]
    [InlineData("/\n/evil.example")]
    [InlineData("\t//evil.example")]
    public async Task NoJsReturnUrl_WithControlCharacters_GoesHome(string returnUrl)
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], map: a => a.MapAction("sign", () => ServerAction.Ok()));
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(NoJsPost("sign", token, ("__netjes_return", returnUrl)), Ct);

        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    // The forms below declare every field they post, so the private words alone decide what a failure echoes.
    public sealed class LoginForm
    {
        public string? Email { get; init; }
        public string? Password { get; init; }
        public string? ConfirmPassword { get; init; }
    }

    public sealed class PaymentForm
    {
        public string? Email { get; init; }
        public string? ShippingAddress { get; init; }
        public string? NewPwd { get; init; }
        [JsonPropertyName("pin_code")] public string? PinCode { get; init; }
        public string? Otp { get; init; }
        public string? CardNumber { get; init; }
        [JsonPropertyName("card-cvc")] public string? CardCvc { get; init; }
        public string? ApiToken { get; init; }
    }

    public sealed class LookAlikeForm
    {
        public string? Email { get; init; }
        public string? Password2 { get; init; }
        public string? Newpassword { get; init; }
        public string? PINCode { get; init; }
        public string? Pin2 { get; init; }
        public string? OTPValue { get; init; }
        public string? Cardnumber { get; init; }
        public string? Creditcard { get; init; }
        public string? Cardno { get; init; }
        public string? Cardcvc { get; init; }
        public string? Newpwd { get; init; }
        public string? Otpcode { get; init; }
        public string? Ibannumber { get; init; }
        public string? Ssnlast4 { get; init; }
        public string? X1Pin { get; init; }
        public string? Step2Otp { get; init; }
        [JsonPropertyName("cc-number")] public string? CcNumber { get; init; }
        [JsonPropertyName("cc-csc")] public string? CcCsc { get; init; }
        public string? Spinner { get; init; }
        public string? ShippingAddress { get; init; }
        public string? Account { get; init; }
    }

    [Fact]
    public async Task NoJsValidationFailure_DoesNotEchoPasswords()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/login/page.tsx"],
            map: a => a.MapAction("login", ([FromForm] LoginForm input) => ServerAction.Invalid("email", "unknown")));
        var token = await TokenAsync(app);

        await app.Client.SendAsync(
            NoJsPost("login", token, ("email", "ada@example.com"), ("password", "hunter2"), ("confirmPassword", "hunter2"), ("__netjes_return", "/login")),
            Ct);

        var values = app.Renderer.LastPayload["actionState"]!["values"]!;
        Assert.Equal("ada@example.com", (string?)values["email"]);
        Assert.Null(values["password"]);
        Assert.Null(values["confirmPassword"]);
    }

    [Fact]
    public async Task NoJsValidationFailure_DoesNotEchoSecretsByTheWordsInTheirNames()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/checkout/page.tsx"],
            map: a => a.MapAction("pay", ([FromForm] PaymentForm input) => ServerAction.Invalid("email", "unknown")));
        var token = await TokenAsync(app);

        await app.Client.SendAsync(
            NoJsPost("pay", token,
                ("email", "ada@example.com"), ("shippingAddress", "1 Main St"), ("newPwd", "x"), ("pin_code", "1234"), ("otp", "999"),
                ("cardNumber", "4242"), ("card-cvc", "123"), ("apiToken", "t"), ("__netjes_return", "/checkout")),
            Ct);

        var values = app.Renderer.LastPayload["actionState"]!["values"]!.AsObject();
        Assert.Equal(["email", "shippingAddress"], values.Select(v => v.Key));
    }

    [Fact]
    public async Task NoJsValidationFailure_HidesRunTogetherAndAcronymNames_TooButNotLookAlikes()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/checkout/page.tsx"],
            map: a => a.MapAction("pay", ([FromForm] LookAlikeForm input) => ServerAction.Invalid("email", "unknown")));
        var token = await TokenAsync(app);

        await app.Client.SendAsync(
            NoJsPost("pay", token,
                ("email", "a@b.c"), ("password2", "x"), ("newpassword", "x"), ("PINCode", "1"), ("pin2", "1"), ("OTPValue", "1"),
                ("cardnumber", "1"), ("creditcard", "1"), ("cardno", "1"), ("cardcvc", "1"), ("newpwd", "1"), ("NEWPWD", "1"),
                ("otpcode", "1"), ("ibannumber", "1"), ("ssnlast4", "1"), ("x1Pin", "1"), ("step2Otp", "1"), ("cc-number", "1"), ("cc-csc", "1"),
                ("spinner", "on"), ("shippingAddress", "1 Main St"), ("account", "acme"), ("__netjes_return", "/checkout")),
            Ct);

        // Hiding a field by mistake costs a retype; echoing a card number puts it in the page.
        var values = app.Renderer.LastPayload["actionState"]!["values"]!.AsObject();
        Assert.Equal(["email", "spinner", "shippingAddress", "account"], values.Select(v => v.Key));
    }

    public sealed class AccountForm
    {
        public string? Email { get; init; }
        public string? Pass { get; init; }
        public string? NewPw { get; init; }
        public string? ApiKey { get; init; }
        [JsonPropertyName("api_key")] public string? ApiKey2 { get; init; }
        public string? PrivateKey { get; init; }
        public string? Credential { get; init; }
        [JsonPropertyName("mfa_code")] public string? MfaCode { get; init; }
        public string? RecoveryCode { get; init; }
        public string? Bsn { get; init; }
        public string? PostalCode { get; init; }
    }

    [Fact]
    public async Task NoJsValidationFailure_HidesKeysCredentialsAndRecoveryCodes()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/account/page.tsx"],
            map: a => a.MapAction("account", ([FromForm] AccountForm input) => ServerAction.Invalid("email", "unknown")));
        var token = await TokenAsync(app);

        await app.Client.SendAsync(
            NoJsPost("account", token,
                ("email", "a@b.c"), ("pass", "x"), ("newPw", "x"), ("apiKey", "x"), ("api_key", "x"), ("privateKey", "x"), ("credential", "x"),
                ("mfa_code", "1"), ("recoveryCode", "1"), ("bsn", "1"), ("postalCode", "1234 AB"), ("__netjes_return", "/account")),
            Ct);

        var values = app.Renderer.LastPayload["actionState"]!["values"]!.AsObject();
        Assert.Equal(["email", "postalCode"], values.Select(v => v.Key));
    }

    [Fact]
    public async Task NoJsValidationFailure_EchoesWhatPrivateFormFieldsAllows()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/checkout/page.tsx"],
            configure: o => o.PrivateFormFields = ["iban"],
            map: a => a.MapAction("pay", ([FromForm] string email, [FromForm] string pin, [FromForm] string iban) => ServerAction.Invalid("email", "unknown")));
        var token = await TokenAsync(app);

        await app.Client.SendAsync(NoJsPost("pay", token, ("email", "a@b.c"), ("pin", "1"), ("iban", "NL00"), ("__netjes_return", "/checkout")), Ct);

        var values = app.Renderer.LastPayload["actionState"]!["values"]!.AsObject();
        Assert.Equal(["email", "pin"], values.Select(v => v.Key));
    }

    public sealed class IntegrationForm
    {
        public string? Email { get; init; }
        [JsonPropertyName("api_key")] public string? ApiKey { get; init; }
        [JsonPropertyName("auth-code")] public string? AuthCode { get; init; }
        public string? ApiKeyHint { get; init; }
    }

    [Fact]
    public async Task NoJsValidationFailure_HidesPrivateWordsWrittenWithSeparators()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/settings/page.tsx"],
            configure: o => o.PrivateFormFields = ["api_key", "auth-code"],
            map: a => a.MapAction("connect", ([FromForm] IntegrationForm input) => ServerAction.Invalid("email", "unknown")));
        var token = await TokenAsync(app);

        await app.Client.SendAsync(
            NoJsPost("connect", token, ("email", "a@b.c"), ("api_key", "x"), ("auth-code", "1"), ("apiKeyHint", "x"), ("__netjes_return", "/settings")),
            Ct);

        var values = app.Renderer.LastPayload["actionState"]!["values"]!.AsObject();
        Assert.Equal(["email"], values.Select(v => v.Key));
    }

    [Fact]
    public async Task ActionCall_FromOriginNull_IsForbidden()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], map: a => a.MapAction("noop", () => ServerAction.Ok()));
        var token = await TokenAsync(app);
        var request = NoJsPost("noop", token);
        request.Headers.Add(NetjesHeaders.Data, "1");
        request.Headers.Add("Origin", "null");

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("cross-site", HttpStatusCode.Forbidden)]
    [InlineData("same-site", HttpStatusCode.Forbidden)]
    [InlineData("same-origin", HttpStatusCode.OK)]
    public async Task ActionCall_FromOriginNull_IsJudgedBySecFetchSite(string site, HttpStatusCode expected)
    {
        // A no-JS form post from a page with Referrer-Policy: no-referrer sends Origin: null, but the browser still
        // says it came from this origin.
        await using var app = await TestApp.StartAsync(["app/page.tsx"], map: a => a.MapAction("noop", () => ServerAction.Ok()));
        var token = await TokenAsync(app);
        var request = NoJsPost("noop", token);
        request.Headers.Add(NetjesHeaders.Data, "1");
        request.Headers.Add("Origin", "null");
        request.Headers.Add("Sec-Fetch-Site", site);

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ActionCall_FromAnAllowedOrigin_IsAccepted()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o => o.AllowedActionOrigins = ["https://shop.example"],
            map: a => a.MapAction("noop", () => ServerAction.Ok()));
        var token = await TokenAsync(app);
        var request = NoJsPost("noop", token);
        request.Headers.Add(NetjesHeaders.Data, "1");
        request.Headers.Add("Origin", "https://shop.example");

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
