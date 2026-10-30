namespace Netjes.Sidecar;

/// <summary>
/// The Vite dev server cannot start in development: something else, most often another app's Vite dev server, has its
/// port.
/// </summary>
internal sealed class VitePortInUseException(int port)
    : NetjesStartupException(
        $"Port {port} is in use, and the Vite dev server needs it. Stop what is using it, or move Vite to another port " +
        $"with Netjes__ViteDevPort={port + 1} in the environment.");
