using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Netjes.Rendering;

/// <summary>
/// What a document needs that its Content-Security-Policy refuses, for a warning: Netjes's inline bootstrap, the
/// document's module scripts and, in Development, the Vite dev server's scripts, its HMR socket and the styles inlined
/// from it. The policy goes out as written either way. Sources are matched as browsers match them (CSP Level 3, and
/// as Chromium and Firefox do where it differs), and a source that may allow what the document needs counts: the
/// checks warn about what no source of the list can allow, not about everything that could go wrong.
/// </summary>
internal static partial class CspChecks
{
    private static readonly string[] ScriptHashes = NetjesContentSecurityPolicy.ScriptHashes.Split(' ');

    /// <summary>
    /// What the policies in the header's values refuse, each policy on its own, since browsers enforce every one (several
    /// in one value are joined by commas). A finding that two policies share is listed once.
    /// </summary>
    public static IReadOnlyList<CspFinding> Check(StringValues policies, CspDocument document)
    {
        var self = Origin.Of(document.Scheme, new HostString(document.Host));
        Origin? devServer = document.DevServer is { } origin && Origin.TryParse(origin, out var parsed) ? parsed : null;
        var findings = new List<CspFinding>();
        foreach (var policy in CspDirectives.Policies(policies))
        {
            CheckScripts(policy, document.Scripts, self, devServer, findings);
            if (devServer is { } server)
            {
                CheckSocket(policy, server, self, findings);
                CheckStyles(policy, findings);
            }
        }

        return [.. findings.Distinct()];
    }

    /// <summary>The policy with each nonce source replaced by one value: what is the same in every document under it.</summary>
    public static string WithoutNonces(string policy) => NonceSource().Replace(policy, "'nonce'");

    /// <summary>
    /// Script elements, and the modules they load, follow <c>script-src-elem</c>, else <c>script-src</c>, else
    /// <c>default-src</c>. Netjes puts the document's nonce on every script it writes, and modules inherit it from the
    /// script that loads them, so a list with a nonce is not judged.
    /// </summary>
    private static void CheckScripts(string policy, IReadOnlyList<string> scripts, Origin self, Origin? devServer, List<CspFinding> findings)
    {
        if (Governing(policy, "script-src-elem", "script-src", "default-src") is not { } directive || directive.Sources.Any(IsNonce))
        {
            return;
        }

        if (!ScriptHashes.All(hash => directive.Sources.Any(source => IsTheHash(source, hash))) && !AllowsInline(directive.Sources, scripts: true))
        {
            findings.Add(new(CspProblem.Bootstrap, directive.Name));
        }

        if (directive.Sources.Any(source => Is(source, "'strict-dynamic'")))
        {
            // Next to it, browsers ignore 'self' and host sources, so it decides for every module script.
            findings.Add(new(CspProblem.StrictDynamicWithoutNonce, directive.Name));
            return;
        }

        if (scripts.FirstOrDefault(url => IsOn(url, self)) is { } own && !directive.Sources.Any(MayAllowTheAppsOwnScripts))
        {
            findings.Add(new(CspProblem.ModuleScripts, directive.Name, own));
        }

        if (devServer is { } server && server != self && !directive.Sources.Any(source => Matches(source, server, self)))
        {
            findings.Add(new(CspProblem.DevServerScripts, directive.Name, server.ToString()));
        }
    }

    /// <summary>
    /// Vite's client opens its HMR socket on the origin it was loaded from, ws for http and wss for https, unless
    /// <c>server.hmr</c> in the Vite config moves it, which Netjes cannot see. Connections follow <c>connect-src</c>,
    /// else <c>default-src</c>, and a nonce does not apply to them.
    /// </summary>
    private static void CheckSocket(string policy, Origin devServer, Origin self, List<CspFinding> findings)
    {
        var socket = devServer with { Scheme = devServer.Scheme == "https" ? "wss" : "ws" };
        if (Governing(policy, "connect-src", "default-src") is { } directive && !directive.Sources.Any(source => Matches(source, socket, self)))
        {
            findings.Add(new(CspProblem.HmrSocket, directive.Name, socket.ToString()));
        }
    }

