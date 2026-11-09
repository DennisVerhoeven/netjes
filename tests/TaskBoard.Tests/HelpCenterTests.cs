using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Netjes;
using Netjes.Rendering;

namespace TaskBoard.Tests;

/// <summary>
/// The help center page package (examples/HelpCenter), which TaskBoard mounts at /(marketing)/help: inside the
/// marketing chrome, with TaskBoard's own contact page in place of the package's and a page of TaskBoard's in the
/// package's (center) group. None of it reads the database, so the app runs without one.
/// </summary>
public class HelpCenterTests
{
    private const string Package = "@netjes-examples/help-center";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheHelpCenter_RendersInsideTheMarketingChrome()
    {
        await using var app = new HelpCenterApp();
        var client = app.CreateClient();

        var (status, payload) = await PageAsync(client, "/help");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal($"{Package}/app/page.tsx", (string?)payload["page"]);
        Assert.Equal(["app/layout.tsx", "app/(marketing)/layout.tsx", $"{Package}/app/layout.tsx"], Layouts(payload));
        Assert.Equal("/help", (string?)payload["mounts"]![Package]);
    }

    [Theory]
    [InlineData("/help/contact")]
    [InlineData("/HELP/Contact")]
    public async Task TheContactPage_IsTaskBoardsOverride_WithThePackagesProps(string url)
    {
        await using var app = new HelpCenterApp();
        var client = app.CreateClient(new() { AllowAutoRedirect = true });

        var (status, payload) = await PageAsync(client, url);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("app/(marketing)/help/contact/page.tsx", (string?)payload["page"]);
        Assert.Equal("/help/contact", (string?)payload["route"]);
        Assert.Equal(["app/layout.tsx", "app/(marketing)/layout.tsx", $"{Package}/app/layout.tsx"], Layouts(payload));
        Assert.Equal("help@example.com", (string?)payload["props"]!["email"]);
    }

    /// <summary>A client navigation: the page payload as JSON.</summary>
    private static async Task<(HttpStatusCode Status, JsonNode Payload)> PageAsync(HttpClient client, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(NetjesHeaders.Data, "1");
        var response = await client.SendAsync(request, Ct);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!);
    }

    /// <summary>The layout files around the page, outermost first.</summary>
    private static string[] Layouts(JsonNode payload) =>
        [.. payload["segments"]!.AsArray().Select(s => (string?)s!["layout"]).OfType<string>()];

    /// <summary>The real app with server rendering faked and no database: nothing here reads one.</summary>
    private sealed class HelpCenterApp : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(TaskBoardApp.ExampleRoot);
            builder.UseSetting("ConnectionStrings:TaskBoard", "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1");
            builder.UseSetting("Netjes:Mode", "Development");
            builder.ConfigureServices(services => services.AddSingleton<ISsrRenderer>(new FakeRenderer()));
        }
    }
}
