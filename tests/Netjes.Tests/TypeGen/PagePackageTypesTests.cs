using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Netjes.Routing;
using Netjes.Tests.Endpoints;
using Netjes.TypeGen;

namespace Netjes.Tests.TypeGen;

/// <summary>
/// The generated files of an app that mounts a page package: the app's own, which holds the package's routes but none
/// of its schemas, and the package's (<c>WriteTypes</c> on the mount), which holds them below the mount.
/// </summary>
public class PagePackageTypesTests
{
    public sealed record HomeProps(string Title);

    public sealed record ArticleProps(string Slug, string Title, Href Back);

    public sealed record Feedback(string? Comment);

    public sealed record CenterProps(IReadOnlyList<string> Sections);

    public sealed record Vote(string Article, bool Helpful);

    public sealed record VoteCount(int Helpful);

    public sealed record FeedbackNote(string Text);

    public sealed record FeedbackReceipt(string Id);

    public sealed record AppHomeProps(string Greeting);

    /// <summary>The package's pages, with a group layout around its articles.</summary>
    internal static readonly string[] PackageFiles = ["page.tsx", "contact/page.tsx", "(center)/layout.tsx", "(center)/articles/[slug]/page.tsx"];

    /// <summary>What the package's own extension maps through its mount.</summary>
    internal static void MapHelpCenter(PagePackageBuilder mount)
    {
        mount.MapPageLoader("/", () => Page.Props(new HomeProps("Help")));
        mount.MapPageLoader("/articles/[slug]", (string slug) => Page.Props(new ArticleProps(slug, "An article", new Href("/"))));
        mount.MapPageAction("/articles/[slug]", ([FromForm] Feedback feedback) => Page.Redirect("/"));
        mount.MapLayoutLoader("app/(center)", () => Page.Props(new CenterProps(["Getting started"])));
        mount.MapAction("help.vote", (Vote vote) => ServerAction.Ok(new VoteCount(1)));
        mount.MapPost("/feedback", (FeedbackNote note) => TypedResults.Ok(new FeedbackReceipt("1")));
        mount.MapGet("/articles/{slug}/feed", (string slug, int? take) => Results.Text(slug));
    }

    /// <summary>The app with the package installed and mounted at <paramref name="prefix"/>.</summary>
    internal static Task<TestApp> StartAsync(
        string[] appFiles,
        string prefix = "/help",
        Action<PagePackageBuilder>? mount = null,
        Action<WebApplication>? map = null,
        Action<NetjesOptions>? configure = null,
        Action<IServiceCollection>? services = null,
        Action<string>? prepareRoot = null) =>
        TestApp.StartAsync(
            appFiles,
            prepareRoot: root =>
            {
                TestPagePackage.Install(root, PackageFiles);
                prepareRoot?.Invoke(root);
            },
            map: a =>
            {
                map?.Invoke(a);
                (mount ?? MapHelpCenter)(a.MapPagePackage(TestPagePackage.Descriptor, prefix));
            },
            configure: configure,
            services: services);

    private static string AppFile(TestApp app) => app.App.Services.GetRequiredService<TypeScriptGenerator>().Generate();

    [Fact]
    public async Task TheAppsFile_LeavesOutThePackagesSchemasFormsLayoutsActionsAndEndpoints()
    {
        await using var app = await StartAsync(["app/page.tsx"], map: a => a.MapPageLoader("/", () => Page.Props(new AppHomeProps("Hi"))));

        var output = AppFile(app);

        Assert.Contains("export const AppHomeProps = z.object({", output);
        foreach (var name in new[] { " HomeProps", "ArticleProps", "Feedback", "CenterProps", "Vote", "help.vote", "/help/feedback", "layouts" })
        {
            Assert.DoesNotContain(name, output);
        }

        Assert.Contains("export const pages = {\n  \"/\": AppHomeProps,\n} as const;", output);
        Assert.Contains("export const forms = {} as const;", output);
        Assert.Contains("export const api = {} as const;", output);
        Assert.Contains("export type EndpointParams = Record<never, never>;", output);
    }

