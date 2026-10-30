using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netjes.Tests.Endpoints;

namespace Netjes.Tests.Routing;

public class RouteDiagnosticsTests
{
    private const string Category = "Netjes.Routing.RouteDiagnostics";

    public sealed record ValueProps(string? Value);

    private static PageResult<ValueProps> Props(object? value) => Page.Props(new ValueProps(value?.ToString()));

    private static async Task<IReadOnlyList<string>> WarningsAsync(
        string[] files, Action<WebApplication>? map = null, Action<IServiceCollection>? services = null)
    {
        await using var app = await TestApp.StartAsync(files, map: map, services: services);
        return Warnings(app);
    }

    private static IReadOnlyList<string> Warnings(TestApp app) =>
        [.. app.Logs.Where(l => l.Category == Category && l.Level == LogLevel.Warning).Select(l => l.Message)];

    [Fact]
    public async Task PageLoader_ParameterFromTheQueryNextToAnUnboundRouteParameter_IsWarnedAtStartup()
    {
        var warnings = await WarningsAsync(
            ["app/blog/[slug]/page.tsx"],
            map: a => a.MapPageLoader("/blog/[slug]", (string id) => Props(id)));

        var warning = Assert.Single(warnings);
        Assert.Contains("app/blog/[slug]/page.tsx", warning);
        Assert.Contains("'id'", warning);
        Assert.Contains("'slug'", warning);
        Assert.Contains("query string", warning);
    }

    public sealed class Posts;

    public sealed record PostQuery(string Slug, int? Page);

    public sealed record SearchQuery(string? Id, int? Page);

    /// <summary>Binds itself from the request, so minimal API binding never looks at the route or the query string.</summary>
    public sealed class Paging
    {
        public static ValueTask<Paging?> BindAsync(HttpContext http) => ValueTask.FromResult<Paging?>(new Paging());
    }

    public static TheoryData<string, Action<WebApplication>> NotFromTheQuery => new()
    {
        { "[FromQuery]", a => a.MapPageLoader("/blog/[slug]", ([FromQuery] string id) => Props(id)) },
        { "[FromHeader]", a => a.MapPageLoader("/blog/[slug]", ([FromHeader] string id) => Props(id)) },
        { "[FromRoute] by name", a => a.MapPageLoader("/blog/[slug]", ([FromRoute(Name = "slug")] string id, int page) => Props(id + page)) },
        { "[FromServices]", a => a.MapPageLoader("/blog/[slug]", ([FromServices] Posts posts) => Props(posts)) },
        { "a service", a => a.MapPageLoader("/blog/[slug]", (Posts posts) => Props(posts)) },
        { "HttpContext and friends", a => a.MapPageLoader("/blog/[slug]", (HttpContext http, HttpRequest request, ClaimsPrincipal user, CancellationToken ct) => Props(http)) },
        { "[AsParameters] binding the route", a => a.MapPageLoader("/blog/[slug]", ([AsParameters] PostQuery query) => Props(query)) },
        { "[AsParameters] next to an unbound route", a => a.MapPageLoader("/blog/[slug]", ([AsParameters] SearchQuery query) => Props(query)) },
        { "BindAsync", a => a.MapPageLoader("/blog/[slug]", (Paging paging) => Props(paging)) },
        { "a case-only difference, which binds the route", a => a.MapPageLoader("/blog/[slug]", (string Slug, int page) => Props(Slug + page)) },
        { "every route parameter bound", a => a.MapPageLoader("/projects/[id]", (string id, string? q, int? page) => Props(id + q + page)) },
        { "an array next to a bound route", a => a.MapPageLoader("/blog/[slug]", (string slug, string[] tags) => Props(slug + tags.Length)) },
        { "no route parameters", a => a.MapPageLoader("/blog", (string? tag, string? id) => Props(tag + id)) },
    };

    [Theory]
    [MemberData(nameof(NotFromTheQuery))]
    public async Task PageLoader_ParametersThatDoNotBindFromTheQueryOrLeaveNoRouteParameterUnbound_AreNotWarned(
        string name, Action<WebApplication> map)
    {
        var warnings = await WarningsAsync(
            ["app/blog/page.tsx", "app/blog/[slug]/page.tsx", "app/projects/[id]/page.tsx"],
            map: map,
            services: s => s.AddSingleton<Posts>());

        Assert.True(warnings.Count == 0, $"{name}: {string.Join(" | ", warnings)}");
    }

