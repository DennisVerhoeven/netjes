using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Netjes.Routing;

namespace Netjes.Tests.Endpoints;

/// <summary>
/// A page package mounted with <c>MapPagePackage</c>: its pages merge into the app's route table under the prefix, from
/// <c>node_modules</c> in development and from the client build's manifest and record in production.
/// </summary>
public class PagePackageTests
{
    private const string Help = TestPagePackage.Name;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Development_APackagesPage_AnswersUnderItsMount()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            prepareRoot: root => TestPagePackage.Install(root, ["page.tsx", "contact/page.tsx"]),
            map: a => a.MapPagePackage(TestPagePackage.Descriptor, "/help"));

        Assert.Equal($"{Help}/app/contact/page.tsx", (string?)(await app.GetDataJsonAsync("/help/contact"))["page"]);
        Assert.Equal($"{Help}/app/page.tsx", (string?)(await app.GetDataJsonAsync("/help"))["page"]);
        Assert.Equal("app/page.tsx", (string?)(await app.GetDataJsonAsync("/"))["page"]);
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetDataAsync("/contact")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Development_AFileAddedToThePackage_IsRoutableWithoutARestart(bool linked)
    {
        string? package = null;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            prepareRoot: root => package = linked
                ? TestPagePackage.Link(root, ["page.tsx", "contact/page.tsx"])
                : TestPagePackage.Install(root, ["page.tsx", "contact/page.tsx"]),
            map: a => a.MapPagePackage(TestPagePackage.Descriptor, "/help"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetDataAsync("/help/fresh")).StatusCode);

        Directory.CreateDirectory(Path.Combine(package!, "app", "fresh"));
        await File.WriteAllTextAsync(Path.Combine(package!, "app", "fresh", "page.tsx"), "export default () => null", Ct);

        var status = HttpStatusCode.NotFound;
        for (var attempt = 0; attempt < 50 && status != HttpStatusCode.OK; attempt++)
        {
            await Task.Delay(100, Ct);
            status = (await app.GetDataAsync("/help/fresh")).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal($"{Help}/app/fresh/page.tsx", (string?)(await app.GetDataJsonAsync("/help/fresh"))["page"]);
    }

    [Theory]
    [InlineData("routes")]
    [InlineData("generate")]
    public async Task Commands_ScanTheMountedPackage_BeforeAnyBuild(string command)
    {
        var output = Path.Combine(Path.GetTempPath(), $"netjes-{Guid.NewGuid():N}.out");
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            environment: "Production",
            prepareRoot: root => TestPagePackage.Install(root, ["page.tsx", "articles/[slug]/page.tsx"]),
            map: a => a.MapPagePackage(TestPagePackage.Descriptor, "/help"),
            configure: o => o.Command = new NetjesCommandOptions { Name = command, Output = output },
            services: s => s.AddSingleton(new Netjes.TypeGen.CommandExit(_ => { })));

        var written = await File.ReadAllTextAsync(output, Ct);
        File.Delete(output);
        if (command == "routes")
        {
            var routes = JsonNode.Parse(written)!["routes"]!.AsArray();
            Assert.Contains(routes, r => (string?)r!["pattern"] == "/help/articles/[slug]" && (string?)r["file"] == $"{Help}/app/articles/[slug]/page.tsx");
        }
        else
        {
            Assert.Contains("\"/help/articles/[slug]\"", written);
        }
    }

    [Fact]
    public async Task Production_RoutesComeFromTheManifestAndTheRecord_AndAssetsByEachFilesManifestKey()
    {
        await using var app = await TestApp.StartAsync(
            [],
            environment: "Production",
            prepareRoot: root => TestPagePackage.WriteBuild(root, ["app/page.tsx"], [new(Help, ["page.tsx", "contact/page.tsx"])]),
            map: a => a.MapPagePackage(TestPagePackage.Descriptor, "/help"));

        Assert.Equal($"{Help}/app/contact/page.tsx", (string?)(await app.GetDataJsonAsync("/help/contact"))["page"]);
        Assert.Equal("app/page.tsx", (string?)(await app.GetDataJsonAsync("/"))["page"]);
        await app.GetDocumentAsync("/help/contact");
        var assets = app.Renderer.LastPayload["document"]!["assets"]!;
        // The contact page's chunk is the manifest's third module.
        Assert.Contains("/assets/module-2.js", assets["modulePreloads"]!.AsArray().Select(n => (string?)n));
        Assert.Contains("/assets/module-2.css", assets["stylesheets"]!.AsArray().Select(n => (string?)n));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_APackageMissingFromTheRecord_StopsStartup_NamingTheFix(bool otherPackageBuilt)
    {
        var ex = await Assert.ThrowsAsync<NetjesStartupException>(() => TestApp.StartAsync(
            [],
            environment: "Production",
            prepareRoot: root => TestPagePackage.WriteBuild(
                root, ["app/page.tsx"], otherPackageBuilt ? [new(TestPagePackage.Other.Name, ["page.tsx"])] : []),
            map: a => a.MapPagePackage(TestPagePackage.Descriptor, "/help")));

        Assert.Equal(
            $"'{Help}' is mounted but not in the client build: add it to netjes({{ packages }}) in vite.config.ts and build again",
            ex.Message);
    }

    [Fact]
    public async Task Production_AFileInTheRecordWithoutAManifestKey_StopsStartup()
    {
        var ex = await Assert.ThrowsAsync<NetjesStartupException>(() => TestApp.StartAsync(
            [],
            environment: "Production",
            prepareRoot: root => TestPagePackage.WriteBuild(
                root, ["app/page.tsx"], [new(Help, ["page.tsx", "contact/page.tsx"])], unbuilt: [$"{Help}/app/contact/page.tsx"]),
            map: a => a.MapPagePackage(TestPagePackage.Descriptor, "/help")));

        Assert.Contains($"'{Help}/app/contact/page.tsx'", ex.Message);
        Assert.Contains($"node_modules/{Help}/app/contact/page.tsx", ex.Message);
        Assert.Contains("npm run build", ex.Message);
    }

    [Fact]
    public async Task Development_APackageThatIsNotInstalled_StopsStartup_NamingTheVersionToInstall()
    {
        var ex = await Assert.ThrowsAsync<NetjesStartupException>(() => TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPagePackage(TestPagePackage.Descriptor, "/help")));

        Assert.Equal($"'{Help}' is mounted but not installed: npm install {Help}@{TestPagePackage.Descriptor.Version}", ex.Message);
    }

    /// <summary>The app with the package mounted at <paramref name="prefix"/>: installed in Development, built in Production.</summary>
    private static Task<TestApp> StartWithPackageAsync(
        string environment,
        string[] appFiles,
        string[] packageFiles,
        string prefix = "/help",
        Action<WebApplication>? map = null,
        Action<PagePackageBuilder>? mount = null,
        Action<IEndpointConventionBuilder>? pages = null,
        Action<IServiceCollection>? services = null,
        Action<WebApplication>? configureApp = null) =>
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
                var mounted = a.MapPagePackage(TestPagePackage.Descriptor, prefix);
                mount?.Invoke(mounted);
                map?.Invoke(a);
            },
            pages: pages,
            services: services,
            configureApp: configureApp);

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task AnAppPageAgainstAPackagePage_FailsAtMapPages_NamingBothAndTheOverride(string environment)
    {
        var ex = await Assert.ThrowsAsync<RouteConflictException>(() => StartWithPackageAsync(
            environment, ["app/page.tsx", "app/help/contact/page.tsx"], ["page.tsx", "contact/page.tsx"]));

        Assert.Contains("'app/help/contact/page.tsx'", ex.Message);
        Assert.Contains($"'{Help}/app/contact/page.tsx'", ex.Message);
        Assert.Contains(".Override(\"/help/contact\") on its mount", ex.Message);
        Assert.Contains(nameof(NetjesEndpointRouteBuilderExtensions.MapPages), ex.StackTrace);
    }

    [Fact]
    public async Task AnAppLoaderForAContestedPage_BeforeMapPages_ThrowsTheConflict()
    {
        var ex = await Assert.ThrowsAsync<RouteConflictException>(() => StartWithPackageAsync(
            "Development",
            ["app/help/contact/page.tsx"],
            ["contact/page.tsx"],
            map: a => a.MapPageLoader("/help/contact", () => Page.Props(new { })),
            pages: _ => Assert.Fail("MapPages() ran.")));

        Assert.Contains("'app/help/contact/page.tsx'", ex.Message);
        Assert.Contains($"'{Help}/app/contact/page.tsx'", ex.Message);
        Assert.Contains(nameof(NetjesEndpointRouteBuilderExtensions.MapPageLoader), ex.StackTrace);
    }

    [Fact]
    public async Task AnAppLayoutLoaderAtAContestedPagesUrl_BeforeMapPages_ThrowsTheConflict()
    {
        var ex = await Assert.ThrowsAsync<RouteConflictException>(() => StartWithPackageAsync(
            "Development",
            ["app/help/page.tsx"],
            ["layout.tsx", "page.tsx"],
            map: a => a.MapLayoutLoader("/help", () => Page.Props(new { })),
            pages: _ => Assert.Fail("MapPages() ran.")));

        Assert.Contains("'app/help/page.tsx'", ex.Message);
        Assert.Contains($"'{Help}/app/page.tsx'", ex.Message);
        Assert.Contains(nameof(NetjesEndpointRouteBuilderExtensions.MapLayoutLoader), ex.StackTrace);
    }

    [Fact]
    public async Task Development_AConflictWithAPackagePageAddedWhileRunning_IsLoggedAndThePreviousRoutesAnswer()
    {
        await using var app = await StartWithPackageAsync("Development", ["app/page.tsx", "app/help/about/page.tsx"], ["contact/page.tsx"]);

        // Below a folder the watcher already follows.
        Directory.CreateDirectory(Path.Combine(app.Root, "app", "help", "contact"));
        await File.WriteAllTextAsync(Path.Combine(app.Root, "app", "help", "contact", "page.tsx"), "export default () => null", Ct);
        LogEntry? error = null;
        for (var attempt = 0; attempt < 100 && error is null; attempt++)
        {
            await Task.Delay(100, Ct);
            error = app.Logs.FirstOrDefault(e => e.Category == typeof(FileSystemRouteTableProvider).FullName && e.Level == LogLevel.Error);
        }

        Assert.NotNull(error);
        Assert.Contains("Keeping the previous routes", error.Message);
        Assert.Contains($"'{Help}/app/contact/page.tsx' and 'app/help/contact/page.tsx'", error.Message);
        Assert.Equal($"{Help}/app/contact/page.tsx", (string?)(await app.GetDataJsonAsync("/help/contact"))["page"]);
    }

    [Fact]
    public async Task MapPagePackage_InsideAMapGroup_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(
            ["app/page.tsx"],
            prepareRoot: root => TestPagePackage.Install(root, ["page.tsx"]),
            map: a => a.MapGroup("/support").MapPagePackage(TestPagePackage.Descriptor, "/help")));

        Assert.Contains($"'{Help}'", ex.Message);
        Assert.Contains("MapGroup", ex.Message);
        Assert.Contains("app.MapPagePackage", ex.Message);
    }

    [Fact]
    public async Task MapPagePackage_AfterMapPages_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(
            ["app/page.tsx"],
            prepareRoot: root => TestPagePackage.Install(root, ["page.tsx"]),
            map: a =>
            {
                a.MapPages();
                a.MapPagePackage(TestPagePackage.Descriptor, "/help");
            }));

        Assert.Contains($"'{Help}'", ex.Message);
        Assert.Contains("before app.MapPages()", ex.Message);
    }

    [Fact]
    public async Task MapPagePackage_ASecondMountOfOnePackage_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(
            ["app/page.tsx"],
            prepareRoot: root => TestPagePackage.Install(root, ["page.tsx"]),
            map: a =>
            {
                a.MapPagePackage(TestPagePackage.Descriptor, "/help");
                a.MapPagePackage(TestPagePackage.Descriptor, "/support");
            }));

        Assert.Contains($"'{Help}' is mounted already, at /help", ex.Message);
    }

    private static void Nobody(IServiceCollection services) =>
        services.AddAuthorization().AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, NobodyHandler>("Test", null);

    private sealed class NobodyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }

    private sealed record Marker(string Name);

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task ConventionsOnTheMount_ProtectThePackagesFilePages_ButNotTheAppsPagesUnderThePrefix(string environment)
    {
        await using var app = await StartWithPackageAsync(
            environment,
            ["app/layout.tsx", "app/unauthorized.tsx", "app/help/(center)/shortcuts/page.tsx"],
            ["page.tsx", "(center)/layout.tsx", "(center)/articles/[slug]/page.tsx"],
            mount: m => m.RequireAuthorization(),
            services: Nobody,
            configureApp: a => a.UseAuthorization());

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetDocumentAsync("/help")).StatusCode);
        Assert.Equal("app/unauthorized.tsx", (string?)app.Renderer.LastPayload["page"]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetDataAsync("/help/articles/x")).StatusCode);
        // The app's own page in the package's folder is the app's to protect: the scope the guide documents.
        Assert.Equal(HttpStatusCode.OK, (await app.GetDataAsync("/help/shortcuts")).StatusCode);
    }

    [Fact]
    public async Task ConventionsOfMapPages_ReachThePackagesFilePages_AfterTheMounts()
    {
        await using var app = await StartWithPackageAsync(
            "Development",
            ["app/page.tsx"],
            ["page.tsx"],
            mount: m => m.WithMetadata(new Marker("mount")),
            pages: p => p.WithMetadata(new Marker("pages")));

        var endpoint = app.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Single(e => e.Metadata.GetMetadata<PageRouteMetadata>()?.Pattern == "/help");
        var appPage = app.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Single(e => e.Metadata.GetMetadata<PageRouteMetadata>()?.Pattern == "/");

        Assert.Equal(["mount", "pages"], endpoint.Metadata.GetOrderedMetadata<Marker>().Select(m => m.Name));
        Assert.Equal(["pages"], appPage.Metadata.GetOrderedMetadata<Marker>().Select(m => m.Name));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task AnAppLayoutLoaderAboveTheMount_ProtectsThePackagesFilePages(string environment)
    {
        var calls = 0;
        await using var app = await StartWithPackageAsync(
            environment,
            ["app/layout.tsx", "app/unauthorized.tsx", "app/members/layout.tsx", "app/members/page.tsx"],
            ["page.tsx", "contact/page.tsx"],
            prefix: "/members/help",
            map: a => a.MapLayoutLoader("/members", () =>
            {
                calls++;
                return Page.Props(new { Secret = "s3cret" });
            }).RequireAuthorization(),
            services: Nobody,
            configureApp: a => a.UseAuthorization());

        foreach (var url in new[] { "/members/help", "/members/help/contact", "/MEMBERS/Help/contact" })
        {
            var response = await app.GetDataAsync(url);
            var body = await response.Content.ReadAsStringAsync(Ct);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("app/unauthorized.tsx", (string?)JsonNode.Parse(body)!["page"]);
            Assert.DoesNotContain("s3cret", body);
        }

        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("pages", "../app")]
    [InlineData("pages", "app/../../x")]
    [InlineData("pages", "/app")]
    [InlineData("pages", "C:/app")]
    [InlineData("pages", "app\\x")]
    [InlineData("pages", ".")]
    [InlineData("pages", "")]
    [InlineData("sources", "../src")]
    [InlineData("sources", "/src")]
    [InlineData("sources", "C:\\src")]
    [InlineData("sources", "src\\x")]
    [InlineData("css", "../styles/x.css")]
    [InlineData("css", "/styles/x.css")]
    [InlineData("css", "D:styles/x.css")]
    [InlineData("css", "styles\\x.css")]
    public async Task Development_APackageWhosePathsLeaveIt_StopsStartup(string key, string value)
    {
        var field = new JsonObject { ["pages"] = "app" };
        field[key] = key == "sources" ? new JsonArray("app", value) : value;

        var ex = await Assert.ThrowsAsync<NetjesStartupException>(() => TestApp.StartAsync(
            ["app/page.tsx"],
            prepareRoot: root => TestPagePackage.Install(root, ["page.tsx"], netjes: field),
            map: a => a.MapPagePackage(TestPagePackage.Descriptor, "/help")));

        Assert.StartsWith($"'{Help}' has \"{key}\": {JsonValue.Create(value).ToJsonString()} in the \"netjes\" field of its package.json", ex.Message);
    }

    [Fact]
    public async Task Development_APackageWithoutTheNetjesField_StopsStartup()
    {
        var ex = await Assert.ThrowsAsync<NetjesStartupException>(() => TestApp.StartAsync(
            ["app/page.tsx"],
            prepareRoot: root =>
            {
                var dir = TestPagePackage.Install(root, ["page.tsx"]);
                File.WriteAllText(Path.Combine(dir, "package.json"), $$"""{ "name": "{{Help}}", "version": "1.0.0" }""");
            },
            map: a => a.MapPagePackage(TestPagePackage.Descriptor, "/help")));

        Assert.Equal($"'{Help}' is mounted, but it is not a page package: its package.json has no \"netjes\" field.", ex.Message);
    }
}
