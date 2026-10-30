using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace TaskBoard.Tests;

/// <summary>Sign-in and sign-up are limited per account and per client address, and answer 429 past the limit.</summary>
[Collection(TaskBoardCollection.Name)]
[Trait("Category", "Docker")]
public sealed class AuthLimitTests(TaskBoardApp app)
{
    private static string NewEmail() => $"user-{Guid.NewGuid():N}@taskboard.test";

    /// <summary>A TaskBoard of its own (its limits counted from zero) with these per-address limits.</summary>
    private WebApplicationFactory<Program> WithLimits(int signInsPerAddress = 1000, int signUpsPerAddress = 1000) =>
        app.WithWebHostBuilder(b => b
            .UseSetting("AuthLimits:SignInsPerAddress", signInsPerAddress.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .UseSetting("AuthLimits:SignUpsPerAddress", signUpsPerAddress.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static async Task<HttpStatusCode> FailedLoginAsync(Browser browser) =>
        (await browser.ActionAsync("auth.login", new { email = NewEmail(), password = "wrong password!" })).Status;

    [Fact]
    public async Task Login_PastTheLimitForOneAddress_Is429_WhileOtherAddressesGoOn()
    {
        await using var limited = WithLimits(signInsPerAddress: 3);
        var browser = Browser.At(limited, from: "198.51.100.20");

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, await FailedLoginAsync(browser));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await FailedLoginAsync(browser));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await FailedLoginAsync(Browser.At(limited, from: "198.51.100.21")));
    }

    [Fact]
    public async Task Login_IPv6AddressesInOne64_ShareTheirLimit()
    {
        await using var limited = WithLimits(signInsPerAddress: 3);

        for (var host = 1; host <= 3; host++)
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, await FailedLoginAsync(Browser.At(limited, from: $"2001:db8:0:7::{host}")));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await FailedLoginAsync(Browser.At(limited, from: "2001:db8:0:7:ffff::9")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await FailedLoginAsync(Browser.At(limited, from: "2001:db8:0:8::1")));
    }

    [Fact]
    public async Task BehindTheProxy_TheVisitorsAddressIsLimited_NotTheProxys()
    {
        await using var limited = WithLimits(signInsPerAddress: 3);
        var visitor = Browser.At(limited, from: "203.0.113.60", via: "192.0.2.10");

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, await FailedLoginAsync(visitor));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await FailedLoginAsync(visitor));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await FailedLoginAsync(Browser.At(limited, from: "203.0.113.61", via: "192.0.2.10")));
    }

    [Fact]
    public async Task Register_PastTheLimitForOneAddress_Is429()
    {
        await using var limited = WithLimits(signUpsPerAddress: 2);

        // A new browser each time (one that signed up is signed in, and its token belonged to the anonymous visitor).
        async Task<HttpStatusCode> RegisterAsync() =>
            (await Browser.At(limited, from: "198.51.100.30")
                .ActionAsync("auth.register", new { name = "Limited", email = NewEmail(), password = "correct horse battery" })).Status;

        Assert.Equal(HttpStatusCode.OK, await RegisterAsync());
        Assert.Equal(HttpStatusCode.OK, await RegisterAsync());
        Assert.Equal(HttpStatusCode.TooManyRequests, await RegisterAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Login_TheEleventhAttemptOnOneAccountIn15Minutes_Is429_EvenWithTheRightPassword(bool accountExists)
    {
        var email = accountExists ? await app.NewBrowser().SignUpAsync() : NewEmail();
        var browser = app.NewBrowser();
        await browser.PageAsync("/login");

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var (failed, _) = await browser.ActionAsync("auth.login", new { email, password = "wrong password!" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, failed);
        }

        // The same email in another case and from another browser: it is the account that is limited.
        var (status, _) = await app.NewBrowser().ActionAsync("auth.login", new { email = email.ToUpperInvariant(), password = "correct horse battery" });
        var (otherAccount, _) = await app.NewBrowser().ActionAsync("auth.login", new { email = NewEmail(), password = "wrong password!" });

        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, otherAccount);
    }

    [Theory]
    [InlineData("SignInsPerAddress", "0")]
    [InlineData("SignInsPerAccount", "0")]
    [InlineData("SignUpsPerAddress", "-1")]
    public async Task ALimitBelowOne_StopsTheAppAtStartup(string limit, string value)
    {
        // Not a 500 on every sign-in later: a fixed window needs at least one permit.
        await using var misconfigured = app.WithWebHostBuilder(b => b.UseSetting($"AuthLimits:{limit}", value));

        var error = Assert.Throws<OptionsValidationException>(() => misconfigured.CreateClient());
        Assert.Contains(limit, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_AnEmailLongerThanAnAccountCanHave_IsTurnedAwayBeforeTheAccountLimitKeepsIt()
    {
        // Every account's email fits in 254 characters. A longer one is a field error and is never kept as a limiter
        // key (the per-account limiter keeps one per email for 15 minutes), so it is never counted either.
        var email = new string('a', 243) + "@example.com";
        var browser = app.NewBrowser();
        await browser.PageAsync("/login");

        for (var attempt = 1; attempt <= 11; attempt++)
        {
            var (status, response) = await browser.ActionAsync("auth.login", new { email, password = "wrong password!" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
            Assert.NotNull(response["errors"]!["email"]);
        }
    }
}