    /// <summary>
    /// In Development a document carries the app's CSS in <c>&lt;style&gt;</c> elements, with the nonce when there is
    /// one, and Vite's client adds more, with the nonce from the <c>csp-nonce</c> meta. Style elements follow
    /// <c>style-src-elem</c>, else <c>style-src</c>, else <c>default-src</c>.
    /// </summary>
    private static void CheckStyles(string policy, List<CspFinding> findings)
    {
        if (Governing(policy, "style-src-elem", "style-src", "default-src") is { } directive
            && !directive.Sources.Any(IsNonce)
            && !AllowsInline(directive.Sources, scripts: false))
        {
            findings.Add(new(CspProblem.DevStyles, directive.Name));
        }
    }

    private sealed record Directive(string Name, IReadOnlyList<string> Sources);

    /// <summary>The first of the named directives that the policy has: browsers fall back from each to the next.</summary>
    private static Directive? Governing(string policy, params ReadOnlySpan<string> names)
    {
        foreach (var name in names)
        {
            if (CspDirectives.Sources(policy, name) is { } sources)
            {
                return new Directive(name, sources);
            }
        }

        return null;
    }

    /// <summary>The hash as <see cref="NetjesContentSecurityPolicy.ScriptHashes"/> writes it: its algorithm's name ignoring case, its value exactly.</summary>
    private static bool IsTheHash(string source, string hash)
    {
        var value = hash.IndexOf('-') + 1;
        return source.Length == hash.Length
            && source.AsSpan(0, value).Equals(hash.AsSpan(0, value), StringComparison.OrdinalIgnoreCase)
            && source.AsSpan(value).SequenceEqual(hash.AsSpan(value));
    }

    /// <summary>
    /// <c>'unsafe-inline'</c>, which browsers ignore next to a hash or a nonce, and in a list of scripts next to
    /// <c>'strict-dynamic'</c> too.
    /// </summary>
    private static bool AllowsInline(IReadOnlyList<string> sources, bool scripts) =>
        sources.Any(source => Is(source, "'unsafe-inline'"))
        && !sources.Any(source => IsHash(source) || IsNonce(source) || (scripts && Is(source, "'strict-dynamic'")));

    /// <summary>
    /// Whether the source may allow a script that the app serves itself. The request's host is not always the one the
    /// browser sees (behind a proxy), so any host source may name it; only a list with none of them, <c>'self'</c>,
    /// <c>*</c>, <c>http:</c> or <c>https:</c> is sure to refuse it.
    /// </summary>
    private static bool MayAllowTheAppsOwnScripts(string source) =>
        Is(source, "'self'") || (!source.StartsWith('\'') && (!IsSchemeSource(source, out var scheme) || scheme is "http" or "https"));

    /// <summary>A path, or an absolute URL on the request's origin.</summary>
    private static bool IsOn(string url, Origin self) =>
        !Origin.TryParse(url.StartsWith("//", StringComparison.Ordinal) ? $"{self.Scheme}:{url}" : url, out var origin) || origin == self;

    /// <summary>
    /// Whether the source allows <paramref name="url"/> on a page at <paramref name="self"/>. The scheme of a source
    /// matches itself and its secure form (http matches https, ws matches wss, and http never matches ws), and a source
    /// without a scheme has the page's; a port matches itself, <c>*</c> any port, and none the scheme's default. The
    /// path of a source is not judged.
    /// </summary>
    private static bool Matches(string source, Origin url, Origin self)
    {
        if (source == "*")
        {
            // Every network scheme; others, such as data:, only when they are named.
            return url.Scheme is "http" or "https" or "ws" or "wss";
        }

        if (Is(source, "'self'"))
        {
            // The page's own host and port, a socket there included.
            return url.Host == self.Host
                && (url.Port == self.Port || (url.Port == DefaultPort(url.Scheme) && self.Port == DefaultPort(self.Scheme)))
                && (url.Scheme == self.Scheme || url.Scheme is "https" or "wss" || (self.Scheme == "http" && url.Scheme == "ws"));
        }

        if (source.StartsWith('\''))
        {
            // Another keyword, a nonce or a hash: none of them names a URL.
            return false;
        }

        if (IsSchemeSource(source, out var only))
        {
            return SchemeMatches(only, url.Scheme);
        }

        // A host source: [scheme "://"] host [":" port] [path].
        var scheme = self.Scheme;
        var rest = source;
        var separator = rest.IndexOf("://", StringComparison.Ordinal);
        if (separator >= 0)
        {
            scheme = rest[..separator].ToLowerInvariant();
            rest = rest[(separator + 3)..];
        }

        var path = rest.IndexOf('/');
        if (path >= 0)
        {
            rest = rest[..path];
        }

        string? port = null;
        var colon = rest.LastIndexOf(':');
        if (colon >= 0)
        {
            port = rest[(colon + 1)..];
            rest = rest[..colon];
        }

        return SchemeMatches(scheme, url.Scheme) && HostMatches(rest, url.Host) && PortMatches(port, url);
    }

