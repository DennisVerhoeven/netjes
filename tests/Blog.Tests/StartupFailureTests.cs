using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Blog.Tests;

/// <summary>
/// The Blog when it cannot start. Run as its own program (<c>dotnet Blog.dll</c>, as <c>dotnet run</c> and the netjes
/// CLI run it), it says why in one log line and exits with code 1, without a stack trace. Run by a test host
/// (WebApplicationFactory), its start fails with that reason.
/// </summary>
public class StartupFailureTests
{
    private const string MissingNode = "netjes-test-no-such-node";

    /// <summary>A build folder that is not there.</summary>
    private const string NoBuild = "netjes-test-no-build";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithoutNode_TheAppSaysSoInOneLine_AndExitsWithCode1()
    {
        var (code, output) = await RunAsync(new()
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["Netjes__NodeExecutable"] = MissingNode,
        });

        AssertOneLine(output, $"Could not start '{MissingNode}'. Is Node.js installed and on the PATH? Set Netjes:NodeExecutable to use another executable.");
        // After the host's own messages, which say it started.
        Assert.Contains("Application started. Press Ctrl+C to shut down.", output, StringComparison.Ordinal);
        Assert.True(code == 1, $"Exit code {code}:\n{output}");
    }

    [Fact]
    public async Task WithoutABuild_InProduction_TheAppSaysSoInOneLine_AndExitsWithCode1()
    {
        var (code, output) = await RunAsync(new()
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["Netjes__DistDirectory"] = NoBuild,
        });

        var root = GuestbookRateLimitTests.BlogApp.ExampleRoot;
        AssertOneLine(output,
            $"No production build found at '{Path.Combine(root, NoBuild, "client", ".vite", "manifest.json")}'. To develop, run " +
            $"`npm run dev`, or start the app with ASPNETCORE_ENVIRONMENT=Development. To run in production, build first: " +
            $"`npm run build` in '{root}'.");
        Assert.True(code == 1, $"Exit code {code}:\n{output}");
    }

    [Fact]
    public void WithoutABuild_InProduction_ATestHostsStartFails_WithTheReason()
    {
        using var app = new FailingApp(builder =>
        {
            builder.UseSetting("Netjes:Mode", "Production");
            builder.UseSetting("Netjes:DistDirectory", NoBuild);
        });

        var ex = Assert.ThrowsAny<InvalidOperationException>(() => app.CreateClient());

        Assert.StartsWith("No production build found at ", ex.Message);
    }

    [Fact]
    public void WithoutNode_ATestHostsStartFails_WithTheReason()
    {
        using var app = new FailingApp(builder =>
        {
            builder.UseSetting("Netjes:Mode", "Development");
            builder.UseSetting("Netjes:NodeExecutable", MissingNode);
        });

        var ex = Assert.ThrowsAny<InvalidOperationException>(() => app.CreateClient());

        Assert.StartsWith($"Could not start '{MissingNode}'.", ex.Message);
    }

    /// <summary>
    /// The one error the app logged, at Critical and as the last thing it logged, is <paramref name="message"/>, with
    /// no exception or stack.
    /// </summary>
    private static void AssertOneLine(string output, string message)
    {
        var lines = output.Split('\n');
        var entries = lines.Select((line, i) => (line, i)).Where(l => Regex.IsMatch(l.line, "^(trce|dbug|info|warn|fail|crit): ")).ToList();
        var (header, index) = Assert.Single(entries, e => e.line.StartsWith("fail: ") || e.line.StartsWith("crit: "));
        Assert.StartsWith("crit: ", header);
        Assert.Equal(message, lines[index + 1].Trim());
        Assert.Equal(index, entries[^1].i);
        Assert.DoesNotContain("Unhandled exception", output, StringComparison.Ordinal);
        Assert.DoesNotContain(lines, line => line.TrimStart().StartsWith("at ", StringComparison.Ordinal));
    }

    /// <summary>Runs the built Blog in the example's folder, as <c>dotnet run</c> would, until it exits.</summary>
    private static async Task<(int Code, string Output)> RunAsync(Dictionary<string, string> environment)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet"));
        var start = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = GuestbookRateLimitTests.BlogApp.ExampleRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Blog.dll"));
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        var output = new ConcurrentQueue<string>();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) output.Enqueue(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) output.Enqueue(e.Data);
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        // WaitForExitAsync waits for the output to end too.
        return (process.ExitCode, string.Join('\n', output));
    }

    /// <summary>The real Blog in a test host, with its server rendering as configured.</summary>
    private sealed class FailingApp(Action<IWebHostBuilder> configure) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(GuestbookRateLimitTests.BlogApp.ExampleRoot);
            configure(builder);
        }
    }
}