    [Fact]
    public async Task TheAppsFile_RoutesThePackagesPages_WithThePrefixsParametersFirst()
    {
        await using var app = await StartAsync(["app/page.tsx"], prefix: "/(marketing)/[tenant]/help");

        var output = AppFile(app);

        Assert.Contains(
            "export const routePatterns = [\"/\", \"/[tenant]/help\", \"/[tenant]/help/articles/[slug]\", \"/[tenant]/help/contact\"] as const;",
            output);
        Assert.Contains(
            """
            export type RouteParams = {
              "/": Record<string, never>;
              "/[tenant]/help": { tenant: string };
              "/[tenant]/help/articles/[slug]": { tenant: string; slug: string };
              "/[tenant]/help/contact": { tenant: string };
            };
            """.ReplaceLineEndings("\n"),
            output);
        Assert.Contains("export type Route = '/' | `/${string}/help` | `/${string}/help/articles/${string}` | `/${string}/help/contact`;", output);
        Assert.Contains("declare module 'netjes' {", output);
    }

    [Fact]
    public async Task TheAppsFile_KeepsTheAppsLoaderForItsPageInsideThePackagesFolder()
    {
        await using var app = await StartAsync(
            ["app/page.tsx", "app/help/(center)/shortcuts/page.tsx"],
            map: a => a.MapPageLoader("/help/shortcuts", () => Page.Props(new AppHomeProps("Keys"))));

        var output = AppFile(app);

        Assert.Contains("export const pages = {\n  \"/\": z.object({}),\n  \"/help/shortcuts\": AppHomeProps,\n} as const;", output);
        Assert.Contains("\"/help/shortcuts\": Record<string, never>;", output);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Where the tests write the package's file, relative to the app: a folder of the package's, as a workspace has it.</summary>
    private const string PackageTypes = "packages/help-center/generated/netjes.ts";

    private static string PackageTypesPath(TestApp app) => Path.Combine(app.Root, "packages", "help-center", "generated", "netjes.ts");

    private static Action<PagePackageBuilder> WithTypes(Action<PagePackageBuilder>? mount = null) =>
        m => (mount ?? MapHelpCenter)(m.WriteTypes(PackageTypes));

    [Fact]
    public async Task WriteTypes_InDevelopment_WritesThePackagesFile_KeyedBelowTheMount()
    {
        await using var app = await StartAsync(["app/page.tsx"], mount: WithTypes());

        var output = await File.ReadAllTextAsync(PackageTypesPath(app), Ct);

        Assert.Contains("export const routePatterns = [\"/\", \"/articles/[slug]\", \"/contact\"] as const;", output);
        Assert.Contains("export const pages = {\n  \"/\": HomeProps,\n  \"/articles/[slug]\": ArticleProps,\n  \"/contact\": z.object({}),\n} as const;", output);
        Assert.DoesNotContain("declare module", output);
        Assert.DoesNotContain("AppHomeProps", output);
    }

    private static string PackageFile(TestApp app) =>
        app.App.Services.GetRequiredService<TypeScriptGenerator>().Generate(app.App.Services.GetRequiredService<RouteSources>().Mounts.Single());

    [Fact]
    public async Task ThePackagesFile_IsTheSameWhereverTheAppMountsThePackage()
    {
        var files = new List<string>();
        foreach (var prefix in new[] { "/help", "/(marketing)/help", "/[tenant]/help", "/support/(desk)/[tenant]/help", "/" })
        {
            await using var app = await StartAsync(prefix == "/" ? [] : ["app/page.tsx"], prefix: prefix);
            files.Add(PackageFile(app));
        }

        Assert.All(files, file => Assert.Equal(files[0], file));
        Assert.DoesNotContain("tenant", files[0]);
        Assert.Contains("\"GET /articles/{slug}/feed\": \"/articles/{slug}/feed\",", files[0]);
    }

    private static void Write(string root, string file, string content)
    {
        var path = Path.Combine(root, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public async Task Development_RewritesAStalePackageFile()
    {
        await using var app = await StartAsync(["app/page.tsx"], mount: WithTypes(), prepareRoot: root => Write(root, PackageTypes, "// stale\n"));

        Assert.Equal(PackageFile(app), await File.ReadAllTextAsync(PackageTypesPath(app), Ct));
        Assert.Contains(app.Logs, l => l.Message == "Wrote packages/help-center/generated/netjes.ts".Replace('/', Path.DirectorySeparatorChar));
    }

    [Fact]
    public async Task Development_WithTypesFileNull_WritesNoPackagesFileEither()
    {
        // What a test host sets, so that a test run rewrites no committed file.
        await using var app = await StartAsync(["app/page.tsx"], mount: WithTypes(), configure: o => o.TypesFile = null);

        Assert.False(File.Exists(PackageTypesPath(app)));
    }

    [Fact]
    public async Task GenerateCommand_WritesTheAppsFileAndThePackagesFile()
    {
        int? exit = null;
        await using var app = await StartAsync(
            ["app/page.tsx"],
            mount: WithTypes(),
            configure: o => o.Command = new NetjesCommandOptions { Name = "generate" },
            services: s => s.AddSingleton(new CommandExit(code => exit = code)));

        Assert.Equal(0, exit);
        Assert.Equal(AppFile(app), await File.ReadAllTextAsync(Path.Combine(app.Root, "generated", "netjes.ts"), Ct));
        Assert.Equal(PackageFile(app), await File.ReadAllTextAsync(PackageTypesPath(app), Ct));
    }

    [Fact]
    public async Task GenerateCommand_WithTypesFileNull_WritesNoPackagesFile_AndExits2()
    {
        int? exit = null;
        await using var app = await StartAsync(
            ["app/page.tsx"],
            mount: WithTypes(),
            configure: o =>
            {
                o.TypesFile = null;
                o.Command = new NetjesCommandOptions { Name = "generate" };
            },
            services: s => s.AddSingleton(new CommandExit(code => exit = code)));

        Assert.Equal(2, exit);
        Assert.False(File.Exists(PackageTypesPath(app)));
    }

    /// <summary>The app's file and the package's, as the generator writes them.</summary>
    private static async Task<(string App, string Package)> CurrentFilesAsync()
    {
        await using var reference = await StartAsync(["app/page.tsx"], mount: WithTypes(), configure: o => o.TypesFile = null);
        return (AppFile(reference), PackageFile(reference));
    }

    [Theory]
    [InlineData("current", 0)]
    [InlineData("stale", 1)]
    [InlineData("missing", 1)]
    public async Task GenerateCheck_ChecksThePackagesFile_NamingItWhenItIsNotCurrent(string state, int expectedExit)
    {
        var (appFile, packageFile) = await CurrentFilesAsync();
        int? exit = null;
        await using var app = await StartAsync(
            ["app/page.tsx"],
            mount: WithTypes(),
            configure: o => o.Command = new NetjesCommandOptions { Name = "generate", Check = true },
            services: s => s.AddSingleton(new CommandExit(code => exit = code)),
            prepareRoot: root =>
            {
                Write(root, "generated/netjes.ts", appFile);
                if (state != "missing")
                {
                    Write(root, PackageTypes, state == "current" ? packageFile : packageFile.Replace("\"/contact\": z.object({})", "\"/contact\": z.never()"));
                }
            });

        Assert.Equal(expectedExit, exit);
        var errors = app.Logs.Where(l => l.Level == Microsoft.Extensions.Logging.LogLevel.Error).Select(l => l.Message).ToList();
        if (state == "current")
        {
            Assert.Empty(errors);
            return;
        }

        var error = Assert.Single(errors);
        Assert.StartsWith(PackageTypesPath(app), error);
        Assert.Contains(state == "stale" ? "is out of date; run `netjes generate`" : "does not exist; run `netjes generate`", error);
        if (state == "stale")
        {
            Assert.Contains("+ ", error);
            Assert.Contains("\"/contact\": z.object({})", error);
        }

        // --check writes nothing.
        Assert.Equal(state == "missing", !File.Exists(PackageTypesPath(app)));
    }

    [Fact]
    public async Task GenerateCheck_NamesEveryFileThatIsNotCurrent()
    {
        int? exit = null;
        await using var app = await StartAsync(
            ["app/page.tsx"],
            mount: WithTypes(),
            configure: o => o.Command = new NetjesCommandOptions { Name = "generate", Check = true },
            services: s => s.AddSingleton(new CommandExit(code => exit = code)),
            prepareRoot: root => Write(root, "generated/netjes.ts", "// stale\n"));

        Assert.Equal(1, exit);
        var errors = app.Logs.Where(l => l.Level == Microsoft.Extensions.Logging.LogLevel.Error).Select(l => l.Message).ToList();
        Assert.Equal(2, errors.Count);
        Assert.StartsWith($"{Path.Combine(app.Root, "generated", "netjes.ts")} is out of date", errors[0]);
        Assert.Equal($"{PackageTypesPath(app)} does not exist; run `netjes generate`", errors[1]);
    }

    public sealed record ContactProps(string Email);

    [Fact]
    public async Task AnOverriddenPage_IsTyped_InThePackagesFile_WhoseLoaderItRendersWith()
    {
        await using var app = await StartAsync(
            ["app/page.tsx", "app/help/contact/page.tsx"],
            mount: m =>
            {
                MapHelpCenter(m);
                m.MapPageLoader("/contact", () => Page.Props(new ContactProps("help@example.com")));
                m.Override("/help/contact");
            });

        var package = PackageFile(app);
        var appFile = AppFile(app);

        Assert.Contains("  \"/contact\": ContactProps,\n", package);
        Assert.DoesNotContain("ContactProps", appFile);
        Assert.Contains("export const pages = {\n  \"/\": z.object({}),\n} as const;", appFile);
        Assert.Contains("\"/help/contact\": Record<string, never>;", appFile);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("generated/netjes.ts")]
    [InlineData("./generated/../generated/netjes.ts")]
    [InlineData("GENERATED/netjes.ts")]
    public async Task WriteTypes_RefusesNoPathAndTheAppsOwnFile(string path)
    {
        var failure = await Assert.ThrowsAsync<ArgumentException>(() => StartAsync(["app/page.tsx"], mount: m => m.WriteTypes(path)));

        Assert.Equal("path", failure.ParamName);
        Assert.Contains("WriteTypes", failure.Message);
        Assert.Contains("'@acme/help-center'", failure.Message);
        Assert.Contains(path.Trim().Length == 0 ? "needs the path of a file" : "the app's own generated file", failure.Message);
    }

    [Fact]
    public async Task WriteTypes_RefusesTheFileOfAnotherPackage()
    {
        var failure = await Assert.ThrowsAsync<ArgumentException>(() => TestApp.StartAsync(
            ["app/page.tsx"],
            prepareRoot: root =>
            {
                TestPagePackage.Install(root, ["page.tsx"]);
                TestPagePackage.Install(root, ["page.tsx"], name: TestPagePackage.Other.Name);
            },
            map: a =>
            {
                a.MapPagePackage(TestPagePackage.Descriptor, "/help").WriteTypes("types/netjes.ts");
                a.MapPagePackage(TestPagePackage.Other, "/status").WriteTypes("types/../types/netjes.ts");
            }));

        Assert.Contains("on the mount of '@acme/status' names the generated file of '@acme/help-center'", failure.Message);
    }

    public sealed record AppShellProps(string Site);

    public sealed record SaveInput(string Text);

    public sealed record StatusProps(bool Up);

    public sealed record Ping(string At);

    [Fact]
    public async Task ThePackagesFile_HoldsNoneOfTheAppsOrAnotherPackagesPagesSchemasLayoutsActionsOrEndpoints()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx"],
            prepareRoot: root =>
            {
                TestPagePackage.Install(root, PackageFiles);
                TestPagePackage.Install(root, ["page.tsx"], name: TestPagePackage.Other.Name);
            },
            map: a =>
            {
                a.MapLayoutLoader("/", () => Page.Props(new AppShellProps("Shop")));
                a.MapPageLoader("/", () => Page.Props(new AppHomeProps("Hi")));
                a.MapAction("app.save", (SaveInput input) => ServerAction.Ok());
                // The app's own endpoint at a URL below the package's mount.
                a.MapGet("/help/status", () => TypedResults.Ok(new Ping("now")));
                MapHelpCenter(a.MapPagePackage(TestPagePackage.Descriptor, "/help"));
                var status = a.MapPagePackage(TestPagePackage.Other, "/status");
                status.MapPageLoader("/", () => Page.Props(new StatusProps(true)));
                status.MapAction("status.check", () => ServerAction.Ok());
                status.MapGet("/ping", () => TypedResults.Ok(new Ping("now")));
            });
        var help = app.App.Services.GetRequiredService<RouteSources>().Mounts.Single(m => m.Name == TestPagePackage.Name);

        var package = app.App.Services.GetRequiredService<TypeScriptGenerator>().Generate(help);

        Assert.Contains("export const routePatterns = [\"/\", \"/articles/[slug]\", \"/contact\"] as const;", package);
        foreach (var name in new[] { "AppShellProps", "AppHomeProps", "SaveInput", "StatusProps", "Ping", "app.save", "status.check", "/status", "/ping", "\"app\"" })
        {
            Assert.DoesNotContain(name, package);
        }
    }

    [Fact]
    public async Task WriteTypes_RefusesNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => StartAsync(["app/page.tsx"], mount: m => m.WriteTypes(null!)));
    }

    /// <summary>Types named like the exports of a page package's file, which an app's file does not have.</summary>
    public static class Named
    {
        public sealed record Link(string Title);

        public sealed record MountHref(string Url);

        public sealed record LinksProps(Link Link, MountHref Next);
    }

    [Fact]
    public async Task ThePackagesFile_RenamesATypeNamedLikeOneOfItsExports_AndTheAppsFileDoesNot()
    {
        await using var app = await StartAsync(
            ["app/links/page.tsx"],
            map: a => a.MapPageLoader("/links", () => Page.Props(new Named.LinksProps(new("Home"), new("/")))),
            mount: m => m.MapPageLoader("/contact", () => Page.Props(new Named.LinksProps(new("Help"), new("/")))));

        var package = PackageFile(app);
        var appFile = AppFile(app);

        Assert.Contains("export const NamedLink = z.object({", package);
        Assert.Contains("export const NamedMountHref = z.object({", package);
        Assert.DoesNotContain("export const Link = z", package);
        Assert.Contains("export const { Link, Form, SubmitButton, useRouter, usePathname, useHref } = mounted<MountHref>(", package);
        Assert.Contains("export const Link = z.object({", appFile);
        Assert.Contains("export const MountHref = z.object({", appFile);
    }

    /// <summary>The app's own type of the same name as one of the package's.</summary>
    public static class AppTypes
    {
        public sealed record HomeProps(string Greeting, int Visits);

        public sealed record HeroBlock(string Heading);
    }

    [Fact]
    public async Task TheAppsFile_RenamesNoneOfTheAppsSchemas_ForAPackagesTypeOfTheSameName()
    {
        await using var app = await StartAsync(["app/page.tsx"], map: a => a.MapPageLoader("/", () => Page.Props(new AppTypes.HomeProps("Hi", 1))));

        var output = AppFile(app);

        Assert.Contains("export const HomeProps = z.object({\n  greeting: z.string(),\n  visits: z.number().int(),\n});", output);
        Assert.Contains("export const pages = {\n  \"/\": HomeProps,\n} as const;", output);
    }

    public sealed record HeroBlock(string Title, Href Link);

    public sealed record QuoteBlock(string Text);

    /// <summary>The blocks a package adds to both files, as a package of page blocks would.</summary>
    internal static void MapWithBlocks(PagePackageBuilder mount)
    {
        MapHelpCenter(mount);
        mount.AddTypeScriptExport("blocks", "quote", new Dictionary<string, Type> { ["props"] = typeof(QuoteBlock) });
        mount.AddTypeScriptExport("blocks", "hero", new Dictionary<string, Type> { ["props"] = typeof(HeroBlock) });
    }

    [Fact]
    public async Task AnExportAddedThroughTheMount_IsInTheAppsFileAndThePackagesFile()
    {
        await using var app = await StartAsync(["app/page.tsx"], mount: MapWithBlocks);

        foreach (var output in new[] { AppFile(app), PackageFile(app) })
        {
            Assert.Contains("export const HeroBlock = z.object({\n  title: z.string(),\n  link: Href,\n});", output);
            Assert.Contains("export const blocks = {\n  \"hero\": { \"props\": HeroBlock },\n  \"quote\": { \"props\": QuoteBlock },\n} as const;", output);
        }
    }

    [Fact]
    public async Task AnExportAddedThroughTheMount_RenamesNoneOfTheAppsSchemas_ForASlotsTypeOfTheSameName()
    {
        // The export's schemas are in the app's file too, where the app's own `HeroBlock` was there first.
        await using var app = await StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props(new AppTypes.HeroBlock("Hi"))),
            mount: MapWithBlocks);

        var output = AppFile(app);
        var package = PackageFile(app);

        Assert.Contains("export const HeroBlock = z.object({\n  heading: z.string(),\n});", output);
        Assert.Contains("export const pages = {\n  \"/\": HeroBlock,\n} as const;", output);
        Assert.Contains("export const PagePackageTypesTestsHeroBlock = z.object({\n  title: z.string(),\n  link: Href,\n});", output);
        Assert.Contains("  \"hero\": { \"props\": PagePackageTypesTestsHeroBlock },\n", output);
        Assert.Contains("export const HeroBlock = z.object({\n  title: z.string(),\n  link: Href,\n});", package);
        Assert.Contains("  \"hero\": { \"props\": HeroBlock },\n", package);
    }

    [Fact]
    public async Task AnExportAddedThroughOneMount_IsInNoOtherPackagesFile_AndTheAppsOwnIsInNoPackagesFile()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            prepareRoot: root =>
            {
                TestPagePackage.Install(root, PackageFiles);
                TestPagePackage.Install(root, ["page.tsx"], name: TestPagePackage.Other.Name);
            },
            map: a =>
            {
                a.Services.GetRequiredService<TypeGenRegistry>().Add("appBlocks", "banner", new Dictionary<string, Type> { ["props"] = typeof(QuoteBlock) });
                MapWithBlocks(a.MapPagePackage(TestPagePackage.Descriptor, "/help"));
                a.MapPagePackage(TestPagePackage.Other, "/status")
                    .AddTypeScriptExport("checks", "ping", new Dictionary<string, Type> { ["result"] = typeof(StatusProps) });
            });
        var mounts = app.App.Services.GetRequiredService<RouteSources>().Mounts;
        var generator = app.App.Services.GetRequiredService<TypeScriptGenerator>();

        var help = generator.Generate(mounts.Single(m => m.Name == TestPagePackage.Name));
        var status = generator.Generate(mounts.Single(m => m.Name == TestPagePackage.Other.Name));
        var appFile = AppFile(app);

        Assert.Contains("export const blocks = {", help);
        Assert.DoesNotContain("export const checks", help);
        Assert.DoesNotContain("appBlocks", help);
        Assert.Contains("export const checks = {\n  \"ping\": { \"result\": StatusProps },\n} as const;", status);
        Assert.DoesNotContain("export const blocks", status);
        Assert.DoesNotContain("appBlocks", status);
        Assert.DoesNotContain("QuoteBlock", status);
        foreach (var name in new[] { "export const appBlocks = {", "export const blocks = {", "export const checks = {" })
        {
            Assert.Contains(name, appFile);
        }
    }

    [Fact]
    public async Task AnExportAddedThroughTwoMounts_StopsTheApp_NamingBothPackages()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(
            ["app/page.tsx"],
            prepareRoot: root =>
            {
                TestPagePackage.Install(root, PackageFiles);
                TestPagePackage.Install(root, ["page.tsx"], name: TestPagePackage.Other.Name);
            },
            map: a =>
            {
                MapWithBlocks(a.MapPagePackage(TestPagePackage.Descriptor, "/help"));
                a.MapPagePackage(TestPagePackage.Other, "/status")
                    .AddTypeScriptExport("blocks", "chart", new Dictionary<string, Type> { ["props"] = typeof(StatusProps) });
            }));

        Assert.Equal(
            "'@acme/status' cannot add 'chart' to the TypeScript export 'blocks', which '@acme/help-center' added. Give the export a name of its own.",
            error.Message);
    }

    [Theory]
    [InlineData("Link")]
    [InlineData("useRouter")]
    [InlineData("MountHref")]
    public async Task AnExportThroughTheMount_CannotTakeANameOfThePackagesFile(string group)
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => StartAsync(
            ["app/page.tsx"],
            mount: m => m.AddTypeScriptExport(group, "hero", new Dictionary<string, Type> { ["props"] = typeof(HeroBlock) })));

        Assert.StartsWith($"'{group}' cannot be the name of a TypeScript export: a page package's generated file declares", error.Message);
    }

    [Fact]
    public async Task GenerateCheck_NoticesAnExportAddedThroughTheMount_InBothFiles()
    {
        var (appFile, packageFile) = await CurrentFilesAsync();
        int? exit = null;
        await using var app = await StartAsync(
            ["app/page.tsx"],
            mount: WithTypes(MapWithBlocks),
            configure: o => o.Command = new NetjesCommandOptions { Name = "generate", Check = true },
            services: s => s.AddSingleton(new CommandExit(code => exit = code)),
            prepareRoot: root =>
            {
                Write(root, "generated/netjes.ts", appFile);
                Write(root, PackageTypes, packageFile);
            });

        Assert.Equal(1, exit);
        var errors = app.Logs.Where(l => l.Level == Microsoft.Extensions.Logging.LogLevel.Error).Select(l => l.Message).ToList();
        Assert.Equal(2, errors.Count);
        Assert.All(errors, error => Assert.Contains(": export const HeroBlock = z.object({", error));
        Assert.StartsWith($"{Path.Combine(app.Root, "generated", "netjes.ts")} is out of date", errors[0]);
        Assert.StartsWith($"{PackageTypesPath(app)} is out of date", errors[1]);
    }
}
