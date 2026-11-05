using Netjes.Rendering;

namespace Netjes.Tests.Rendering;

public class CspChecksTests
{
    /// <summary>A document of a client build, behind https: its module script is a file the app serves.</summary>
    private static readonly CspDocument Built = new(["/assets/client-entry-AAA.js"], "https", "www.example.com", DevServer: null);

    /// <summary>A document in Development, on <c>http://localhost:5000</c>: its scripts come from the Vite dev server.</summary>
    private static readonly CspDocument Development = new(
        ["http://localhost:5173/@vite/client", "http://localhost:5173/@netjes/client-entry"], "http", "localhost:5000", "http://localhost:5173");

    /// <summary>Development behind https, with the dev server at another address (<c>NETJES_VITE_ORIGIN</c>).</summary>
    private static readonly CspDocument DevelopmentOverHttps = new(
        ["https://vite.example.test/@vite/client", "https://vite.example.test/@netjes/client-entry"], "https", "app.example.test", "https://vite.example.test");

    /// <summary>Development with the dev server on the app's own origin, as a proxy in front of both could put it.</summary>
    private static readonly CspDocument DevelopmentOnTheAppsOrigin = new(
        ["http://localhost:5000/@vite/client", "http://localhost:5000/@netjes/client-entry"], "http", "localhost:5000", "http://localhost:5000");

    /// <summary>
    /// What the checks find, one finding after the other: its problem, the directive whose sources were judged and the
    /// URL it names, if any. <c>ScriptHashes</c> in the policy stands for <see cref="NetjesContentSecurityPolicy.ScriptHashes"/>,
    /// and <c>SCRIPTHASHES</c> for the same hashes with their prefix in upper case.
    /// </summary>
    private static string Findings(string policy, CspDocument document) =>
        string.Join(", ", CspChecks.Check(WithScriptHashes(policy), document)
            .Select(f => f.Url is null ? $"{f.Problem} {f.Directive}" : $"{f.Problem} {f.Directive} {f.Url}"));

    private static string WithScriptHashes(string policy) =>
        policy
            .Replace("ScriptHashes", NetjesContentSecurityPolicy.ScriptHashes, StringComparison.Ordinal)
            .Replace("SCRIPTHASHES", NetjesContentSecurityPolicy.ScriptHashes.Replace("'sha256-", "'SHA256-", StringComparison.Ordinal), StringComparison.Ordinal);

    [Theory]
    // The bootstrap: every hash in ScriptHashes, or inline code allowed.
    [InlineData("script-src 'self' ScriptHashes", "")]
    [InlineData("script-src 'self' SCRIPTHASHES", "")]
    [InlineData("script-src 'self'", "Bootstrap script-src")]
    [InlineData("default-src 'self'", "Bootstrap default-src")]
    [InlineData("SCRIPT-SRC 'SELF'", "Bootstrap script-src")]
    [InlineData("script-src 'self' 'unsafe-inline'", "")]
    [InlineData("script-src 'self' 'UNSAFE-INLINE'", "")]
    [InlineData("default-src 'self' 'unsafe-inline'", "")]
    // Next to a hash, browsers ignore 'unsafe-inline'.
    [InlineData("script-src 'self' 'unsafe-inline' 'sha256-AAAA'", "Bootstrap script-src")]
    [InlineData("script-src 'self' 'unsafe-inline' 'sha384-AAAA'", "Bootstrap script-src")]
    // Script elements follow script-src-elem, then script-src, then default-src.
    [InlineData("script-src 'unsafe-inline'; script-src-elem 'self'", "Bootstrap script-src-elem")]
    [InlineData("script-src-elem 'self' ScriptHashes; script-src 'none'", "")]
    [InlineData("default-src 'none'; script-src 'self' ScriptHashes", "")]
    [InlineData("img-src 'self'", "")]
    [InlineData("script-src 'none'", "Bootstrap script-src, ModuleScripts script-src /assets/client-entry-AAA.js")]
    // The module scripts: a host source may name the app's own host, which behind a proxy is not the request's.
    [InlineData("script-src ScriptHashes", "ModuleScripts script-src /assets/client-entry-AAA.js")]
    [InlineData("script-src https: ScriptHashes", "")]
    [InlineData("script-src HTTP: ScriptHashes", "")]
    [InlineData("script-src * ScriptHashes", "")]
    [InlineData("script-src cdn.example ScriptHashes", "")]
    [InlineData("script-src https://www.example.com/assets/ ScriptHashes", "")]
    [InlineData("script-src data: blob: ScriptHashes", "ModuleScripts script-src /assets/client-entry-AAA.js")]
    // 'strict-dynamic' without a nonce: 'self' and host sources no longer count.
    [InlineData("script-src 'self' 'strict-dynamic' ScriptHashes", "StrictDynamicWithoutNonce script-src")]
    [InlineData("script-src 'self' 'STRICT-DYNAMIC' ScriptHashes", "StrictDynamicWithoutNonce script-src")]
    [InlineData("script-src 'strict-dynamic' 'unsafe-inline' https:", "Bootstrap script-src, StrictDynamicWithoutNonce script-src")]
    // A nonce: Netjes puts it on every script it writes, so nothing is judged.
    [InlineData("script-src 'nonce-abc' 'strict-dynamic'", "")]
    [InlineData("script-src 'NONCE-abc'", "")]
    [InlineData("script-src 'nonce-abc' 'sha256-AAAA'", "")]
    // Several policies, each enforced; the same finding twice is one.
    [InlineData("object-src 'none', script-src 'self'", "Bootstrap script-src")]
    [InlineData("script-src 'self' ScriptHashes, script-src 'none'", "Bootstrap script-src, ModuleScripts script-src /assets/client-entry-AAA.js")]
    [InlineData("script-src 'self', script-src 'self'", "Bootstrap script-src")]
    // Without the dev server, neither the socket nor the styles are judged.
    [InlineData("script-src 'self' ScriptHashes; connect-src 'self'; style-src 'self'", "")]
    public void Check_ForABuiltDocument_FindsWhatThePolicyRefuses(string policy, string findings) =>
        Assert.Equal(findings, Findings(policy, Built));

