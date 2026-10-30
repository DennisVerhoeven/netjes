using System.Net;

namespace TaskBoard.Tests;

[Collection(TaskBoardCollection.Name)]
[Trait("Category", "Docker")]
public sealed class AuthTests(TaskBoardApp app)
{
    private static string NewEmail() => $"user-{Guid.NewGuid():N}@taskboard.test";

    [Fact]
    public async Task Register_CreatesTheAccount_SignsIn_AndGoesToTheProjects()
    {
        var browser = app.NewBrowser();
        await browser.PageAsync("/register");

        var (status, response) = await browser.ActionAsync("auth.register", new { name = "Ada", email = NewEmail(), password = "correct horse battery" });
        var (projectsStatus, projects) = await browser.PageAsync("/projects");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("/projects", (string?)response["redirect"]);
        Assert.Equal(HttpStatusCode.OK, projectsStatus);
        Assert.Equal("Ada", (string?)projects["shared"]!["user"]!["name"]);
    }

    [Fact]
    public async Task Register_TakenEmail_InAnyCase_IsAFieldError()
    {
        var email = await app.NewBrowser().SignUpAsync();
        var browser = app.NewBrowser();
        await browser.PageAsync("/register");

        var (status, response) = await browser.ActionAsync("auth.register", new { name = "Bob", email = email.ToUpperInvariant(), password = "correct horse battery" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Contains("already", (string?)response["errors"]!["email"]![0]);
    }

    [Fact]
    public async Task Register_InvalidInput_ComesBackPerField()
    {
        var browser = app.NewBrowser();
        await browser.PageAsync("/register");

        var (status, response) = await browser.ActionAsync("auth.register", new { name = "", email = "not-an-email", password = "short" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.NotNull(response["errors"]!["name"]);
        Assert.NotNull(response["errors"]!["email"]);
        Assert.NotNull(response["errors"]!["password"]);
    }

    [Fact]
    public async Task Register_WithoutAName_IsAFieldError()
    {
        var browser = app.NewBrowser();
        await browser.PageAsync("/register");

        var (status, response) = await browser.ActionAsync("auth.register", new { email = NewEmail(), password = "correct horse battery" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.NotNull(response["errors"]!["name"]);
    }

    [Fact]
    public async Task Login_WithoutAPassword_IsAFieldError()
    {
        var browser = app.NewBrowser();
        await browser.PageAsync("/login");

        var (status, response) = await browser.ActionAsync("auth.login", new { email = NewEmail() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.NotNull(response["errors"]!["password"]);
    }

    [Fact]
    public async Task Login_WrongPassword_IsAnErrorWithoutSayingWhichPart()
    {
        var email = await app.NewBrowser().SignUpAsync();
        var browser = app.NewBrowser();
        await browser.PageAsync("/login");

        var (status, response) = await browser.ActionAsync("auth.login", new { email, password = "wrong password!" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("That email and password do not match.", (string?)response["errors"]!["password"]![0]);
    }

    [Theory]
    [InlineData("/projects/demo", "/projects/demo")]
    [InlineData("https://evil.example/", "/projects")]
    [InlineData("//evil.example/", "/projects")]
    [InlineData(null, "/projects")]
    public async Task Login_GoesBackToLocalReturnUrlsOnly(string? returnUrl, string expected)
    {
        var email = await app.NewBrowser().SignUpAsync();
        var browser = app.NewBrowser();
        await browser.PageAsync("/login");

        var (_, response) = await browser.ActionAsync("auth.login", new { email, password = "correct horse battery", returnUrl });

        Assert.Equal(expected, (string?)response["redirect"]);
    }

    [Fact]
    public async Task Login_ThatStays_SignsIn_WithoutARedirect()
    {
        var email = await app.NewBrowser().SignUpAsync("Staying user");
        var browser = app.NewBrowser();
        await browser.PageAsync("/");

        var (status, response) = await browser.ActionAsync("auth.login", new { email, password = "correct horse battery", stay = true });
        var (_, landing) = await browser.PageAsync("/");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(response["redirect"]);
        Assert.Equal("Staying user", (string?)landing["shared"]!["user"]!["name"]);
    }

    [Fact]
    public async Task Login_ThatStays_WithoutJavaScript_GoesBackToItsPage()
    {
        var email = await app.NewBrowser().SignUpAsync();
        var browser = app.NewBrowser();
        await browser.PageAsync("/");

        var response = await browser.PostFormAsync("auth.login", ("email", email), ("password", "correct horse battery"), ("stay", "true"), ("__netjes_return", "/"));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Login_WithoutJavaScript_SetsTheCookieAndRedirects()
    {
        var email = await app.NewBrowser().SignUpAsync();
        var browser = app.NewBrowser();
        await browser.PageAsync("/login");

        var response = await browser.PostFormAsync("auth.login", ("email", email), ("password", "correct horse battery"), ("__netjes_return", "/login"));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/projects", response.Headers.Location?.OriginalString);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("taskboard.auth=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_WithoutJavaScript_WrongPassword_RendersTheLoginPageWithTheError()
    {
        var browser = app.NewBrowser();
        await browser.PageAsync("/login");

        var response = await browser.PostFormAsync("auth.login", ("email", NewEmail()), ("password", "nope nope nope"), ("__netjes_return", "/login?returnUrl=%2Fprojects"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("\"page\":\"app/(marketing)/login/page.tsx\"", app.Renderer.Last);
        Assert.Contains("That email and password do not match.", app.Renderer.Last);
    }

    [Fact]
    public async Task LoginPage_WhenSignedIn_RedirectsToTheReturnUrl()
    {
        var browser = app.NewBrowser();
        await browser.SignUpAsync();

        var (_, payload) = await browser.PageAsync("/login?returnUrl=%2Fprojects%2Fdemo");

        Assert.Equal("redirect", (string?)payload["kind"]);
        Assert.Equal("/projects/demo", (string?)payload["location"]);
    }

    [Fact]
    public async Task Logout_SignsOut_AndTheAppAsksToSignInAgain()
    {
        var browser = app.NewBrowser();
        await browser.SignUpAsync();

        var (_, response) = await browser.ActionAsync("auth.logout", new { });
        var (status, page) = await browser.PageAsync("/projects");

        Assert.Equal("/", (string?)response["redirect"]);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("app/unauthorized.tsx", (string?)page["page"]);
        Assert.Null(page["shared"]!["user"]);
    }

    [Fact]
    public async Task PasswordsAreStoredHashed()
    {
        var email = await app.NewBrowser().SignUpAsync();

        var hash = await app.WithDbAsync(db => Task.FromResult(db.Users.Single(u => u.Email == email).PasswordHash));

        Assert.DoesNotContain("correct horse battery", hash);
        Assert.StartsWith("AQAAAA", hash);
    }
}