    [Fact]
    public async Task PageLoader_AnArrayOnACatchAll_BindsFromTheQueryAndIsWarned()
    {
        // Route values are strings (a catch-all's segments joined by '/'); an array parameter binds from the query string.
        var warnings = await WarningsAsync(
            ["app/docs/[...slug]/page.tsx"],
            map: a => a.MapPageLoader("/docs/[...slug]", (string[] slug) => Props(slug.Length)));

        var warning = Assert.Single(warnings);
        Assert.Contains("app/docs/[...slug]/page.tsx", warning);
        Assert.Contains("'slug'", warning);
    }

    [Theory]
    [InlineData("slg")]
    [InlineData("sulg")]
    [InlineData("slugs")]
    public async Task PageLoader_ANearNameOfAnUnboundRouteParameter_IsTheOneWarned(string name)
    {
        // `page` is a query parameter like any other; the near name is the likely typo.
        var warnings = await WarningsAsync(
            ["app/blog/[slug]/page.tsx"],
            map: a => a.MapPageLoader("/blog/[slug]", NearName(name)));

        var warning = Assert.Single(warnings);
        Assert.Contains($"'{name}'", warning);
        Assert.Contains("'slug'", warning);
    }

    [Fact]
    public async Task PageLoader_NoNearNameOfTheUnboundRouteParameter_NamesEveryQueryParameterInOneWarning()
    {
        // Three edits or more from 'slug': no near name, so any of them could be the one meant for the route.
        var warnings = await WarningsAsync(
            ["app/blog/[slug]/page.tsx"],
            map: a => a.MapPageLoader("/blog/[slug]", (string s, int page, string? q) => Props(s + page + q)));

        var warning = Assert.Single(warnings);
        Assert.Contains("binds 's', 'page' and 'q' from the query string", warning);
        Assert.Contains("'slug'", warning);
    }

    [Fact]
    public async Task PageLoader_SeveralUnboundRouteParameters_AreAllNamed()
    {
        var warnings = await WarningsAsync(
            ["app/[team]/[project]/page.tsx"],
            map: a => a.MapPageLoader("/[team]/[project]", (string a1, int page) => Props(a1 + page)));

        var warning = Assert.Single(warnings);
        Assert.Contains("binds 'a1' and 'page' from the query string", warning);
        Assert.Contains("route parameters 'team' and 'project' of /[team]/[project]", warning);
    }

    [Fact]
    public async Task PageLoader_ARouteParameterTheLayoutLoaderAboveBinds_IsBound()
    {
        // The layout loader reads the locale for the whole section; the page loader needs only its query parameters.
        var warnings = await WarningsAsync(
            ["app/[locale]/layout.tsx", "app/[locale]/blog/page.tsx"],
            map: a =>
            {
                a.MapLayoutLoader("/[locale]", (string locale) => Props(locale));
                a.MapPageLoader("/[locale]/blog", (int? page, string? tag) => Props(page + tag));
            });

        Assert.Empty(warnings);
    }

    // A route group's prefix is part of the page's URL: its parameters are route parameters of the page.
    [Theory]
    [InlineData("by name")]
    [InlineData("[FromRoute]")]
    public async Task PageLoader_InARouteGroup_BindingTheGroupsRouteParameter_IsNotWarned(string how)
    {
        var warnings = await WarningsAsync(
            ["app/projects/page.tsx"],
            map: a =>
            {
                var tenant = a.MapGroup("/{tenant}");
                _ = how == "by name"
                    ? tenant.MapPageLoader("/projects", (string tenant, string? q) => Props(tenant + q))
                    : tenant.MapPageLoader("/projects", ([FromRoute(Name = "tenant")] string id, string? q) => Props(id + q));
            });

        Assert.Empty(warnings);
    }