    [Fact]
    public void Check_AHashWithItsValueInAnotherCase_DoesNotCount()
    {
        // The algorithm's name is read ignoring case, the base64 value is not.
        var lowerCase = $"script-src 'self' {NetjesContentSecurityPolicy.ScriptHashes.ToLowerInvariant()}";

        Assert.Equal([new CspFinding(CspProblem.Bootstrap, "script-src")], CspChecks.Check(lowerCase, Built));
    }

    [Theory]
    // The dev server's scripts: *, its scheme, or a host source naming its host and port.
    [InlineData("script-src 'self' ScriptHashes", "DevServerScripts script-src http://localhost:5173")]
    [InlineData("script-src 'self'", "Bootstrap script-src, DevServerScripts script-src http://localhost:5173")]
    [InlineData("script-src 'self' http://localhost:5173 ScriptHashes", "")]
    [InlineData("script-src localhost:5173 ScriptHashes", "")]
    [InlineData("script-src http://localhost:* ScriptHashes", "")]
    [InlineData("script-src localhost:* ScriptHashes", "")]
    [InlineData("script-src HTTP://LOCALHOST:5173 ScriptHashes", "")]
    [InlineData("script-src http://localhost:5173/ ScriptHashes", "")]
    [InlineData("script-src * ScriptHashes", "")]
    [InlineData("script-src http: ScriptHashes", "")]
    [InlineData("script-src https: ScriptHashes", "DevServerScripts script-src http://localhost:5173")]
    [InlineData("script-src https://localhost:5173 ScriptHashes", "DevServerScripts script-src http://localhost:5173")]
    [InlineData("script-src http://localhost ScriptHashes", "DevServerScripts script-src http://localhost:5173")]
    [InlineData("script-src http://localhost:5174 ScriptHashes", "DevServerScripts script-src http://localhost:5173")]
    [InlineData("script-src http://*.localhost:5173 ScriptHashes", "DevServerScripts script-src http://localhost:5173")]
    [InlineData("script-src ws://localhost:5173 ScriptHashes", "DevServerScripts script-src http://localhost:5173")]
    [InlineData("script-src 'self' 'strict-dynamic' ScriptHashes", "StrictDynamicWithoutNonce script-src")]
    [InlineData("script-src 'nonce-abc'", "")]
    [InlineData("object-src 'none'", "")]
    // The HMR socket: *, ws:, or a ws:// source naming the dev server's host and port; nonce or not.
    [InlineData("script-src 'nonce-abc'; connect-src 'self'", "HmrSocket connect-src ws://localhost:5173")]
    [InlineData("default-src 'self'; script-src 'nonce-abc'", "HmrSocket default-src ws://localhost:5173, DevStyles default-src")]
    [InlineData("script-src 'nonce-abc'; connect-src 'self' http://localhost:5173", "HmrSocket connect-src ws://localhost:5173")]
    [InlineData("script-src 'nonce-abc'; connect-src localhost:5173", "HmrSocket connect-src ws://localhost:5173")]
    [InlineData("script-src 'nonce-abc'; connect-src ws://localhost", "HmrSocket connect-src ws://localhost:5173")]
    [InlineData("script-src 'nonce-abc'; connect-src wss:", "HmrSocket connect-src ws://localhost:5173")]
    [InlineData("script-src 'nonce-abc'; connect-src wss://localhost:5173", "HmrSocket connect-src ws://localhost:5173")]
    [InlineData("script-src 'nonce-abc'; connect-src ws://localhost:5173", "")]
    [InlineData("script-src 'nonce-abc'; connect-src ws:", "")]
    [InlineData("script-src 'nonce-abc'; connect-src *", "")]
    [InlineData("script-src 'nonce-abc'; connect-src ws://localhost:*", "")]
    [InlineData("script-src 'nonce-abc'; CONNECT-SRC WS://LOCALHOST:5173", "")]
    [InlineData("script-src 'nonce-abc', connect-src 'self'", "HmrSocket connect-src ws://localhost:5173")]
    // The inlined and Vite-written styles: a nonce, or 'unsafe-inline' with no hash or nonce beside it.
    [InlineData("script-src 'nonce-abc'; style-src 'self'", "DevStyles style-src")]
    [InlineData("script-src 'nonce-abc'; style-src 'self' 'unsafe-inline' 'sha256-AAAA'", "DevStyles style-src")]
    [InlineData("script-src 'nonce-abc'; style-src 'unsafe-inline'; style-src-elem 'self'", "DevStyles style-src-elem")]
    [InlineData("script-src 'nonce-abc'; style-src 'self' 'unsafe-inline'", "")]
    [InlineData("script-src 'nonce-abc'; STYLE-SRC 'UNSAFE-INLINE'", "")]
    [InlineData("script-src 'nonce-abc'; style-src 'nonce-abc'", "")]
    [InlineData("script-src 'nonce-abc'; style-src 'self' 'unsafe-inline' 'nonce-abc'", "")]
    [InlineData("script-src 'nonce-abc'; style-src 'unsafe-inline' 'strict-dynamic'", "")]
    public void Check_ForADocumentInDevelopment_FindsWhatThePolicyRefuses(string policy, string findings) =>
        Assert.Equal(findings, Findings(policy, Development));

