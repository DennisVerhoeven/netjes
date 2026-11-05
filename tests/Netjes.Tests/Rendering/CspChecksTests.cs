using Netjes.Rendering;

namespace Netjes.Tests.Rendering;

public class CspChecksTests
{
    /// <summary>
    /// A document of a client build, behind https: its module script is a file the app serves. It carries the nonce
    /// <c>abc</c>, as the two Development documents do, the one a policy written with <c>{nonce}</c> or
    /// <c>GetCspNonce()</c> holds; a list without a nonce is judged as if the document carried none.
    /// </summary>
    private static readonly CspDocument Built = new(["/assets/client-entry-AAA.js"], "https", "www.example.com", Nonce: "abc", DevServer: null);

    /// <summary>A document in Development, on <c>http://localhost:5000</c>: its scripts come from the Vite dev server.</summary>
    private static readonly CspDocument Development = new(
        ["http://localhost:5173/@vite/client", "http://localhost:5173/@netjes/client-entry"], "http", "localhost:5000", "abc", "http://localhost:5173");

    /// <summary>Development behind https, with the dev server at another address (<c>NETJES_VITE_ORIGIN</c>).</summary>
    private static readonly CspDocument DevelopmentOverHttps = new(
        ["https://vite.example.test/@vite/client", "https://vite.example.test/@netjes/client-entry"], "https", "app.example.test", "abc", "https://vite.example.test");

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
    // The document's nonce: Netjes puts it on every script it writes, so nothing is judged.
    [InlineData("script-src 'nonce-abc' 'strict-dynamic'", "")]
    [InlineData("script-src 'NONCE-abc'", "")]
    [InlineData("script-src 'nonce-abc' 'sha256-AAAA'", "")]
    [InlineData("script-src 'nonce-xyz' 'nonce-abc'", "")]
    // Another nonce is on none of them and turns 'unsafe-inline' off, so the rest of the list decides, and when that
    // refuses a script, the nonce is what to change. Its value is read as written.
    [InlineData("script-src 'nonce-ABC'", "OtherNonce script-src")]
    [InlineData("script-src 'nonce-xyz' 'strict-dynamic' ScriptHashes", "OtherNonce script-src")]
    [InlineData("script-src 'nonce-xyz' 'unsafe-inline' 'self'", "OtherNonce script-src")]
    [InlineData("script-src 'nonce-xyz' 'self' ScriptHashes", "")]
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
    // They are on another origin than the app's, so they need no 'self'.
    [InlineData("script-src ScriptHashes", "DevServerScripts script-src http://localhost:5173")]
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
    [InlineData("script-src 'nonce-xyz' 'self' ScriptHashes", "OtherNonce script-src")]
    [InlineData("script-src 'nonce-xyz' http://localhost:5173 ScriptHashes", "")]
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
    // Another nonce is on none of the styles, and turns 'unsafe-inline' off.
    [InlineData("script-src 'nonce-abc'; style-src 'nonce-xyz'", "OtherNonce style-src")]
    [InlineData("script-src 'nonce-abc'; style-src 'nonce-xyz' 'unsafe-inline'", "OtherNonce style-src")]
    [InlineData("default-src 'nonce-xyz'", "OtherNonce default-src, HmrSocket default-src ws://localhost:5173")]
    public void Check_ForADocumentInDevelopment_FindsWhatThePolicyRefuses(string policy, string findings) =>
        Assert.Equal(findings, Findings(policy, Development));