    private static bool SchemeMatches(string source, string url) =>
        source == url || (source == "http" && url == "https") || (source == "ws" && url == "wss");

    /// <summary>The host ignoring case, any host for <c>*</c>, and for <c>*.example.com</c> any host below <c>example.com</c>.</summary>
    private static bool HostMatches(string pattern, string host) =>
        pattern == "*"
        || (pattern.StartsWith("*.", StringComparison.Ordinal) && host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase))
        || pattern.Equals(host, StringComparison.OrdinalIgnoreCase);

    private static bool PortMatches(string? port, Origin url) =>
        port is null
            ? url.Port == DefaultPort(url.Scheme)
            : port == "*" || (int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number == url.Port);

    private static int DefaultPort(string scheme) => scheme switch
    {
        "http" or "ws" => 80,
        "https" or "wss" => 443,
        _ => -1,
    };

    /// <summary>A scheme source, such as <c>https:</c>: RFC 3986's scheme and a colon. Its scheme comes out lower-case.</summary>
    private static bool IsSchemeSource(string source, out string scheme)
    {
        scheme = "";
        if (source.Length < 2 || source[^1] != ':' || !char.IsAsciiLetter(source[0])
            || !source[1..^1].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
        {
            return false;
        }

        scheme = source[..^1].ToLowerInvariant();
        return true;
    }

    private static bool IsNonce(string source) => IsQuoted(source, "'nonce-");

    private static bool IsHash(string source) => IsQuoted(source, "'sha256-") || IsQuoted(source, "'sha384-") || IsQuoted(source, "'sha512-");

    private static bool IsQuoted(string source, string prefix) =>
        source.Length > prefix.Length && source[^1] == '\'' && source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>A keyword, compared ignoring case as browsers do.</summary>
    private static bool Is(string source, string keyword) => source.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>A URL's scheme, host and port, the scheme's default port when it names none, in lower case.</summary>
    private readonly record struct Origin(string Scheme, string Host, int Port)
    {
        public static Origin Of(string scheme, HostString host)
        {
            var lower = scheme.ToLowerInvariant();
            return new(lower, host.Host.ToLowerInvariant(), host.Port ?? DefaultPort(lower));
        }

        /// <summary>The origin of an absolute http or https URL.</summary>
        public static bool TryParse(string url, out Origin origin)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                origin = new(uri.Scheme, uri.Host.ToLowerInvariant(), uri.Port);
                return true;
            }

            origin = default;
            return false;
        }

        /// <summary>As browsers write it, without the port when it is the scheme's default.</summary>
        public override string ToString() => Port == DefaultPort(Scheme) ? $"{Scheme}://{Host}" : $"{Scheme}://{Host}:{Port}";
    }

    /// <summary>One warning for each finding, at the logger of the renderer, which found it.</summary>
    public static void Warn(ILogger logger, CspFinding finding)
    {
        switch (finding.Problem)
        {
            case CspProblem.Bootstrap:
                logger.LogWarning(
                    "The Content-Security-Policy's {Directive} refuses the inline script that Netjes writes into the head of every " +
                    "document, which collects the values Page.Defer streams: it holds neither the hashes in " +
                    "NetjesContentSecurityPolicy.ScriptHashes nor a nonce, and allows no inline scripts ('unsafe-inline' does not " +
                    "count next to a hash, a nonce or 'strict-dynamic'). Add NetjesContentSecurityPolicy.ScriptHashes to " +
                    "{Directive} (in JavaScript, CSP_SCRIPT_HASHES from netjes), or use a nonce.",
                    finding.Directive, finding.Directive);
                break;
            case CspProblem.StrictDynamicWithoutNonce:
                logger.LogWarning(
                    "The Content-Security-Policy's {Directive} has 'strict-dynamic' but no nonce. Next to 'strict-dynamic', " +
                    "browsers ignore 'self' and host sources, and the document's module scripts carry neither a nonce nor an " +
                    "integrity attribute for a hash to match, so browsers refuse them and the page does not hydrate. Remove " +
                    "'strict-dynamic', or use a nonce: 'nonce-{{nonce}}' in NetjesOptions.ContentSecurityPolicy, or " +
                    "HttpContext.GetCspNonce() in a policy the app writes.",
                    finding.Directive);
                break;
            case CspProblem.ModuleScripts:
                logger.LogWarning(
                    "The Content-Security-Policy's {Directive} allows none of the document's module scripts, which the app serves " +
                    "itself, such as {Script}, so browsers refuse them and the page does not hydrate. Add 'self' to {Directive}, " +
                    "or use a nonce.",
                    finding.Directive, finding.Url, finding.Directive);
                break;
            case CspProblem.DevServerScripts:
                logger.LogWarning(
                    "In Development, the document's module scripts come from the Vite dev server at {Origin}, which the " +
                    "Content-Security-Policy's {Directive} does not allow, so browsers refuse them and the page does not hydrate. " +
                    "Add {Origin} to {Directive} in Development, use a nonce, or send this policy only outside Development.",
                    finding.Url, finding.Directive, finding.Url, finding.Directive);
                break;
            case CspProblem.HmrSocket:
                logger.LogWarning(
                    "In Development, Vite's client opens its HMR WebSocket at {Socket}, which the Content-Security-Policy's " +
                    "{Directive} does not allow, so browsers refuse it and edits reach the page only when it is reloaded. Add " +
                    "{Socket} to {Directive} in Development, or send this policy only outside Development. If server.hmr in the " +
                    "Vite config puts the socket elsewhere, allow that address instead.",
                    finding.Url, finding.Directive, finding.Url, finding.Directive);
                break;
            case CspProblem.DevStyles:
                logger.LogWarning(
                    "In Development, the document carries the app's CSS in inline <style> elements, and Vite's client adds more of " +
                    "them, but the Content-Security-Policy's {Directive} holds no nonce and allows no inline styles, so browsers " +
                    "refuse them and pages show without their styles. For Development, put a nonce or 'unsafe-inline' in " +
                    "{StyleDirective} ('unsafe-inline' does not count next to a hash or a nonce), or send this policy only " +
                    "outside Development.",
                    finding.Directive, finding.Directive == "default-src" ? "style-src" : finding.Directive);
                break;
        }
    }

    [GeneratedRegex("'nonce-[^']*'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NonceSource();
}

/// <summary>What a document needs, as <see cref="CspChecks"/> reads it.</summary>
/// <param name="Scripts">The URLs of its module scripts, as the document writes them.</param>
/// <param name="Scheme">The request's scheme.</param>
/// <param name="Host">The request's host, with its port when it has one.</param>
/// <param name="Nonce">The nonce Netjes puts on the scripts and styles it writes into the document; null when it has none.</param>
/// <param name="DevServer">In Development, the origin of the Vite dev server, which serves the scripts there; otherwise null.</param>
internal sealed record CspDocument(IReadOnlyList<string> Scripts, string Scheme, string Host, string? Nonce, string? DevServer);

/// <summary>What a Content-Security-Policy refuses of what a document needs.</summary>
internal enum CspProblem
{
    /// <summary>The inline bootstrap: no hash of it, no nonce, and no inline scripts allowed.</summary>
    Bootstrap,

    /// <summary><c>'strict-dynamic'</c> without a nonce, which leaves the module scripts nothing to be allowed by.</summary>
    StrictDynamicWithoutNonce,

    /// <summary>The module scripts the app serves itself.</summary>
    ModuleScripts,

    /// <summary>In Development, the module scripts from the Vite dev server.</summary>
    DevServerScripts,

    /// <summary>In Development, the HMR WebSocket of Vite's client.</summary>
    HmrSocket,

    /// <summary>In Development, the inlined styles and those Vite's client writes.</summary>
    DevStyles,
}

/// <summary>A problem, the directive whose sources refuse it, and the URL it is about, when there is one.</summary>
internal sealed record CspFinding(CspProblem Problem, string Directive, string? Url = null);