    [Fact]
    public async Task PageLoader_InARouteGroup_ANearNameOfTheGroupsRouteParameter_IsWarned()
    {
        var warnings = await WarningsAsync(
            ["app/projects/page.tsx"],
            map: a => a.MapGroup("/{tenant}").MapPageLoader("/projects", (string tenat, string? q) => Props(tenat + q)));

        var warning = Assert.Single(warnings);
        // The URL the page answers at, as mapped: the group's prefix and the page's pattern.
        Assert.Contains("binds 'tenat' from the query string, not route parameter 'tenant' of /{tenant}/projects,", warning);
    }

    [Fact]
    public async Task PageLoader_AnArrayWithTheRouteParametersNameInAnotherCase_IsTheOneWarned()
    {
        var warnings = await WarningsAsync(
            ["app/docs/[...slug]/page.tsx"],
            map: a => a.MapPageLoader("/docs/[...slug]", (string[] Slug, int page) => Props(Slug.Length + page)));

        var warning = Assert.Single(warnings);
        Assert.Contains("'Slug'", warning);
    }

    [Fact]
    public async Task LayoutLoader_ParameterFromTheQueryNextToAnUnboundRouteParameter_IsWarned()
    {
        var warnings = await WarningsAsync(
            ["app/[team]/layout.tsx", "app/[team]/page.tsx"],
            map: a => a.MapLayoutLoader("/[team]", (string id) => Props(id)));

        var warning = Assert.Single(warnings);
        Assert.Contains("app/[team]/layout.tsx", warning);
        Assert.Contains("'id'", warning);
        Assert.Contains("'team'", warning);
    }

    [Fact]
    public async Task LayoutLoader_AnArrayNextToAnUnboundRouteParameter_BindsFromTheQueryAndIsWarned()
    {
        // Layout loaders bind arrays from the query string, as page loaders do.
        var warnings = await WarningsAsync(
            ["app/[team]/layout.tsx", "app/[team]/page.tsx"],
            map: a => a.MapLayoutLoader("/[team]", (string[] tags) => Props(tags.Length)));

        var warning = Assert.Single(warnings);
        Assert.Contains("app/[team]/layout.tsx", warning);
        Assert.Contains("binds 'tags' from the query string", warning);
        Assert.Contains("'team'", warning);
    }

    [Fact]
    public async Task LayoutLoader_AnArrayOnACatchAll_BindsFromTheQueryAndIsWarned()
    {
        var warnings = await WarningsAsync(
            ["app/docs/[...slug]/layout.tsx", "app/docs/[...slug]/page.tsx"],
            map: a => a.MapLayoutLoader("/docs/[...slug]", (string[] slug) => Props(slug.Length)));

        var warning = Assert.Single(warnings);
        Assert.Contains("binds the array 'slug' from the query string", warning);
    }

    [Fact]
    public async Task LayoutLoader_ARouteParameterTheLayoutLoaderAboveBinds_IsBound()
    {
        var warnings = await WarningsAsync(
            ["app/[team]/layout.tsx", "app/[team]/[project]/layout.tsx", "app/[team]/[project]/page.tsx"],
            map: a =>
            {
                a.MapLayoutLoader("/[team]", (string team) => Props(team));
                a.MapLayoutLoader("/[team]/[project]", (string project, string? q) => Props(project + q));
            });

        Assert.Empty(warnings);
    }

    public static TheoryData<string, string[], Action<WebApplication>, string, string> NoLoaderAboveBinds => new()
    {
        {
            "no layout loader", ["app/[locale]/layout.tsx", "app/[locale]/blog/page.tsx"],
            a => a.MapPageLoader("/[locale]/blog", (int? page, string? tag) => Props(page + tag)),
            "app/[locale]/blog/page.tsx", "locale"
        },
        {
            "a layout loader above binding another one", ["app/[team]/layout.tsx", "app/[team]/[project]/page.tsx"],
            a =>
            {
                a.MapLayoutLoader("/[team]", (string team) => Props(team));
                a.MapPageLoader("/[team]/[project]", (int? page) => Props(page));
            },
            "app/[team]/[project]/page.tsx", "project"
        },
        {
            "a layout loader of another section", ["app/shop/[id]/layout.tsx", "app/shop/[id]/page.tsx", "app/blog/[id]/page.tsx"],
            a =>
            {
                a.MapLayoutLoader("/shop/[id]", (string id) => Props(id));
                a.MapPageLoader("/blog/[id]", (string? q) => Props(q));
            },
            "app/blog/[id]/page.tsx", "id"
        },
        {
            "a layout loader below", ["app/[team]/layout.tsx", "app/[team]/[project]/layout.tsx", "app/[team]/[project]/page.tsx"],
            a =>
            {
                a.MapLayoutLoader("/[team]", (string? q) => Props(q));
                a.MapLayoutLoader("/[team]/[project]", (string team, string project) => Props(team + project));
            },
            "app/[team]/layout.tsx", "team"
        },
    };