    [Theory]
    // Netjes made no nonce for the document, as when the app writes a nonce of its own into the policy, without
    // GetCspNonce(): any nonce in the policy is another.
    [InlineData(false, "script-src 'nonce-abc'", "OtherNonce script-src")]
    [InlineData(false, "script-src 'nonce-abc' 'self' ScriptHashes", "")]
    [InlineData(false, "script-src 'self' ScriptHashes", "")]
    [InlineData(true, "script-src 'self' http://localhost:5173 ScriptHashes; style-src 'nonce-abc'", "OtherNonce style-src")]
    [InlineData(true, "script-src 'self' http://localhost:5173 ScriptHashes; style-src 'unsafe-inline'", "")]
    public void Check_ForADocumentWithoutANonce_CountsNoNonceInThePolicy(bool inDevelopment, string policy, string findings) =>
        Assert.Equal(findings, Findings(policy, (inDevelopment ? Development : Built) with { Nonce = null }));

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
    [InlineData("script-src ScriptHashes", "DevServerScripts script-src https://vite.example.test")]
    [InlineData("script-src https://vite.example.test:8443 ScriptHashes", "DevServerScripts script-src https://vite.example.test")]
    public void Check_ForADocumentInDevelopmentOverHttps_FindsWhatThePolicyRefuses(string policy, string findings) =>
        Assert.Equal(findings, Findings(policy, DevelopmentOverHttps));

    [Theory]
    // On the app's own origin, as a proxy in front of both could put it, 'self' is the dev server too, for its scripts
    // and its socket.
    [InlineData("http://localhost:5000", "http://localhost:5000", "script-src 'self' ScriptHashes; connect-src 'self'", "")]
    [InlineData("http://localhost:5000", "http://localhost:5000", "script-src ScriptHashes; connect-src 'self'", "ModuleScripts script-src http://localhost:5000/@vite/client")]
    // 'self' also matches the secure scheme on the app's host, wss for the socket, on the same port or on the default
    // ports of both.
    [InlineData("https://app.example.test", "https://app.example.test", "script-src 'self' ScriptHashes; connect-src 'self'", "")]
    [InlineData("http://app.example.test", "https://app.example.test", "script-src 'self' ScriptHashes; connect-src 'self'", "")]
    // But not the other way round, nor from another port.
    [InlineData("https://app.example.test", "http://app.example.test", "script-src 'self' ScriptHashes; connect-src 'self'",
        "DevServerScripts script-src http://app.example.test, HmrSocket connect-src ws://app.example.test")]
    [InlineData("http://app.example.test:8080", "https://app.example.test", "script-src 'self' ScriptHashes; connect-src 'self'",
        "DevServerScripts script-src https://app.example.test, HmrSocket connect-src wss://app.example.test")]
    public void Check_WithTheDevServerOnTheAppsHost_CountsSelfAsBrowsersDo(string page, string devServer, string policy, string findings)
    {
        var app = new Uri(page);
        var document = new CspDocument([$"{devServer}/@vite/client", $"{devServer}/@netjes/client-entry"], app.Scheme, app.Authority, Nonce: null, devServer);

        Assert.Equal(findings, Findings(policy, document));
    }

    [Theory]
    // The document's nonce is abc.
    [InlineData("script-src 'nonce-abc' 'strict-dynamic'", "script-src 'nonce' 'strict-dynamic'")]
    [InlineData("script-src 'NONCE-abc'; style-src 'nonce-abc'", "script-src 'nonce'; style-src 'nonce'")]
    [InlineData("script-src 'nonce-def'; style-src 'nonce-abc' 'nonce-ABC'", "script-src 'other-nonce'; style-src 'nonce' 'other-nonce'")]
    [InlineData("script-src 'self'", "script-src 'self'")]
    public void WithoutNonces_PutsTheDocumentsNonceAsOneValue_AndAnyOtherAsAnother(string policy, string key) =>
        Assert.Equal(key, CspChecks.WithoutNonces(policy, "abc"));

    [Fact]
    public void WithoutNonces_ForADocumentWithoutANonce_PutsEveryNonceAsAnother() =>
        Assert.Equal("script-src 'other-nonce'; style-src 'other-nonce'", CspChecks.WithoutNonces("script-src 'nonce-abc'; style-src 'nonce-def'", null));
}
