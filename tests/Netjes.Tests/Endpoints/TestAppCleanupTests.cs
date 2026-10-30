using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;

namespace Netjes.Tests.Endpoints;

/// <summary>The harness's own promise: a test app leaves no directory behind in the temp folder.</summary>
public class TestAppCleanupTests
{
    [Fact]
    public async Task DisposingADevelopmentApp_RemovesItsRoot_WhateverItsWatchersHold()
    {
        for (var i = 0; i < 25; i++)
        {
            string root;
            await using (var app = await TestApp.StartAsync(["app/page.tsx", "app/about/page.tsx"], environment: "Development"))
            {
                root = app.Root;
                (await app.GetDocumentAsync("/about")).EnsureSuccessStatusCode();
            }

            Assert.False(Directory.Exists(root), $"{root} is still there after round {i}");
        }
    }

    [Fact]
    public async Task AnAppThatFailsToStart_RemovesItsRoot()
    {
        string? root = null;

        await Assert.ThrowsAsync<ArgumentException>(() => TestApp.StartAsync(
            ["app/page.tsx"],
            prepareRoot: path => root = path,
            map: a => a.MapAction("not a valid name!", () => ServerAction.Ok())));

        Assert.NotNull(root);
        Assert.False(Directory.Exists(root), $"{root} is still there");
    }
}
