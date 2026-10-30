namespace Netjes.Tests.Endpoints;

public class LocalUrlTests
{
    [Theory]
    [InlineData("/", true)]
    [InlineData("/projects?tab=1", true)]
    [InlineData("/a/b#c", true)]
    [InlineData("//evil.example", false)]
    [InlineData("/\\evil.example", false)]
    [InlineData("https://evil.example/", false)]
    [InlineData("projects", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("\t//evil.example", false)]
    public void IsLocalUrl_FollowsAspNetCoreRules(string? url, bool local)
    {
        Assert.Equal(local, Page.IsLocalUrl(url));
    }
}
