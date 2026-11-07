using System.Net;
using System.Text.Json.Nodes;

namespace __NAME__.Tests;

/// <summary>The home page's loader and server action (Features/Home/HomePage.cs), requested as the client router does.</summary>
public sealed class HomePageTests(AppFactory app) : IClassFixture<AppFactory>
{
    [Fact]
    public async ValueTask TheHomePage_HasTheLoadersMessage()
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(NetjesHeaders.Data, "1");

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Hello from C#", (string?)payload["props"]!["message"]);
    }

    [Fact]
    public async ValueTask Greeting_WithoutAName_IsAFieldError()
    {
        var (status, result) = await Greet("");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("Tell us your name.", (string?)result["errors"]!["name"]![0]);
    }

    [Fact]
    public async ValueTask Greeting_WithAName_SaysHello()
    {
        var (status, result) = await Greet("Ada");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Hello, Ada!", (string?)result["result"]!["text"]);
    }

    /// <summary>Posts the form with an antiforgery token: the header takes the token, the client's cookies its cookie.</summary>
    private async Task<(HttpStatusCode, JsonNode)> Greet(string name)
    {
        var client = app.CreateClient();
        var token = JsonNode.Parse(await client.GetStringAsync("/_netjes/antiforgery", TestContext.Current.CancellationToken))!;
        client.DefaultRequestHeaders.Add(NetjesHeaders.Data, "1");
        client.DefaultRequestHeaders.Add((string)token["headerName"]!, (string)token["requestToken"]!);

        var response = await client.PostAsync(
            "/_netjes/actions/home.greet",
            new FormUrlEncodedContent([new("name", name)]),
            TestContext.Current.CancellationToken);

        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!);
    }
}