    [Theory]
    [MemberData(nameof(NoLoaderAboveBinds))]
    public async Task Loader_ARouteParameterNoLayoutLoaderAboveBinds_IsStillWarned(
        string name, string[] files, Action<WebApplication> map, string file, string routeParameter)
    {
        var warnings = await WarningsAsync(files, map: map);

        Assert.True(warnings.Count > 0, $"{name}: no warning");
        Assert.All(warnings, w => Assert.Contains($"The loader of {file} ", w));
        Assert.All(warnings, w => Assert.Contains($"'{routeParameter}'", w));
    }

    public sealed record TeamFilter(string Name);

    public static TheoryData<string, Action<WebApplication>> LayoutNotFromTheQuery => new()
    {
        { "every route parameter bound", a => a.MapLayoutLoader("/[team]", (string team, string? q) => Props(team + q)) },
        { "[FromQuery]", a => a.MapLayoutLoader("/[team]", ([FromQuery] string id) => Props(id)) },
        { "an array next to a bound route", a => a.MapLayoutLoader("/[team]", (string team, string[] tags) => Props(team + tags.Length)) },
        { "a service", a => a.MapLayoutLoader("/[team]", (Posts posts, ClaimsPrincipal user) => Props(posts)) },
        { "no route parameters", a => a.MapLayoutLoader("app/(shop)", (string? id) => Props(id)) },
    };

    [Theory]
    [MemberData(nameof(LayoutNotFromTheQuery))]
    public async Task LayoutLoader_ParametersThatDoNotBindFromTheQueryOrLeaveNoRouteParameterUnbound_AreNotWarned(
        string name, Action<WebApplication> map)
    {
        var warnings = await WarningsAsync(
            ["app/[team]/layout.tsx", "app/[team]/page.tsx", "app/(shop)/layout.tsx", "app/(shop)/cart/page.tsx"],
            map: map,
            services: s => s.AddSingleton<Posts>());

        Assert.True(warnings.Count == 0, $"{name}: {string.Join(" | ", warnings)}");
    }

    private static readonly string[] Projects =
    [
        "app/projects/layout.tsx",
        "app/projects/page.tsx",
        "app/projects/[id]/page.tsx",
        "app/projects/[id]/settings/page.tsx",
        "app/blog/[slug]/page.tsx",
    ];

    [Fact]
    public async Task LayoutLoader_AQueryParameterNamedAfterARouteParameterOfAPageBelow_IsWarned()
    {
        // A layout loader binds only the route values of its own URL: this one gets ?id=, never the project's id.
        var warnings = await WarningsAsync(Projects, map: a => a.MapLayoutLoader("/projects", (string id) => Props(id)));

        var warning = Assert.Single(warnings);
        Assert.Contains("The loader of app/projects/layout.tsx binds 'id' from the query string", warning);
        Assert.Contains("route parameter 'id' of /projects/[id] below it", warning);
        Assert.Contains("its own URL, /projects.", warning);
        Assert.Contains("[FromQuery]", warning);
    }

    public static TheoryData<string, Action<WebApplication>, string> NamedAfterARouteParameterBelow => new()
    {
        { "in another case", a => a.MapLayoutLoader("/projects", (string Id) => Props(Id)), "Id" },
        { "an array", a => a.MapLayoutLoader("/projects", (string[] id) => Props(id.Length)), "id" },
        { "a TryParse type", a => a.MapLayoutLoader("/projects", (int? id) => Props(id)), "id" },
        { "next to an ordinary query parameter", a => a.MapLayoutLoader("/projects", (string? q, int id) => Props(q + id)), "id" },
    };

