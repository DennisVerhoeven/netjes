using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace Netjes.Tests.Endpoints;

/// <summary>What the test app refuses to start, so that a test cannot pass or fail for a reason it does not name.</summary>
public class TestAppTests
{
    /// <summary>
    /// Pages are never output-cached in the Development environment, the test app's default, so a test of what the
    /// output cache stores would see every request render there. It says how to test either.
    /// </summary>
    [Fact]
    public async Task InDevelopment_AnAppWithAnOutputCacheStore_DoesNotStartUnlessAskedTo()
    {
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestApp.StartAsync(["app/page.tsx"], services: s => s.AddOutputCache()));
        await using var optedIn = await TestApp.StartAsync(
            ["app/page.tsx"], services: s => s.AddOutputCache(), developmentOutputCache: true);
        await using var testing = await TestApp.StartAsync(
            ["app/page.tsx"], services: s => s.AddOutputCache(), environment: "Testing", mode: NetjesMode.Development);

        Assert.Contains("environment: \"Testing\", mode: NetjesMode.Development", refused.Message, StringComparison.Ordinal);
        Assert.Contains("developmentOutputCache: true", refused.Message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await optedIn.GetDocumentAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await testing.GetDocumentAsync("/")).StatusCode);
    }
}
