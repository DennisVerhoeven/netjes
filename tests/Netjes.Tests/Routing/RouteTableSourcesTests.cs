using Netjes.Routing;

namespace Netjes.Tests.Routing;

/// <summary>
/// The route table built from several sources: the app's own folder and page packages mounted at a prefix, merged by
/// folder.
/// </summary>
public class RouteTableSourcesTests
{
    [Theory]
    [InlineData("/help", "help", "/help", "/help", new string[0])]
    [InlineData("/(marketing)/help", "(marketing)/help", "/help", "/help", new string[0])]
    [InlineData("/support/(desk)/help", "support/(desk)/help", "/support/help", "/support/help", new string[0])]
    [InlineData("/[tenant]/help", "[tenant]/help", "/[tenant]/help", "/{tenant}/help", new[] { "tenant" })]
    [InlineData("/", "", "/", "/", new string[0])]
    public void MountPrefix_GivesItsFolderPatternTemplateAndParameters(
        string prefix, string folder, string pattern, string template, string[] parameters)
    {
        var mount = MountPrefix.Parse(prefix);

        Assert.Equal(folder, mount.Folder);
        Assert.Equal(pattern, mount.Pattern);
        Assert.Equal(template, mount.Template);
        Assert.Equal(parameters, mount.Parameters);
    }

    // A prefix is a folder path that every page of the package goes under: anything that would match more than one
    // folder, hide the package, or climb out of the app directory is refused.
    [Theory]
    [InlineData("/help/[...x]", "catch-all")]
    [InlineData("/help/[[...x]]", "catch-all")]
    [InlineData("/[...x]/help", "catch-all")]
    [InlineData("/@x/help", "slot")]
    [InlineData("/_x/help", "private")]
    [InlineData("/(.)x", "intercepting")]
    [InlineData("/(..)x", "intercepting")]
    [InlineData("/(...)x", "intercepting")]
    [InlineData("/help/.", "'.'")]
    [InlineData("/./help", "'.'")]
    [InlineData("/help/..", "'..'")]
    [InlineData("/../help", "'..'")]
    [InlineData("/%2e%2e/help", "'%'")]
    [InlineData("/help%2Fx", "'%'")]
    [InlineData("/help\\x", "'\\'")]
    [InlineData("\\help", "'\\'")]
    [InlineData("/help//x", "empty segment")]
    [InlineData("//help", "empty segment")]
    [InlineData("/help/", "empty segment")]
    [InlineData("", "starts with '/'")]
    [InlineData("help", "starts with '/'")]
    [InlineData("/[a]/[A]", "more than once")]
    [InlineData("/[foo-bar]", "parameter name")]
    [InlineData("/[]", "parameter name")]
    [InlineData("/()", "not allowed")]
    [InlineData("/{tenant}/help", "not allowed")]
    [InlineData("/help?x", "not allowed")]
    [InlineData("/help#x", "not allowed")]
    [InlineData("/help*", "not allowed")]
    [InlineData("/he[l]p", "not allowed")]
    [InlineData("/he lp", "not allowed")]
    [InlineData("/help:x", "not allowed")]
    public void MountPrefix_Refuses_WithItsReason(string prefix, string reason)
    {
        var ex = Assert.Throws<ArgumentException>(() => MountPrefix.Parse(prefix));

        Assert.Contains($"'{prefix}'", ex.Message);
        Assert.Contains(reason, ex.Message);
        Assert.Equal("prefix", ex.ParamName);
    }
}