    [Theory]
    [MemberData(nameof(NamedAfterARouteParameterBelow))]
    public async Task LayoutLoader_AQueryParameterNamedAfterARouteParameterBelow_IsWarnedInEveryForm(
        string name, Action<WebApplication> map, string parameter)
    {
        var warnings = await WarningsAsync(Projects, map: map);

        var warning = Assert.Single(warnings);
        Assert.True(warning.Contains($"binds '{parameter}' from the query string, not route parameter 'id' of /projects/[id]", StringComparison.Ordinal), $"{name}: {warning}");
    }

    public static TheoryData<string, Action<WebApplication>> NotNamedAfterARouteParameterBelow => new()
    {
        { "[FromQuery]", a => a.MapLayoutLoader("/projects", ([FromQuery] string? id) => Props(id)) },
        { "another name", a => a.MapLayoutLoader("/projects", (string? q, int? page) => Props(q + page)) },
        { "a route parameter of a page in another section", a => a.MapLayoutLoader("/projects", (string? slug) => Props(slug)) },
        { "a service", a => a.MapLayoutLoader("/projects", (Posts id) => Props(id)) },
    };

    [Theory]
    [MemberData(nameof(NotNamedAfterARouteParameterBelow))]
    public async Task LayoutLoader_AQueryParameterNotNamedAfterARouteParameterBelow_IsNotWarned(string name, Action<WebApplication> map)
    {
        var warnings = await WarningsAsync(Projects, map: map, services: s => s.AddSingleton<Posts>());

        Assert.True(warnings.Count == 0, $"{name}: {string.Join(" | ", warnings)}");
    }

