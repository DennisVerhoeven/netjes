using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Netjes.Routing;

namespace Netjes.Tests.Endpoints;

/// <summary>
/// Loaders, page actions, layout loaders, server actions and endpoints mapped through a page package's mount, which map
/// below its URL and only for the package's own pages, and the app's page that replaces one with <c>.Override()</c>.
/// </summary>
public class PagePackageLoaderTests
{
    private const string Help = TestPagePackage.Name;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The app with the package mounted at <paramref name="prefix"/>: installed in Development, built in Production.</summary>
    private static Task<TestApp> StartAsync(
        string[] appFiles,
        string[] packageFiles,
        Action<PagePackageBuilder>? mount = null,
        string prefix = "/help",
        string environment = "Development",
        Action<WebApplication>? before = null,
        Action<WebApplication>? after = null,
        Action<IServiceCollection>? services = null,
        Action<WebApplication>? configureApp = null,
        Action<NetjesOptions>? configure = null,
        Action<WebApplication>? beforeNetjes = null) =>
        TestApp.StartAsync(
            environment == "Production" ? [] : appFiles,
            environment: environment,
            prepareRoot: root =>
            {
                if (environment == "Production")
                {
                    TestPagePackage.WriteBuild(root, appFiles, [new(Help, packageFiles)]);
                }
                else
                {
                    TestPagePackage.Install(root, packageFiles);
                }
            },
            map: a =>
            {
                before?.Invoke(a);
                var mounted = a.MapPagePackage(TestPagePackage.Descriptor, prefix);
                mount?.Invoke(mounted);
                after?.Invoke(a);
            },
            services: services,
            configureApp: configureApp,
            configure: configure,
            beforeNetjes: beforeNetjes);

