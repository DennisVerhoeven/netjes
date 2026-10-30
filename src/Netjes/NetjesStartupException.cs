using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Netjes;

/// <summary>
/// The app cannot start, for a reason its message says in full, with what to do about it: something else has the Vite
/// dev server's port, say. There is nothing more to say, so the app reports it in one line rather than with its stack.
/// </summary>
internal class NetjesStartupException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

/// <summary>
/// The <see cref="NetjesStartupException"/> the app reports in one line, once the host has started, and stops for: set
/// while the host starts, so that the services that act on the started app, the export among them, leave it alone.
/// </summary>
internal sealed class PendingStartupFailure
{
    public NetjesStartupException? Exception { get; set; }
}

/// <summary>
/// A <see cref="NetjesStartupException"/> from the app's own startup code, such as <c>UseNetjes()</c> without a
/// production build: it comes before the host starts, so no hosted service can report it, and it is thrown into
/// <c>Program.cs</c>, where the app or a test host may catch it.
/// </summary>
internal static class UnhandledStartupFailure
{
    /// <summary>
    /// Logs <paramref name="failure"/> as one critical line and ends the process with exit code 1 if nothing catches
    /// it, rather than leave it to the runtime, which prints it with its stack and aborts the process (exit code 134 on
    /// Linux and macOS). Only for an app that is the program its process runs: a test host's process is not the app's.
    /// </summary>
    public static void ExitIfUnhandled(NetjesStartupException failure, IServiceProvider services, string category)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (!ReferenceEquals(e.ExceptionObject, failure))
            {
                return;
            }

            try
            {
                services.GetRequiredService<ILoggerFactory>().CreateLogger(category).LogCritical("{Message}", failure.Message);
                // The console logger writes from a thread of its own; disposed, it writes what it holds first.
                foreach (var provider in services.GetServices<ILoggerProvider>())
                {
                    provider.Dispose();
                }
            }
            catch (Exception)
            {
                Console.Error.WriteLine(failure.Message);
            }
            finally
            {
                Environment.Exit(1);
            }
        };
    }
}