    [Theory]
    // Over https the socket is wss, which ws sources match too, and a source without a port means 443.
    [InlineData("script-src 'nonce-abc'; connect-src wss://vite.example.test", "")]
    [InlineData("script-src 'nonce-abc'; connect-src ws://vite.example.test", "")]
    [InlineData("script-src 'nonce-abc'; connect-src wss:", "")]
    [InlineData("script-src 'nonce-abc'; connect-src 'self'", "HmrSocket connect-src wss://vite.example.test")]
    [InlineData("script-src 'nonce-abc'; connect-src https://vite.example.test", "HmrSocket connect-src wss://vite.example.test")]
    [InlineData("script-src https://vite.example.test ScriptHashes", "")]
    [InlineData("script-src http://vite.example.test ScriptHashes", "")]
    [InlineData("script-src https: ScriptHashes", "")]
    [InlineData("script-src *.example.test ScriptHashes", "")]
    [InlineData("script-src 'self' ScriptHashes", "DevServerScripts script-src https://vite.example.test")]
    [InlineData("script-src https://vite.example.test:8443 ScriptHashes", "DevServerScripts script-src https://vite.example.test")]
    public void Check_ForADocumentInDevelopmentOverHttps_FindsWhatThePolicyRefuses(string policy, string findings) =>
        Assert.Equal(findings, Findings(policy, DevelopmentOverHttps));

    [Theory]
    // There 'self' is the dev server too, for its scripts and its socket.
    [InlineData("script-src 'self' ScriptHashes; connect-src 'self'", "")]
    [InlineData("script-src ScriptHashes; connect-src 'self'", "ModuleScripts script-src http://localhost:5000/@vite/client")]
    public void Check_WithTheDevServerOnTheAppsOrigin_CountsSelfForIt(string policy, string findings) =>
        Assert.Equal(findings, Findings(policy, DevelopmentOnTheAppsOrigin));

    [Theory]
    [InlineData("script-src 'nonce-abc' 'strict-dynamic'", "script-src 'nonce' 'strict-dynamic'")]
    [InlineData("script-src 'NONCE-abc'; style-src 'nonce-def'", "script-src 'nonce'; style-src 'nonce'")]
    [InlineData("script-src 'self'", "script-src 'self'")]
    public void WithoutNonces_ReplacesEveryNonce(string policy, string key) =>
        Assert.Equal(key, CspChecks.WithoutNonces(policy));
}
