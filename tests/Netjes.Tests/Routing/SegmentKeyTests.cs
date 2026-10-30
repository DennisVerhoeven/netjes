using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using Netjes.Routing;

namespace Netjes.Tests.Routing;

public class SegmentKeyTests
{
    private static readonly RouteSegment Admin = new("app/admin", "/admin", []);

    private static LayoutLoaderRegistry Registry(string? secret = null) =>
        new(new NetjesContext(
            Options.Create(new NetjesOptions { SegmentKeySecret = secret }),
            new HostingEnvironment { ContentRootPath = AppContext.BaseDirectory, EnvironmentName = "Production" }));

    private static HttpContext SignedIn(params string[] roles) => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "ada"), .. roles.Select(r => new Claim(ClaimTypes.Role, r))], "Cookies")),
    };

    [Fact]
    public void Key_ChangesWhenTheUsersRolesChange()
    {
        var registry = Registry();

        var asAdmin = registry.KeyFor(Admin, SignedIn("Admin"), "v1");

        Assert.Equal(asAdmin, registry.KeyFor(Admin, SignedIn("Admin"), "v1"));
        Assert.NotEqual(asAdmin, registry.KeyFor(Admin, SignedIn(), "v1"));
        Assert.NotEqual(asAdmin, registry.KeyFor(Admin, SignedIn("Admin", "Billing"), "v1"));
    }

    private static readonly RouteSegment Project = new("app/[team]/[project]", "/[team]/[project]", ["team", "project"]);

    private static HttpContext At(HttpContext http, params (string Name, string Value)[] routeValues)
    {
        foreach (var (name, value) in routeValues)
        {
            http.Request.RouteValues[name] = value;
        }

        return http;
    }

    private static HttpContext User(string? name, string? id, params string[] roles) => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                .. name is null ? [] : new[] { new Claim(ClaimTypes.Name, name) },
                .. id is null ? [] : new[] { new Claim(ClaimTypes.NameIdentifier, id) },
                .. roles.Select(r => new Claim(ClaimTypes.Role, r)),
            ],
            "Cookies")),
    };

    [Fact]
    public void Key_RouteValuesThatDifferOnlyInWhereALineBreakFalls_Differ()
    {
        // /a%0Ab/c and /a/b%0Ac: the path decodes %0A to a line break in the route value.
        var registry = Registry();

        var one = registry.KeyFor(Project, At(SignedIn(), ("team", "a\nb"), ("project", "c")), "v1");
        var other = registry.KeyFor(Project, At(SignedIn(), ("team", "a"), ("project", "b\nc")), "v1");

        Assert.NotEqual(one, other);
    }

    [Fact]
    public void Key_ARoleWithACommaAndTwoRoles_Differ()
    {
        var registry = Registry();

        Assert.NotEqual(registry.KeyFor(Admin, SignedIn("Admin,Billing"), "v1"), registry.KeyFor(Admin, SignedIn("Admin", "Billing"), "v1"));
    }

    [Fact]
    public void Key_AUserNameWithALineBreak_CannotPassForTheNextFields()
    {
        // Ada's name runs into the identifier field, and the route value takes up the line break that leaves over.
        var registry = Registry();
        var team = new RouteSegment("app/[team]", "/[team]", ["team"]);

        var named = registry.KeyFor(team, At(User("ada\nx", null), ("team", "core")), "v1");
        var other = registry.KeyFor(team, At(User("ada", "x"), ("team", "\ncore")), "v1");

        Assert.NotEqual(named, other);
    }

    [Fact]
    public void Key_AnotherUserOnTheSameUrl_Differs()
    {
        var registry = Registry();

        var ada = registry.KeyFor(Project, At(User("ada", "1"), ("team", "a"), ("project", "c")), "v1");

        Assert.NotEqual(ada, registry.KeyFor(Project, At(User("bob", "2"), ("team", "a"), ("project", "c")), "v1"));
        Assert.NotEqual(ada, registry.KeyFor(Project, At(User("ada", "2"), ("team", "a"), ("project", "c")), "v1"));
        Assert.NotEqual(ada, registry.KeyFor(Project, At(new DefaultHttpContext(), ("team", "a"), ("project", "c")), "v1"));
    }

    [Fact]
    public void Key_AMissingRouteValueAndAnEmptyOne_Differ()
    {
        var registry = Registry();
        var docs = new RouteSegment("app/docs/[[...slug]]", "/docs/[[...slug]]", ["slug"]);

        Assert.NotEqual(registry.KeyFor(docs, SignedIn(), "v1"), registry.KeyFor(docs, At(SignedIn(), ("slug", "")), "v1"));
    }

    [Fact]
    public void SecretFingerprint_IsTheSameForTheSameSecret_AndGivesNoneOfItAway()
    {
        var secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        var fingerprint = Registry(secret).SecretFingerprint;

        Assert.Equal(fingerprint, Registry(secret).SecretFingerprint);
        Assert.NotEqual(fingerprint, Registry(secret + "x").SecretFingerprint);
        // Without a secret each registry makes its own, as each instance does.
        Assert.NotEqual(Registry().SecretFingerprint, Registry().SecretFingerprint);
        Assert.DoesNotContain(secret, fingerprint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(secret))[..16], fingerprint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASecretShorterThan32Bytes_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Registry("too short"));

        Assert.Contains("SegmentKeySecret", ex.Message);
        Assert.NotEmpty(Registry(new string('x', 32)).KeyFor(Admin, SignedIn(), "v1"));
    }
}