    [Fact]
    public async Task LayoutLoader_AParameterNamedAfterARouteParameterBelow_IsNotAlsoNamedForItsOwnUnboundRouteParameter()
    {
        // 'id' is meant for the page's id, not the team: only 'q' could be meant for the team.
        var files = new[] { "app/[team]/layout.tsx", "app/[team]/page.tsx", "app/[team]/[id]/page.tsx" };
        var alone = await WarningsAsync(files, map: a => a.MapLayoutLoader("/[team]", (string id) => Props(id)));
        var withQuery = await WarningsAsync(files, map: a => a.MapLayoutLoader("/[team]", (string id, string? q) => Props(id + q)));

        Assert.Contains("binds 'id' from the query string, not route parameter 'id' of /[team]/[id]", Assert.Single(alone));
        Assert.Equal(2, withQuery.Count);
        Assert.Single(withQuery, w => w.Contains("binds 'id' from the query string, not route parameter 'id'", StringComparison.Ordinal));
        Assert.Single(withQuery, w => w.Contains("binds 'q' from the query string, and route parameter 'team'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("app/[id]/page.tsx", "app/[...rest]/page.tsx", "/x")]
    [InlineData("app/[id]/edit/page.tsx", "app/[...rest]/page.tsx", "/x/edit")]
    [InlineData("app/[id]/page.tsx", "app/[[...rest]]/page.tsx", "/x")]
    [InlineData("app/[...rest]/page.tsx", "app/[[...all]]/page.tsx", "/x")]
    [InlineData("app/page.tsx", "app/[[...slug]]/page.tsx", "/")]
    [InlineData("app/docs/page.tsx", "app/docs/[[...slug]]/page.tsx", "/docs")]
    [InlineData("app/[id]/page.tsx", "app/[id]/[[...rest]]/page.tsx", "/x")]
    // Literals in different places: neither page is the other with a literal for a parameter.
    [InlineData("app/en/[slug]/page.tsx", "app/[lang]/about/page.tsx", "/en/about")]
    [InlineData("app/a/[...rest]/page.tsx", "app/[b]/c/page.tsx", "/a/c")]
    // Different parameters at one position: a value meant for one can land in the other.
    [InlineData("app/[lang]/about/page.tsx", "app/[slug]/[page]/page.tsx", "/x/about")]
    public async Task OverlappingPatterns_AreWarnedNamingBothFilesAndTheOneThatWins(string winner, string loser, string url)
    {
        foreach (var files in new[] { [winner, loser], new[] { loser, winner } })
        {
            await using var app = await TestApp.StartAsync(files);

            var warning = Assert.Single(Warnings(app));
            Assert.Contains($"{winner} wins over {loser}", warning);
            Assert.Equal(winner, (string?)(await app.GetDataJsonAsync(url))["page"]);
        }
    }

    [Fact]
    public async Task OverlappingPatterns_InFoldersThatDifferOnlyInCase_AreWarned()
    {
        // Literals match in any case, so folders that differ only in case still overlap. Only a case-sensitive file
        // system (Linux, as a rule) can hold both folders; on macOS and Windows they are one folder.
        Assert.SkipUnless(TempFolderIsCaseSensitive(), "The temp folder's file system ignores case.");
        const string winner = "app/Docs/[id]/page.tsx", loser = "app/docs/[...rest]/page.tsx";

        foreach (var files in new[] { [winner, loser], new[] { loser, winner } })
        {
            await using var app = await TestApp.StartAsync(files);

            var warning = Assert.Single(Warnings(app));
            Assert.Contains($"{winner} wins over {loser}", warning);
            Assert.Equal(winner, (string?)(await app.GetDataJsonAsync("/docs/x"))["page"]);
        }
    }

    private static bool TempFolderIsCaseSensitive()
    {
        var folder = Directory.CreateTempSubdirectory("netjes-case-");
        try
        {
            File.WriteAllText(Path.Combine(folder.FullName, "a"), "");
            return !File.Exists(Path.Combine(folder.FullName, "A"));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task OverlappingPatterns_TheWarningShowsTheURLsBothMatch()
    {
        var warnings = await WarningsAsync(["app/en/[slug]/page.tsx", "app/[lang]/about/page.tsx"]);

        Assert.Contains("/en/about", Assert.Single(warnings));
    }

    [Fact]
    public async Task OverlappingPatterns_APageWithALoaderAndAFilePage_AreCompared()
    {
        var warnings = await WarningsAsync(
            ["app/[id]/page.tsx", "app/[...rest]/page.tsx"],
            map: a => a.MapPageLoader("/[...rest]", (string rest) => Props(rest)));

        Assert.Contains("app/[id]/page.tsx wins over app/[...rest]/page.tsx", Assert.Single(warnings));
    }

    [Fact]
    public async Task OverlappingPatterns_AnEndpointOrderAheadOfPrecedence_DecidesTheWinner()
    {
        // The literal would win on precedence; the loader's order puts the parameter first, which is no longer the convention.
        await using var app = await TestApp.StartAsync(
            ["app/about/page.tsx", "app/[slug]/page.tsx"],
            map: a => a.MapPageLoader("/[slug]", (string slug) => Props(slug)).WithOrder(-1));

        Assert.Contains("app/[slug]/page.tsx wins over app/about/page.tsx", Assert.Single(Warnings(app)));
        Assert.Equal("app/[slug]/page.tsx", (string?)(await app.GetDataJsonAsync("/about"))["page"]);
    }

    [Theory]
    // The catch-all's minlength(1): it needs at least one segment.
    [InlineData("app/docs/page.tsx", "app/docs/[...slug]/page.tsx")]
    [InlineData("app/page.tsx", "app/[...slug]/page.tsx")]
    [InlineData("app/[id]/page.tsx", "app/[id]/[...rest]/page.tsx")]
    // Different lengths, or literals that differ.
    [InlineData("app/blog/page.tsx", "app/blog/[slug]/page.tsx")]
    [InlineData("app/posts/[id]/page.tsx", "app/docs/[...rest]/page.tsx")]
    [InlineData("app/[a]/x/page.tsx", "app/[b]/y/page.tsx")]
    // A page that is the other with literals for some of its parameters: the literal wins, as in Next.js.
    [InlineData("app/blog/new/page.tsx", "app/blog/[slug]/page.tsx")]
    [InlineData("app/Blog/new/page.tsx", "app/blog/[slug]/page.tsx")]
    [InlineData("app/About/page.tsx", "app/[slug]/page.tsx")]
    [InlineData("app/about/page.tsx", "app/[...slug]/page.tsx")]
    [InlineData("app/about/page.tsx", "app/[[...slug]]/page.tsx")]
    [InlineData("app/docs/[...path]/page.tsx", "app/[...slug]/page.tsx")]
    [InlineData("app/blog/[slug]/page.tsx", "app/[...rest]/page.tsx")]
    [InlineData("app/projects/[id]/settings/page.tsx", "app/projects/[id]/[tab]/page.tsx")]
    [InlineData("app/en/[slug]/page.tsx", "app/[lang]/[page]/page.tsx")]
    public async Task PatternsThatCannotMatchTheSameUrl_OrALiteralForAParameter_AreNotWarned(string one, string other)
    {
        var warnings = await WarningsAsync([one, other]);

        Assert.Empty(warnings);
    }

    public sealed record PinInput(string? Text, bool Pinned);

    public sealed record TagsInput(string? Name, string[] Tags, bool? Public);

    public sealed record SignInInput(string Email, string Password);

    public sealed record InitPinInput
    {
        public string? Text { get; init; }

        public bool Pinned { get; init; }
    }

    [Fact]
    public async Task ServerAction_APositionalFormInputWithACheckbox_IsWarned()
    {
        var warnings = await WarningsAsync(
            ["app/page.tsx"], map: a => a.MapAction("pins.add", ([FromForm] PinInput input) => ServerAction.Ok()));

        var warning = Assert.Single(warnings);
        Assert.Contains("'pins.add'", warning);
        Assert.Contains("PinInput", warning);
        Assert.Contains("'Pinned'", warning);
        Assert.DoesNotContain("'Text'", warning);
        Assert.Contains("init properties", warning);
    }

    [Fact]
    public async Task ServerAction_APositionalFormInputWithAListAndANullableCheckbox_NamesBoth()
    {
        var warnings = await WarningsAsync(
            ["app/page.tsx"], map: a => a.MapAction("tags.save", ([FromForm] TagsInput? input) => ServerAction.Ok()));

        Assert.Contains("'Tags' and 'Public'", Assert.Single(warnings));
    }

    [Fact]
    public async Task PageAction_APositionalFormInputWithACheckbox_IsWarnedNamingThePageFile()
    {
        await using var app = await TestApp.StartAsync(
            ["app/notes/page.tsx"], map: a => a.MapPageAction("/notes", ([FromForm] PinInput input) => Page.Redirect("/notes")));

        Assert.Contains("app/notes/page.tsx", Assert.Single(Warnings(app)));
    }

    public static TheoryData<string, Action<WebApplication>> FormInputsAMissingFieldDoesNotRefuse => new()
    {
        { "init properties", a => a.MapAction("pins.add", ([FromForm] InitPinInput input) => ServerAction.Ok()) },
        { "text fields only", a => a.MapAction("auth.login", ([FromForm] SignInInput input) => ServerAction.Ok()) },
        { "a checkbox of its own", a => a.MapAction("auth.login", ([FromForm] SignInInput input, [FromForm] bool? stay) => ServerAction.Ok()) },
        { "JSON", a => a.MapAction("pins.add", (PinInput input) => ServerAction.Ok()) },
    };

    [Theory]
    [MemberData(nameof(FormInputsAMissingFieldDoesNotRefuse))]
    public async Task FormInputs_ThatAMissingCheckboxDoesNotRefuse_AreNotWarned(string name, Action<WebApplication> map)
    {
        var warnings = await WarningsAsync(["app/page.tsx"], map: map);

        Assert.True(warnings.Count == 0, $"{name}: {string.Join(" | ", warnings)}");
    }

    [Fact]
    public async Task RoutesCommand_ReportsTheWarningsInsteadOfLoggingThem()
    {
        var output = Path.Combine(Path.GetTempPath(), $"netjes-{Guid.NewGuid():N}.json");
        await using var app = await TestApp.StartAsync(
            ["app/[id]/page.tsx", "app/[...rest]/page.tsx", "app/blog/[slug]/page.tsx"],
            map: a => a.MapPageLoader("/blog/[slug]", (string id) => Props(id)),
            configure: o => o.Command = new NetjesCommandOptions { Name = "routes", Output = output },
            services: s => s.AddSingleton(new Netjes.TypeGen.CommandExit(_ => { })));

        var report = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken))!;
        File.Delete(output);
        var warnings = report["warnings"]!.AsArray();

        Assert.Empty(Warnings(app));
        Assert.Equal(["query-binding", "overlap"], warnings.Select(w => (string?)w!["kind"]));
        Assert.Equal(["app/blog/[slug]/page.tsx"], warnings[0]!["files"]!.AsArray().Select(f => (string?)f));
        Assert.Equal(["app/[id]/page.tsx", "app/[...rest]/page.tsx"], warnings[1]!["files"]!.AsArray().Select(f => (string?)f));
        Assert.Contains("'id'", (string?)warnings[0]!["message"]);
        Assert.Contains("app/[id]/page.tsx wins over app/[...rest]/page.tsx", (string?)warnings[1]!["message"]);
    }

    [Fact]
    public async Task RoutesCommand_ListsTheServerActionEndpoints_ButNotThePagesCatchAll()
    {
        var output = Path.Combine(Path.GetTempPath(), $"netjes-{Guid.NewGuid():N}.json");
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("posts.create", () => ServerAction.Ok()),
            configure: o => o.Command = new NetjesCommandOptions { Name = "routes", Output = output },
            services: s => s.AddSingleton(new Netjes.TypeGen.CommandExit(_ => { })));

        var report = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken))!;
        File.Delete(output);
        var endpoints = report["endpoints"]!.AsArray().Select(e => ((string?)e!["method"], (string?)e["route"])).ToList();

        Assert.Contains(("POST", "/_netjes/actions/posts.create"), endpoints);
        Assert.Contains(("GET", "/_netjes/antiforgery"), endpoints);
        Assert.DoesNotContain(endpoints, e => e.Item2 is null || e.Item2.Contains("{**", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Production_WarnsAtStartupAsWell()
    {
        await using var app = await TestApp.StartAsync([], environment: "Production", prepareRoot: root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "dist", "client", ".vite"));
            File.WriteAllText(Path.Combine(root, "dist", "client", ".vite", "manifest.json"), """
                {
                  "node_modules/netjes/dist/client-entry.js": { "file": "assets/client-entry-AAA.js", "src": "node_modules/netjes/dist/client-entry.js", "isEntry": true },
                  "app/[id]/page.tsx": { "file": "assets/id-BBB.js", "src": "app/[id]/page.tsx", "isDynamicEntry": true },
                  "app/[...rest]/page.tsx": { "file": "assets/rest-CCC.js", "src": "app/[...rest]/page.tsx", "isDynamicEntry": true }
                }
                """);
        });

        Assert.Contains("app/[id]/page.tsx wins over app/[...rest]/page.tsx", Assert.Single(Warnings(app)));
    }

    [Fact]
    public async Task TheLogLevelOfTheCategory_SilencesBothKinds()
    {
        // The opt-out the README documents, for overlaps an app keeps on purpose.
        string[] files = ["app/[id]/page.tsx", "app/[...rest]/page.tsx", "app/blog/[slug]/page.tsx"];
        Action<WebApplication> map = a => a.MapPageLoader("/blog/[slug]", (string id) => Props(id));
        await using (var warned = await TestApp.StartAsync(files, map: map))
        {
            Assert.Equal(2, Warnings(warned).Count);
        }

        await using var app = await TestApp.StartAsync(files, map: map, prepareRoot: root => File.WriteAllText(
            Path.Combine(root, "appsettings.json"),
            """{ "Logging": { "LogLevel": { "Netjes.Routing.RouteDiagnostics": "Error" } } }"""));

        Assert.Empty(Warnings(app));
    }

    private static Delegate NearName(string name) => name switch
    {
        "slg" => (string slg, int page) => Props(slg + page),
        "sulg" => (string sulg, int page) => Props(sulg + page),
        "slugs" => (string slugs, int page) => Props(slugs + page),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}
