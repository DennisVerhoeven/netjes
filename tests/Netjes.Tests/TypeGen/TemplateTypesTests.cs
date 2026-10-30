using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Netjes.Tests.Endpoints;

namespace Netjes.Tests.TypeGen;

/// <summary>
/// The `netjes new` template ships its generated/netjes.ts, so a new app typechecks before its first build. This mirrors
/// the template's C# (packages/netjes/template) and checks the shipped file is what the generator writes for it.
/// </summary>
public class TemplateTypesTests
{
    public static class HomePage
    {
        public sealed record HomeProps(string Message, DateTimeOffset RenderedAt);

        public sealed record GreetInput
        {
            public string? Name { get; init; }
        }

        public sealed record Greeting(string Text);
    }

    public sealed record SiteProps(string SiteName);

    [Fact]
    public async Task TemplateGeneratedFile_MatchesTheGenerator()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/about/page.tsx", "app/not-found.tsx", "app/error.tsx"],
            map: a =>
            {
                a.MapPageLoader("/", (TimeProvider time) => Page.Props(new HomePage.HomeProps("Hello from C#", time.GetUtcNow())));
                a.MapAction("home.greet", ([FromForm] HomePage.GreetInput input) =>
                    string.IsNullOrWhiteSpace(input.Name)
                        ? ServerAction.Invalid("name", "Tell us your name.")
                        : ServerAction.Ok(new HomePage.Greeting($"Hello, {input.Name.Trim()}!")));
            },
            configure: o => o.UseSharedProps(_ => ValueTask.FromResult(new SiteProps("App"))));

        var expected = app.App.Services.GetRequiredService<Netjes.TypeGen.TypeScriptGenerator>().Generate();
        var shipped = await File.ReadAllTextAsync(
            Path.Combine(RepoRoot(), "packages", "netjes", "template", "generated", "netjes.ts"), TestContext.Current.CancellationToken);

        Assert.Equal(expected, shipped.ReplaceLineEndings("\n"));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Netjes.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
