using System.Text;

namespace Netjes.Rendering;

/// <param name="WaitForAll">Send nothing until every Suspense boundary has resolved (for crawlers and the static export).</param>
/// <param name="RenderId">Set when the payload has deferred values; <see cref="ISsrRenderer.ResolveAsync"/> delivers them.</param>
public sealed record SsrStreamOptions(bool WaitForAll, string? RenderId = null);

/// <summary>Renders a page payload (HTML-safe JSON) to a complete HTML document.</summary>
public interface ISsrRenderer
{
    /// <summary>Renders the page payload to a complete HTML document.</summary>
    Task<string> RenderAsync(string payloadJson, CancellationToken cancellationToken);

    /// <summary>
    /// Renders as a stream that carries the shell first and each Suspense boundary as it resolves. Throws, before
    /// returning, if the shell fails. The default buffers <see cref="RenderAsync"/>.
    /// </summary>
    async Task<Stream> RenderStreamAsync(string payloadJson, SsrStreamOptions options, CancellationToken cancellationToken) =>
        new MemoryStream(Encoding.UTF8.GetBytes(await RenderAsync(payloadJson, cancellationToken)), writable: false);

    /// <summary>
    /// Delivers a deferred value (its JSON, or an error message) to the render with that id. Called as soon as
    /// <see cref="RenderStreamAsync"/> has been called, before it returns: a complete document needs its values first.
    /// </summary>
    Task ResolveAsync(string renderId, string id, string? valueJson, string? error, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
