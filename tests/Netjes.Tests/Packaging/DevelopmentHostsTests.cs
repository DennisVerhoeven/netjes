using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;

namespace Netjes.Tests.Packaging;

/// <summary>
/// The template and both examples ship an appsettings.Development.json that limits the Host header to loopback, so a
/// DNS-rebound page cannot reach the dev app (SEC-24). The tests run each shipped file through ASP.NET's host filtering.
/// </summary>
public class DevelopmentHostsTests
{
    public static TheoryData<string> Apps => new()
    {
        "packages/netjes/template",
        "examples/Blog",
        "examples/TaskBoard",
    };

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task DevelopmentRequests_WithAForeignHost_AreRejected(string app)
    {
        await using var server = await StartAsync(app);

        using var response = await Get(server, "rebind.attacker.test:8081");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task DevelopmentRequests_ToLoopbackHosts_AreServed(string app)
    {
        await using var server = await StartAsync(app);

        foreach (var host in new[] { "localhost:8081", "127.0.0.1:8080", "[::1]:5090", "localhost" })
        {
            using var response = await Get(server, host);

            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{app}: Host {host} gave {(int)response.StatusCode}");
        }
    }

    private static async Task<WebApplication> StartAsync(string app)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        foreach (var file in new[] { "appsettings.json", "appsettings.Development.json" })
        {
            builder.Configuration.AddJsonFile(Path.Combine(RepoRoot(), app, file), optional: true);
        }

        var web = builder.Build();
        web.MapGet("/", () => "ok");
        await web.StartAsync();
        return web;
    }

    private static Task<HttpResponseMessage> Get(WebApplication web, string host)
    {
        var client = web.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = host;
        return client.SendAsync(request);
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
