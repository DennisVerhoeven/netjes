using System.Net;
using Microsoft.AspNetCore.Builder;

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
}
