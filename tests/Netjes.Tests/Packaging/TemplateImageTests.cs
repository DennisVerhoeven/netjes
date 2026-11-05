using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Netjes.Tests.Packaging;

/// <summary>
/// How the production images of the template and both examples end. The kernel ignores a signal that a container's
/// PID 1 has no handler for, so an app that is PID 1 never dies of its own abort(): after a crash it spins on arm64 and
/// never exits, and on x86-64 under emulation it ended with 139 instead of 134. The images run the app under an init
/// for that. Each is built from its own Dockerfile's <c>production</c> stage, with a small app of the test's own,
/// published under the name the image runs, in place of the <c>build</c> stage, and a stub in place of the <c>node</c>
/// stage; any other stage it copies from is built for real. The container runs on one CPU, read-only and with every
/// capability dropped, as docker-compose.prod.yml runs the app.
/// </summary>
[Trait("Category", "Docker")]
public sealed class TemplateImageTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir = Directory.CreateTempSubdirectory("netjes-image-").FullName;
    private readonly string _tag = "netjes-test-image-" + Guid.NewGuid().ToString("N");
    private string? _container;

    /// <summary>Each Dockerfile, and the name of the app it runs: the template's <c>__NAME__</c>, as <c>netjes new</c> fills it in.</summary>
    public static TheoryData<string, string> Images => new()
    {
        { "packages/netjes/template/Dockerfile", "App" },
        { "examples/Blog/Dockerfile", "Blog" },
        { "examples/TaskBoard/Dockerfile", "TaskBoard" },
    };

    [Theory]
    [MemberData(nameof(Images))]
    public async Task ProductionImage_WhenTheAppThrowsAtStartup_ExitsWithDotnetsAbortCode(string dockerfile, string app)
    {
        await BuildAsync(dockerfile, app, """throw new InvalidOperationException("Not configured");""");

        _container = await StartAsync();

        // .NET prints the exception and aborts the process: 128 + SIGABRT.
        var state = await ExitedAsync(TimeSpan.FromSeconds(30));
        if (state is not { Status: "exited", ExitCode: 134 })
        {
            Assert.Fail(await DescribeAsync(dockerfile, state));
        }
    }

    [Theory]
    [MemberData(nameof(Images))]
    public async Task ProductionImage_OnDockerStop_StopsTheAppAtOnce(string dockerfile, string app)
    {
        await BuildAsync(dockerfile, app, """
            using System.Runtime.InteropServices;

            var stopping = new TaskCompletionSource();
            using var registration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                stopping.TrySetResult();
            });
            Console.WriteLine("Waiting for SIGTERM");
            await stopping.Task;
            return 0;
            """);
        _container = await StartAsync();
        await LoggedAsync(dockerfile, "Waiting for SIGTERM", TimeSpan.FromSeconds(30));

        // Docker sends SIGTERM to the container's PID 1, and SIGKILL 20 seconds later.
        var stop = Stopwatch.StartNew();
        await DockerAsync(["stop", "-t", "20", _container]);
        stop.Stop();

        var state = await StateAsync();
        if (state is not { Status: "exited", ExitCode: 0 } || stop.Elapsed >= TimeSpan.FromSeconds(5))
        {
            Assert.Fail($"{await DescribeAsync(dockerfile, state)}\ndocker stop took {stop.Elapsed.TotalSeconds:0.0} s");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await DockerAsync(["rm", "-f", "-v", _container], check: false, ct: CancellationToken.None);
        }

        await DockerAsync(["rmi", "-f", _tag], check: false, ct: CancellationToken.None);
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// Builds the production stage of <paramref name="dockerfile"/> as <see cref="_tag"/>: <paramref name="program"/>,
    /// published as <paramref name="app"/>, stands in for the app the <c>build</c> stage publishes, and a stub for the
    /// <c>node</c> stage's Node.js, which the program never starts. The main build context is empty: the production
    /// stage copies from other stages only.
    /// </summary>
    private async Task BuildAsync(string dockerfile, string app, string program)
    {
        var text = (await File.ReadAllTextAsync(Path.Combine(RepoRoot(), dockerfile), Ct)).Replace("__NAME__", app, StringComparison.Ordinal);
        var copiedFrom = CopiedFrom(text, "production");
        Assert.True(copiedFrom.All(stage => stage is "build" or "node" or "tini"),
            $"{dockerfile}: the production stage copies from {string.Join(", ", copiedFrom)}, but this test stands in for build and node only, and builds tini");
        Write("Dockerfile", text);

        Write(Path.Combine("src", app + ".csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <UseAppHost>false</UseAppHost>
              </PropertyGroup>
            </Project>
            """);
        Write(Path.Combine("src", "Program.cs"), program);
        await RunAsync(Dotnet(), ["publish", Path.Combine(_dir, "src"), "-c", "Release", "-o", Path.Combine(_dir, "build", "app")],
            TimeSpan.FromMinutes(5), check: true, Ct);

        // The image's health check runs it.
        var node = Write(Path.Combine("node", "usr", "local", "bin", "node"), "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(node, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var context = Directory.CreateDirectory(Path.Combine(_dir, "context")).FullName;
        await DockerAsync([
            "build", "--progress=plain", "--target", "production",
            "--build-context", "build=" + Path.Combine(_dir, "build"),
            "--build-context", "node=" + Path.Combine(_dir, "node"),
            "-f", Path.Combine(_dir, "Dockerfile"), "-t", _tag, context,
        ], timeout: TimeSpan.FromMinutes(15));
    }

    /// <summary>What the <c>COPY --from</c> lines of the stage named <paramref name="stage"/> copy from.</summary>
    private static List<string> CopiedFrom(string dockerfile, string stage)
    {
        var start = Regex.Match(dockerfile, $@"^FROM\s+\S+\s+AS\s+{stage}[ \t]*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        Assert.True(start.Success, $"No stage named {stage}");
        var rest = dockerfile[(start.Index + start.Length)..];
        var end = Regex.Match(rest, @"^FROM\s", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return [.. Regex.Matches(end.Success ? rest[..end.Index] : rest, @"^COPY\s+(?:--\S+\s+)*?--from=(\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value)
            .Distinct()];
    }

    /// <summary>Starts the image on one CPU, as docker-compose.prod.yml runs it, and returns the container's id.</summary>
    private async Task<string> StartAsync() =>
        (await DockerAsync([
            "run", "-d", "--cpus", "1", "--read-only", "--tmpfs", "/tmp", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges:true", _tag,
        ])).Trim();

    /// <summary>Waits until the container has stopped, or <paramref name="within"/> has passed.</summary>
    private async Task<ContainerState> ExitedAsync(TimeSpan within)
    {
        var deadline = Stopwatch.StartNew();
        var state = await StateAsync();
        while (state.Status != "exited" && deadline.Elapsed < within)
        {
            await Task.Delay(250, Ct);
            state = await StateAsync();
        }

        return state;
    }

    private async Task<ContainerState> StateAsync()
    {
        var fields = (await DockerAsync(["inspect", "--format", "{{.State.Status}} {{.State.ExitCode}}", _container!])).Trim().Split(' ');
        return new ContainerState(fields[0], int.Parse(fields[1], CultureInfo.InvariantCulture));
    }

    /// <summary>Waits until the container's output holds <paramref name="text"/>.</summary>
    private async Task LoggedAsync(string dockerfile, string text, TimeSpan within)
    {
        var deadline = Stopwatch.StartNew();
        while (!(await LogsAsync()).Contains(text, StringComparison.Ordinal))
        {
            if (deadline.Elapsed > within)
            {
                Assert.Fail($"The app did not write '{text}' within {within.TotalSeconds} s. {await DescribeAsync(dockerfile, await StateAsync())}");
            }

            await Task.Delay(100, Ct);
        }
    }

    /// <summary>The container's state, how much CPU it takes while it runs, and its output.</summary>
    private async Task<string> DescribeAsync(string dockerfile, ContainerState state)
    {
        var cpu = state.Status == "running"
            ? (await DockerAsync(["stats", "--no-stream", "--format", "{{.CPUPerc}}", _container!], check: false)).Trim()
            : "none";
        return $"{dockerfile}: {state.Status} with exit code {state.ExitCode}, CPU {cpu}. Its output:\n{await LogsAsync()}";
    }

    /// <summary>The container's standard output, then its standard error.</summary>
    private async Task<string> LogsAsync()
    {
        var (output, error) = await RunAsync("docker", ["logs", _container!], TimeSpan.FromMinutes(1), check: true, Ct);
        return output + error;
    }

    private string Write(string path, string content)
    {
        var full = Path.Combine(_dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Runs <c>docker</c> and returns its standard output.</summary>
    private static async Task<string> DockerAsync(string[] args, TimeSpan? timeout = null, bool check = true, CancellationToken? ct = null) =>
        (await RunAsync("docker", args, timeout ?? TimeSpan.FromMinutes(1), check, ct ?? Ct)).Output;

    /// <summary>Runs a command to its end; with <paramref name="check"/>, fails the test with its output unless it exits with 0.</summary>
    private static async Task<(string Output, string Error)> RunAsync(string file, string[] args, TimeSpan timeout, bool check, CancellationToken ct)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException($"{file} {string.Join(' ', args)} did not finish within {timeout.TotalMinutes} min:\n{await output}{await error}");
        }

        var (stdout, stderr) = (await output, await error);
        if (check && process.ExitCode != 0)
        {
            Assert.Fail($"{file} {string.Join(' ', args)} exited with {process.ExitCode}:\n{stdout}{stderr}");
        }

        return (stdout, stderr);
    }

    private static string Dotnet() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet"));

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

    private readonly record struct ContainerState(string Status, int ExitCode);
}
