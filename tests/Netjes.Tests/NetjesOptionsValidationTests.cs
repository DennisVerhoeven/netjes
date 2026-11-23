using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Netjes.Tests.Endpoints;

namespace Netjes.Tests;

/// <summary>Settings Netjes refuses as the app starts, with the option and the value in the message.</summary>
public class NetjesOptionsValidationTests
{
    private static NetjesOptions Resolve(Action<NetjesOptions>? configure = null, Dictionary<string, string?>? settings = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build());
        services.AddNetjes(configure);
        return services.BuildServiceProvider().GetRequiredService<IOptions<NetjesOptions>>().Value;
    }

    [Theory]
    [InlineData("*")]       // every cookie, the sign-in cookies aside
    [InlineData(" *")]      // nothing before the '*' but a space, which no cookie name starts with
    [InlineData("cart*x")]
    [InlineData("*cart")]
    [InlineData("cart**")]
    [InlineData(".app.*.cart")]
    public void ANoReloadCookiesEntry_ThatIsNeitherANameNorAPrefix_IsRefused(string entry)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(o => o.NoReloadCookies = ["cart", entry]));

        Assert.Contains("Netjes:NoReloadCookies", ex.Message);
        Assert.Contains($"'{entry}'", ex.Message);
        Assert.Contains("'.app.cart.*'", ex.Message); // the form that works
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void AnEmptyNoReloadCookiesEntry_IsRefused(string entry)
    {
        // A Set-Cookie without '=' is read by browsers as a cookie with an empty name.
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(o => o.NoReloadCookies = [entry]));

        Assert.Contains("Netjes:NoReloadCookies", ex.Message);
        Assert.Contains("empty", ex.Message);
    }

    [Fact]
    public void ANoReloadCookiesEntryFromConfiguration_IsCheckedToo()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(settings: new() { ["Netjes:NoReloadCookies:0"] = "*" }));

        Assert.Contains("'*'", ex.Message);
    }

    [Fact]
    public void EveryRefusedEntry_IsInTheMessage()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(o => o.NoReloadCookies = ["*", "a*b"]));

        Assert.Equal(2, ex.Failures.Count());
        Assert.Contains("'*'", ex.Message);
        Assert.Contains("'a*b'", ex.Message);
    }

    [Fact]
    public void NamesAndPrefixes_AreAccepted()
    {
        var options = Resolve(o => o.NoReloadCookies = ["cart", ".app.cart.*", "a*", "x.y"]);

        Assert.Equal(["cart", ".app.cart.*", "a*", "x.y"], options.NoReloadCookies);
    }

    [Fact]
    public async Task ARefusedNoReloadCookiesEntry_StopsTheAppAtStartup()
    {
        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            TestApp.StartAsync(["app/page.tsx"], configure: o => o.NoReloadCookies = [".app.cart*.store"]));

        Assert.Contains("'.app.cart*.store'", ex.Message);
    }
}
