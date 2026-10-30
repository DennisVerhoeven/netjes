using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Netjes.Rendering;

/// <summary>
/// The document .NET writes itself when no Node sidecar is ready to render one (ADR 0013): the head the
/// <c>Document</c> component renders (the assets, the CSP nonce, the deferred-values bootstrap) with the title,
/// description and robots from C#, and a body with only the payload. Its <c>document.clientRender</c> tells the client
/// entry to render the page with <c>createRoot</c> rather than hydrate it.
/// </summary>
internal static class ClientRenderShell
{
    /// <summary>
    /// <c>DEFERRED_BOOTSTRAP</c> from <c>packages/netjes/src/deferred.ts</c>, character for character, so a hash-based
    /// Content-Security-Policy allows it by the same hash.
    /// </summary>
    internal const string DeferredBootstrap =
        "self.__netjes_d=self.__netjes_d||function(i,v,e){self.__netjes_v[i]=[v,e]};Object.prototype.hasOwnProperty.call(self,'__netjes_v')||(self.__netjes_v={})";

    public const string End = "</body></html>";

    /// <summary>Everything up to the end of the payload; deferred values and <see cref="End"/> follow.</summary>
    /// <param name="payloadJson">The payload, already escaped for a script element (<see cref="HtmlSafeJson"/>).</param>
    public static string Start(DocumentPayload document, PageMetadata? metadata, string payloadJson)
    {
        var nonce = NonceAttribute(document.Nonce);
        var html = new StringBuilder(1024 + payloadJson.Length)
            .Append("<!DOCTYPE html><html lang=\"").Append(Encode(document.Lang)).Append("\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        if (document.Nonce is not null)
        {
            // Vite reads the nonce from here for the styles it injects.
            html.Append("<meta property=\"csp-nonce\"").Append(nonce).Append('>');
        }

        html.Append("<script").Append(nonce).Append('>').Append(DeferredBootstrap).Append("</script>");
        if (metadata?.Title is { } title)
        {
            html.Append("<title>").Append(Encode(title)).Append("</title>");
        }

        if (metadata?.Description is { } description)
        {
            html.Append("<meta name=\"description\" content=\"").Append(Encode(description)).Append("\">");
        }

        if (metadata?.Robots is { } robots)
        {
            // A page the loader keeps out of search results stays out while it renders in the browser.
            html.Append("<meta name=\"robots\" content=\"").Append(robots.Index ? "index" : "noindex")
                .Append(robots.Follow ? ", follow" : ", nofollow").Append("\">");
        }

        foreach (var href in document.Assets.Stylesheets)
        {
            html.Append("<link rel=\"stylesheet\" href=\"").Append(Encode(href)).Append('"').Append(nonce).Append('>');
        }

        foreach (var href in document.Assets.ModulePreloads)
        {
            html.Append("<link rel=\"modulepreload\" href=\"").Append(Encode(href)).Append('"').Append(nonce).Append('>');
        }

        foreach (var src in document.Assets.Scripts)
        {
            html.Append("<script type=\"module\" src=\"").Append(Encode(src)).Append("\" async").Append(nonce).Append("></script>");
        }

        // The client entry waits until something follows the payload (or the document has loaded), so that it reads
        // the whole of it: here that is the comment, as the deferred values may be a while.
        return html.Append("</head><body><script id=\"__NETJES_DATA__\" type=\"application/json\"").Append(nonce).Append('>')
            .Append(payloadJson).Append("</script><!-- No Node sidecar was ready: this page renders in the browser. -->")
            .ToString();
    }

    /// <summary>
    /// The script that hands one deferred value (its JSON) or its error to the page, as <c>deferredScript</c> in
    /// <c>deferred.ts</c> writes it into a server-rendered document: the value goes as JSON text for <c>JSON.parse</c>,
    /// so a <c>"__proto__"</c> key stays a key.
    /// </summary>
    public static string Value(string id, string? valueJson, string? error, string? nonce)
    {
        var args = error is null
            ? $"{Literal(id)},JSON.parse({Literal(valueJson ?? "null")})"
            : $"{Literal(id)},null,{Literal(error)}";
        return $"<script{NonceAttribute(nonce)}>self.__netjes_d({args})</script>";
    }

    /// <summary>A JavaScript string literal that can sit inside a script element.</summary>
    private static string Literal(string value) => HtmlSafeJson.Escape(JsonSerializer.Serialize(value));

    /// <summary>The nonce as the client writes it: base64, so nothing else is kept, and nothing needs escaping.</summary>
    private static string NonceAttribute(string? nonce) => nonce is null
        ? ""
        : $" nonce=\"{new string([.. nonce.Where(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' or '_' or '-')])}\"";

    private static string Encode(string value) => HtmlEncoder.Default.Encode(value);
}
