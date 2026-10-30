namespace Netjes.Sidecar;

/// <summary>
/// The Vite dev server did not start in development, for the reason Vite gives (its code frame included): most often
/// an error in vite.config, or a package it imports that is not installed.
/// </summary>
internal sealed class ViteDevServerFailedException(string reason, bool packageMissing, string root)
    : NetjesStartupException(
        $"The Vite dev server did not start: {reason.TrimEnd()}\n" +
        (packageMissing ? $"Run `npm install` in '{root}', then start the app again." : "Fix it, then start the app again."));
