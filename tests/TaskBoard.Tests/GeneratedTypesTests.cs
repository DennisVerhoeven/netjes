using System.Diagnostics;

namespace TaskBoard.Tests;

/// <summary>
/// What CI runs: `netjes generate --check` starts the app in command mode, which must not need Postgres, and fails when
/// the committed generated/netjes.ts is out of date.
/// </summary>
public class GeneratedTypesTests
{
    [Fact]
    public async Task GenerateCheck_WithoutADatabase_FindsTheCommittedTypesCurrent()
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = TaskBoardApp.ExampleRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        // What the netjes CLI sets for `generate --check`; the database is unreachable.
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment["NETJES_COMMAND"] = "generate";
        start.Environment["NETJES_CHECK"] = "1";
        start.Environment["ConnectionStrings__TaskBoard"] = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1";

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

        Assert.True(process.ExitCode == 0, $"exit {process.ExitCode}\n{await output}\n{await errors}");
    }
}