    private static IEnumerable<RouteEndpoint> Endpoints(TestApp app) =>
        app.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task ALoaderThroughTheMount_ServesThePackagesPageBelowTheMount(string environment)
    {
        await using var app = await StartAsync(
            ["app/page.tsx"],
            ["page.tsx", "articles/[slug]/page.tsx"],
            environment: environment,
            mount: m =>
            {
                m.MapPageLoader("/articles/[slug]", (string slug) => Page.Props(new { Title = $"About {slug}" }));
                m.MapPageLoader("/", () => Page.Props(new { Title = "Help" }));
            });

        var article = await app.GetDataJsonAsync("/help/articles/x");
        var root = await app.GetDataJsonAsync("/help");

        Assert.Equal($"{Help}/app/articles/[slug]/page.tsx", (string?)article["page"]);
        Assert.Equal("About x", (string?)article["props"]!["title"]);
        Assert.Equal($"{Help}/app/page.tsx", (string?)root["page"]);
        Assert.Equal("Help", (string?)root["props"]!["title"]);
        var loader = Endpoints(app).Single(e => e.Metadata.GetMetadata<PageRouteMetadata>() is { IsLoader: true, Pattern: "/help/articles/[slug]" });
        Assert.Equal("/help/articles/{slug}", loader.RoutePattern.RawText);
        Assert.Equal("/help", Endpoints(app).Single(e => e.Metadata.GetMetadata<PageRouteMetadata>() is { IsLoader: true, Pattern: "/help" }).RoutePattern.RawText);
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetDataAsync("/help/help/articles/x")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetDataAsync("/articles/x")).StatusCode);
    }

    [Theory]
    [InlineData("/", "/", "/")]
    [InlineData("/", "/articles/[slug]", "/articles/{slug}")]
    [InlineData("/(site)", "/", "/")]
    [InlineData("/(site)", "/articles/[slug]", "/articles/{slug}")]
    [InlineData("/[tenant]/help", "/", "/{tenant}/help")]
    [InlineData("/help", "/articles/[slug]", "/help/articles/{slug}")]
    public async Task ALoaderThroughTheMount_MapsAtThePagesOwnTemplate(string prefix, string pattern, string template)
    {
        await using var app = await StartAsync(
            [],
            ["page.tsx", "articles/[slug]/page.tsx"],
            prefix: prefix,
            mount: m =>
            {
                m.MapPageLoader(pattern, () => Page.Props());
                m.MapPageAction(pattern, () => Page.Redirect("/"));
            });

        var mapped = Endpoints(app).Where(e => e.Metadata.GetMetadata<PageRouteMetadata>() is { Pattern: var p } && p.EndsWith(pattern.TrimStart('/'), StringComparison.Ordinal)
            && (e.Metadata.GetMetadata<PageRouteMetadata>()!.IsLoader || e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("POST"))).ToList();

        Assert.Equal(2, mapped.Count);
        Assert.All(mapped, e => Assert.Equal(template, e.RoutePattern.RawText));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task ALoaderThroughAMountWithAValue_BindsThePrefixsValueAndThePagesOwn(string environment)
    {
        await using var app = await StartAsync(
            ["app/page.tsx"],
            ["articles/[slug]/page.tsx", "articles/[slug]/feedback/page.tsx"],
            prefix: "/[tenant]/help",
            environment: environment,
            mount: m =>
            {
                m.MapPageLoader("/articles/[slug]", (string tenant, string slug) => Page.Props(new { Title = $"{tenant}: {slug}" }));
                m.MapPageLoader("/articles/[slug]/feedback", ([FromRoute(Name = "tenant")] string site, [FromRoute] string slug) =>
                    Page.Props(new { Title = $"{site} on {slug}" }));
            });

        Assert.Equal("acme: x", (string?)(await app.GetDataJsonAsync("/acme/help/articles/x"))["props"]!["title"]);
        Assert.Equal("acme on x", (string?)(await app.GetDataJsonAsync("/acme/help/articles/x/feedback"))["props"]!["title"]);
        Assert.Contains(Endpoints(app), e => e.Metadata.GetMetadata<PageRouteMetadata>()?.Pattern == "/[tenant]/help/articles/[slug]");
    }

    [Fact]
    public async Task ALoaderThroughTheMount_BindingARouteValueOutsideTheMergedTemplate_FailsAtStartup()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(
            ["app/page.tsx"],
            ["articles/[slug]/page.tsx"],
            prefix: "/[tenant]/help",
            mount: m => m.MapPageLoader("/articles/[slug]", ([FromRoute] string id) => Page.Props())));

        Assert.Contains($"{Help}/app/articles/[slug]/page.tsx", ex.Message);
        Assert.Contains("'id'", ex.Message);
    }

    private static readonly string[] AppPages = ["app/page.tsx", "app/help/(center)/shortcuts/page.tsx", "app/help/faq/layout.tsx", "app/help/faq/page.tsx"];

    private static readonly string[] PackagePages = ["layout.tsx", "page.tsx", "contact/page.tsx", "(center)/layout.tsx", "(center)/articles/[slug]/page.tsx"];

    public static TheoryData<string, Action<PagePackageBuilder>, string[]> ForTheApp => new()
    {
        {
            "a loader on the mount", m => m.MapPageLoader("/shortcuts", () => Page.Props()),
            ["on the mount of '@acme/help-center' at /help", "'app/help/(center)/shortcuts/page.tsx'", "app.MapPageLoader(\"/help/shortcuts\")"]
        },
        {
            "a page action on the mount", m => m.MapPageAction("/faq", () => Page.Redirect("/")),
            ["on the mount of '@acme/help-center' at /help", "'app/help/faq/page.tsx'", "app.MapPageAction(\"/help/faq\")"]
        },
        {
            "a layout loader on the mount, by folder", m => m.MapLayoutLoader("app/faq", () => Page.Props()),
            ["on the mount of '@acme/help-center' at /help", "'app/help/faq/layout.tsx'", "app.MapLayoutLoader"]
        },
        {
            "a layout loader on the mount, by URL", m => m.MapLayoutLoader("/faq", () => Page.Props()),
            ["on the mount of '@acme/help-center' at /help", "'app/help/faq/layout.tsx'", "app.MapLayoutLoader"]
        },
    };

    [Theory]
    [MemberData(nameof(ForTheApp))]
    public async Task TheMount_RefusesTheAppsPagesAndLayouts_NamingTheMount(string what, Action<PagePackageBuilder> map, string[] message)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(AppPages, PackagePages, mount: map));

        Assert.All(message, part => Assert.Contains(part, ex.Message));
        Assert.NotNull(what);
    }

    public static TheoryData<string, Action<WebApplication>, string[]> ForThePackage => new()
    {
        {
            "a loader", a => a.MapPageLoader("/help/contact", () => Page.Props()),
            ["'@acme/help-center/app/contact/page.tsx'", "the page package '@acme/help-center' mounted at /help", ".Override(\"/help/contact\")", "app/help/contact/page.tsx"]
        },
        {
            "a page action", a => a.MapPageAction("/help/articles/[slug]", () => Page.Redirect("/")),
            ["'@acme/help-center/app/(center)/articles/[slug]/page.tsx'", "the page package '@acme/help-center' mounted at /help", "app/help/(center)/articles/[slug]/page.tsx"]
        },
        {
            "a layout loader, by URL", a => a.MapLayoutLoader("/help", () => Page.Props()),
            ["'@acme/help-center/app/layout.tsx'", "the page package '@acme/help-center' mounted at /help", "through its mount"]
        },
        {
            "a layout loader, by folder", a => a.MapLayoutLoader("app/help/(center)", () => Page.Props()),
            ["'@acme/help-center/app/(center)/layout.tsx'", "the page package '@acme/help-center' mounted at /help", "through its mount"]
        },
    };

    [Theory]
    [MemberData(nameof(ForThePackage))]
    public async Task TheApp_IsRefusedThePackagesPagesAndLayouts_NamingTheMount(string what, Action<WebApplication> map, string[] message)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(AppPages, PackagePages, after: map));

        Assert.All(message, part => Assert.Contains(part, ex.Message));
        Assert.NotNull(what);
    }

    private static void Nobody(IServiceCollection services) =>
        services.AddAuthorization().AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, NobodyHandler>("Test", null);

    private sealed class NobodyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }

    private static async Task AssertUnauthorizedAsync(TestApp app, string url)
    {
        var response = await app.GetDataAsync(url);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("app/unauthorized.tsx", (string?)JsonNode.Parse(body)!["page"]);
        Assert.DoesNotContain("s3cret", body);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task TheMountsLayoutLoaderInAGroup_ProtectsTheAppsPageInThatGroup_BeforeEitherLoaderRuns(string environment)
    {
        var calls = 0;
        await using var app = await StartAsync(
            ["app/layout.tsx", "app/unauthorized.tsx", "app/help/(center)/shortcuts/page.tsx"],
            ["page.tsx", "(center)/layout.tsx", "(center)/articles/[slug]/page.tsx"],
            environment: environment,
            mount: m => m.MapLayoutLoader("app/(center)", () =>
            {
                calls++;
                return Page.Props(new { Secret = "s3cret" });
            }).RequireAuthorization(),
            after: a => a.MapPageLoader("/help/shortcuts", () =>
            {
                calls++;
                return Page.Props(new { Secret = "s3cret" });
            }),
            services: Nobody,
            configureApp: a => a.UseAuthorization());

        Assert.NotNull(app.App.Services.GetRequiredService<LayoutLoaderRegistry>().Find("app/help/(center)"));
        await AssertUnauthorizedAsync(app, "/help/shortcuts");
        await AssertUnauthorizedAsync(app, "/HELP/Shortcuts/");
        await AssertUnauthorizedAsync(app, "/help/articles/x");
        Assert.Equal(0, calls);
        Assert.Equal(HttpStatusCode.OK, (await app.GetDataAsync("/help")).StatusCode);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task AnAppLoaderMappedBeforeTheMount_GetsTheConventionsOfThePackagesLayoutLoaderAboveIt(string environment)
    {
        var calls = 0;
        await using var app = await StartAsync(
            ["app/layout.tsx", "app/unauthorized.tsx", "app/help/(center)/shortcuts/page.tsx"],
            ["(center)/layout.tsx", "(center)/articles/[slug]/page.tsx"],
            environment: environment,
            before: a => a.MapPageLoader("/help/shortcuts", () =>
            {
                calls++;
                return Page.Props(new { Secret = "s3cret" });
            }),
            mount: m => m.MapLayoutLoader("/", () => Page.Props()).RequireAuthorization(),
            services: Nobody,
            configureApp: a => a.UseAuthorization());

        await AssertUnauthorizedAsync(app, "/help/shortcuts");
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task AnAppLayoutLoaderAboveTheMount_ProtectsThePackagesLoaderPagesAndPageActions(string environment)
    {
        var calls = 0;
        await using var app = await StartAsync(
            ["app/layout.tsx", "app/unauthorized.tsx", "app/members/layout.tsx", "app/members/page.tsx"],
            ["page.tsx", "contact/page.tsx"],
            prefix: "/members/help",
            environment: environment,
            before: a => a.MapLayoutLoader("/members", () => Page.Props(new { Secret = "s3cret" })).RequireAuthorization(),
            mount: m =>
            {
                m.MapPageLoader("/contact", () =>
                {
                    calls++;
                    return Page.Props(new { Secret = "s3cret" });
                });
                m.MapPageAction("/contact", () =>
                {
                    calls++;
                    return Page.Redirect("/");
                }).DisableAntiforgery();
            },
            services: Nobody,
            configureApp: a => a.UseAuthorization());

        await AssertUnauthorizedAsync(app, "/members/help/contact");
        await AssertUnauthorizedAsync(app, "/Members/HELP/contact");
        var post = await app.Client.PostAsync("/members/help/contact", new FormUrlEncodedContent([]), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("/help", "/HELP/Contact", "/help/contact")]
    [InlineData("/help", "/Help/Articles/X?tab=1", "/help/articles/X?tab=1")]
    [InlineData("/help", "/help/contact/", "/help/contact")]
    [InlineData("/(site)/help", "/HELP/contact", "/help/contact")]
    [InlineData("/[tenant]/help", "/Acme/HELP/Articles/X", "/Acme/help/articles/X")]
    public async Task AnotherSpelling_OfAFilePageOrALoaderPageUnderTheMount_RedirectsToTheMountsSpelling(string prefix, string sent, string location)
    {
        await using var app = await StartAsync(
            ["app/page.tsx"],
            ["contact/page.tsx", "articles/[slug]/page.tsx"],
            prefix: prefix,
            mount: m => m.MapPageLoader("/articles/[slug]", (string slug) => Page.Props()),
            configure: o => o.TrailingSlash = TrailingSlash.Never,
            // The request target, as Kestrel sets it; TestServer sets none.
            beforeNetjes: a => a.Use((HttpContext http, RequestDelegate next) =>
            {
                http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()!.RawTarget =
                    Microsoft.AspNetCore.Http.Extensions.UriHelper.GetEncodedPathAndQuery(http.Request);
                return next(http);
            }));

        var response = await app.GetDocumentAsync(sent);

        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
        Assert.Equal(location, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task AServerActionThroughTheMount_MapsAtTheActionsUrl_WithTheMountsConventions()
    {
        await using var app = await StartAsync(
            ["app/page.tsx"],
            ["page.tsx"],
            prefix: "/[tenant]/help",
            mount: m =>
            {
                m.MapAction("help.vote", () => ServerAction.Ok()).DisableAntiforgery();
                m.MapAction("help.open", () => ServerAction.Ok()).DisableAntiforgery().AllowAnonymous();
                m.RequireAuthorization();
            },
            services: Nobody,
            configureApp: a => a.UseAuthorization());

        var actions = Endpoints(app).Where(e => e.Metadata.GetMetadata<ServerActionMetadata>() is not null).ToList();
        Assert.Equal(["/_netjes/actions/help.open", "/_netjes/actions/help.vote"], actions.Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/')).Order());
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(app, "/_netjes/actions/help.vote")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(app, "/_netjes/actions/help.open")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await CallAsync(app, "/acme/help/_netjes/actions/help.open")).StatusCode);
    }

    /// <summary>A server action call as the client router makes it.</summary>
    private static Task<HttpResponseMessage> CallAsync(TestApp app, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add(NetjesHeaders.Data, "1");
        return app.Client.SendAsync(request, Ct);
    }

    [Theory]
    [InlineData("help.vote", false)]
    [InlineData("Help.Vote", false)]
    [InlineData("HELP.VOTE", true)]
    public async Task AServerActionOfTheApp_NamedAsOneOfThePackages_InAnyCase_FailsAtStartup_NamingBoth(string name, bool appFirst)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(
            ["app/page.tsx"],
            ["page.tsx"],
            before: a =>
            {
                if (appFirst) a.MapAction(name, () => ServerAction.Ok());
            },
            mount: m => m.MapAction("help.vote", () => ServerAction.Ok()),
            after: a =>
            {
                if (!appFirst) a.MapAction(name, () => ServerAction.Ok());
            }));

        Assert.Contains("the page package '@acme/help-center'", ex.Message);
        Assert.Contains("the app", ex.Message);
        Assert.Contains("'help.vote'", ex.Message);
        Assert.Contains($"'{name}'", ex.Message);
    }

    public static TheoryData<string, Action<IEndpointRouteBuilder>> OnAGroupOfTheMount => new()
    {
        { "MapPageLoader", g => g.MapPageLoader("/", () => Page.Props()) },
        { "MapPageAction", g => g.MapPageAction("/", () => Page.Redirect("/")) },
        { "MapLayoutLoader", g => g.MapLayoutLoader("/", () => Page.Props()) },
        { "MapAction", g => g.MapAction("help.vote", () => ServerAction.Ok()) },
    };

    [Theory]
    [MemberData(nameof(OnAGroupOfTheMount))]
    public async Task OnAMapGroupMadeFromTheMount_TheNetjesExtensionsThrow_NamingTheMount(string method, Action<IEndpointRouteBuilder> map)
    {
        foreach (var prefix in new[] { "", "/", "/x" })
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(
                ["app/page.tsx"], ["layout.tsx", "page.tsx"], mount: m => map(m.MapGroup(prefix).MapGroup(""))));

            Assert.StartsWith($"{method} is called on a MapGroup made from the mount of '@acme/help-center' at /help", ex.Message);
        }
    }

    [Fact]
    public async Task APostEndpointThroughTheMount_MapsBelowItsUrl_WithItsConventions()
    {
        await using var app = await StartAsync(
            ["app/page.tsx"],
            ["page.tsx"],
            mount: m =>
            {
                m.MapPost("/feedback", () => "thanks");
                m.MapPost("/ping", () => "pong").AllowAnonymous();
                m.RequireAuthorization();
            },
            services: Nobody,
            configureApp: a => a.UseAuthorization());

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.PostAsync("/help/feedback", null, Ct)).StatusCode);
        Assert.Equal("pong", await (await app.Client.PostAsync("/help/ping", null, Ct)).Content.ReadAsStringAsync(Ct));
        Assert.NotEqual(HttpStatusCode.OK, (await app.Client.PostAsync("/ping", null, Ct)).StatusCode);
    }

    [Theory]
    [InlineData("Development", "/help/contact", false)]
    [InlineData("Production", "/help/contact", false)]
    [InlineData("Development", "/HELP/Contact/", false)]
    [InlineData("Development", "/help/contact", true)]
    public async Task AnOverride_RendersTheAppsFile_WithThePackagesLoader_AndPostsReachThePackagesPageAction(
        string environment, string pattern, bool overrideFirst)
    {
        var posted = 0;
        await using var app = await StartAsync(
            ["app/page.tsx", "app/help/contact/page.tsx"],
            ["page.tsx", "contact/page.tsx"],
            environment: environment,
            mount: m =>
            {
                if (overrideFirst) m.Override(pattern);
                m.MapPageLoader("/contact", () => Page.Props(new { Email = "help@site.example" }));
                m.MapPageAction("/contact", () =>
                {
                    posted++;
                    return Page.Redirect("/help");
                }).DisableAntiforgery();
                if (!overrideFirst) m.Override(pattern);
            });

        var payload = await app.GetDataJsonAsync("/help/contact");
        var post = await app.Client.PostAsync("/help/contact", new FormUrlEncodedContent([]), Ct);

        Assert.Equal("app/help/contact/page.tsx", (string?)payload["page"]);
        Assert.Equal("help@site.example", (string?)payload["props"]!["email"]);
        Assert.Equal(HttpStatusCode.SeeOther, post.StatusCode);
        Assert.Equal(1, posted);
        Assert.Equal($"{Help}/app/page.tsx", (string?)(await app.GetDataJsonAsync("/help"))["page"]);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task AnOverriddenFilePage_KeepsTheMountsConventions(string environment)
    {
        await using var app = await StartAsync(
            ["app/layout.tsx", "app/unauthorized.tsx", "app/help/contact/page.tsx"],
            ["page.tsx", "contact/page.tsx"],
            environment: environment,
            mount: m => m.Override("/help/contact").RequireAuthorization(),
            services: Nobody,
            configureApp: a => a.UseAuthorization());

        await AssertUnauthorizedAsync(app, "/help/contact");
        await AssertUnauthorizedAsync(app, "/Help/CONTACT");
    }

    public static TheoryData<string, string[], string, Type, string[]> RefusedOverrides => new()
    {
        { "no package page", [], "/help/nope", typeof(RouteConflictException), ["no page package has a page at '/help/nope'"] },
        { "an app page only", ["app/help/faq/page.tsx"], "/help/faq", typeof(RouteConflictException), ["no page package has a page at '/help/faq'"] },
        { "a package page with no app file", [], "/help/contact", typeof(RouteConflictException), ["the app has no page at '/help/contact'", "app/help/contact/page.tsx"] },
        { "other parameter names", ["app/help/articles/[id]/page.tsx"], "/help/articles/[id]", typeof(RouteConflictException), ["must name their parameters alike"] },
        { "a parent folder", [], "/help/../account/x", typeof(ArgumentException), ["'..'"] },
        { "an encoded parent folder", [], "/help/%2e%2e/x", typeof(ArgumentException), ["'%'"] },
        { "a backslash", [], "/help\\contact", typeof(ArgumentException), ["'\\'"] },
        { "an empty segment", [], "/help//contact", typeof(ArgumentException), ["empty segment"] },
        { "no leading slash", [], "help/contact", typeof(ArgumentException), ["starts with '/'"] },
        { "outside the mount", ["app/other/contact/page.tsx"], "/other/contact", typeof(ArgumentException), ["not below the mount of '@acme/help-center' at /help"] },
        { "the mount's prefix only in part", ["app/helpdesk/contact/page.tsx"], "/helpdesk/contact", typeof(ArgumentException), ["not below the mount"] },
        { "another package's page", ["app/help/status/page.tsx"], "/help/status", typeof(RouteConflictException), ["'@acme/status/app/page.tsx'", "a page of '@acme/status', mounted at /help/status"] },
    };

    [Theory]
    [MemberData(nameof(RefusedOverrides))]
    public async Task AnOverride_ThatFitsNoPageOfThePackage_IsRefused_SayingWhy(string what, string[] appFiles, string pattern, Type error, string[] message)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => TestApp.StartAsync(
            ["app/page.tsx", .. appFiles],
            prepareRoot: root =>
            {
                TestPagePackage.Install(root, ["page.tsx", "contact/page.tsx", "articles/[slug]/page.tsx"]);
                TestPagePackage.Install(root, ["page.tsx"], name: TestPagePackage.Other.Name);
            },
            map: a =>
            {
                a.MapPagePackage(TestPagePackage.Other, "/help/status");
                a.MapPagePackage(TestPagePackage.Descriptor, "/help").Override(pattern);
            },
            pages: _ => Assert.Fail("MapPages() ran.")));

        Assert.IsType(error, ex);
        Assert.All(message, part => Assert.Contains(part, ex.Message));
        Assert.Contains(nameof(PagePackageBuilder.Override), ex.StackTrace);
        Assert.NotNull(what);
    }

    [Fact]
    public async Task TheAppsLoaderForAnOverriddenPage_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(
            ["app/page.tsx", "app/help/contact/page.tsx"],
            ["contact/page.tsx"],
            mount: m => m.Override("/help/contact"),
            after: a => a.MapPageLoader("/help/contact", () => Page.Props())));

        Assert.Contains("'app/help/contact/page.tsx' replaces '@acme/help-center/app/contact/page.tsx'", ex.Message);
        Assert.Contains("renders with the package's loader and page action", ex.Message);
    }

    [Fact]
    public async Task AnOverride_AfterMapPages_Throws()
    {
        PagePackageBuilder? mount = null;
        await using var app = await StartAsync(
            ["app/page.tsx", "app/help/contact/page.tsx"],
            ["contact/page.tsx"],
            mount: m => mount = m.Override("/help/contact"));

        var ex = Assert.Throws<InvalidOperationException>(() => mount!.Override("/help/contact"));

        Assert.Contains("after MapPages()", ex.Message);
    }

    public static TheoryData<Action<PagePackageBuilder>, string[]> NoSuchFile => new()
    {
        { m => m.MapPageLoader("/nope", () => Page.Props()), ["No page of '@acme/help-center' matches '/nope' below its mount at /help", "Expected @acme/help-center/app/nope/page.tsx", "Its pages: /, /contact"] },
        { m => m.MapPageAction("nope", () => Page.Redirect("/")), ["matches 'nope'", "Expected @acme/help-center/app/nope/page.tsx"] },
        { m => m.MapLayoutLoader("app/nope", () => Page.Props()), ["No layout of '@acme/help-center' serves 'app/nope'", "Expected @acme/help-center/app/nope/layout.tsx"] },
        { m => m.MapLayoutLoader("/contact", () => Page.Props()), ["No layout of '@acme/help-center' serves '/contact'", "Expected @acme/help-center/app/contact/layout.tsx"] },
        { m => m.MapLayoutLoader("(center)", () => Page.Props()), ["starts with its pages folder, such as 'app' or 'app/(group)'"] },
    };

    [Theory]
    [MemberData(nameof(NoSuchFile))]
    public async Task ThroughTheMount_ALoaderForAFileThePackageLacks_FailsAtStartup_NamingThePackagesFile(Action<PagePackageBuilder> map, string[] message)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(["app/page.tsx"], ["layout.tsx", "page.tsx", "contact/page.tsx"], mount: map));

        Assert.All(message, part => Assert.Contains(part, ex.Message));
    }

    [Fact]
    public async Task Development_APackagesPageRemovedWhileItsLoaderIsMapped_IsReported_NamingThePackagesFile()
    {
        await using var app = await StartAsync(
            ["app/page.tsx"],
            ["page.tsx", "contact/page.tsx"],
            mount: m => m.MapPageLoader("/contact", () => Page.Props()));
        var package = Path.Combine(app.Root, "node_modules", "@acme", "help-center");

        File.Delete(Path.Combine(package, "app", "contact", "page.tsx"));
        LogEntry? error = null;
        for (var attempt = 0; attempt < 100 && error is null; attempt++)
        {
            await Task.Delay(100, Ct);
            await app.GetDataAsync("/help/contact");
            error = app.Logs.FirstOrDefault(e => e.Level >= LogLevel.Error && e.Exception?.Message.Contains("No page file matches '/help/contact'") == true);
        }

        Assert.NotNull(error);
        Assert.Contains("Expected @acme/help-center/app/contact/page.tsx, the page of '@acme/help-center' mounted at /help", error.Exception!.Message);
    }

    [Fact]
    public async Task TheMount_RefusesThePagesAndLayoutsOfAPackageMountedInsideIt()
    {
        foreach (var map in new Action<PagePackageBuilder>[]
                 {
                     m => m.MapPageLoader("/status", () => Page.Props()),
                     m => m.MapLayoutLoader("/status", () => Page.Props()),
                 })
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(
                ["app/page.tsx"],
                prepareRoot: root =>
                {
                    TestPagePackage.Install(root, ["page.tsx"]);
                    TestPagePackage.Install(root, ["layout.tsx", "page.tsx"], name: TestPagePackage.Other.Name);
                },
                map: a =>
                {
                    var help = a.MapPagePackage(TestPagePackage.Descriptor, "/help");
                    a.MapPagePackage(TestPagePackage.Other, "/help/status");
                    map(help);
                }));

            Assert.Contains("on the mount of '@acme/help-center' at /help", ex.Message);
            Assert.Contains("of the page package '@acme/status', not one of the package's", ex.Message);
        }
    }
}
