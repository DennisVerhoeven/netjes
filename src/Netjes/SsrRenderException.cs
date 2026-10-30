namespace Netjes;

/// <summary>Server rendering failed in the Node sidecar; <see cref="JsStack"/> holds the JavaScript stack when known.</summary>
public sealed class SsrRenderException(string message, string? jsStack, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>The JavaScript stack of the failure, or null when there is none.</summary>
    public string? JsStack { get; } = jsStack;
}
