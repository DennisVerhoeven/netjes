namespace Netjes.Tests.Endpoints;

/// <summary><c>Netjes:PublicConfig</c>: runtime configuration for the browser, like NEXT_PUBLIC_ variables without a rebuild.</summary>
public class PublicConfigTests
{
    private const string Settings = """
        {
          "Netjes": {
            "PublicConfig": {
              "ApiUrl": "https://api.example.com",
              "Features": { "Search": "true" },
              "Regions": [ "eu", "us" ]
            }
          },
          "ConnectionStrings": { "Db": "secret" }
        }
        """;

    private static void WriteSettings(string root) => File.WriteAllText(Path.Combine(root, "appsettings.json"), Settings);

    [Fact]
    public async Task DocumentPayload_CarriesThePublicConfigSection_WithCamelCasedKeys()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], prepareRoot: WriteSettings);

        await app.GetDocumentAsync("/");

        Assert.Equal(
            """{"apiUrl":"https://api.example.com","features":{"search":"true"},"regions":["eu","us"]}""",
            app.Renderer.LastPayload["document"]!["publicConfig"]!.ToJsonString());
    }

    [Fact]
    public async Task OnlyThatSection_IsSent()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], prepareRoot: WriteSettings);

        await app.GetDocumentAsync("/");

        Assert.DoesNotContain("secret", app.Renderer.Payloads.Single());
    }

    [Fact]
    public async Task WithoutTheSection_NothingIsSent()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"]);

        await app.GetDocumentAsync("/");

        Assert.Null(app.Renderer.LastPayload["document"]!["publicConfig"]);
    }

    [Fact]
    public async Task DataResponses_DoNotRepeatIt()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], prepareRoot: WriteSettings);

        var payload = await app.GetDataJsonAsync("/");

        Assert.DoesNotContain("api.example.com", payload.ToJsonString());
    }
}
