using Netjes.Rendering;

namespace Netjes.Tests.Rendering;

public class ViteManifestTests
{
    private const string Manifest = """
        {
          "../../packages/netjes/dist/client-entry.js": {
            "file": "assets/client-entry-AAA.js",
            "src": "../../packages/netjes/dist/client-entry.js",
            "isEntry": true,
            "imports": ["_react-BBB.js"],
            "dynamicImports": ["app/layout.tsx", "app/page.tsx", "app/blog/[slug]/page.tsx"],
            "css": ["assets/client-entry-CCC.css"]
          },
          "_react-BBB.js": { "file": "assets/react-BBB.js" },
          "_shared-DDD.js": { "file": "assets/shared-DDD.js", "imports": ["_react-BBB.js"], "css": ["assets/shared-EEE.css"] },
          "app/layout.tsx": {
            "file": "assets/layout-FFF.js",
            "src": "app/layout.tsx",
            "isDynamicEntry": true,
            "imports": ["_react-BBB.js"],
            "css": ["assets/layout-GGG.css"]
          },
          "app/page.tsx": {
            "file": "assets/index-HHH.js",
            "src": "app/page.tsx",
            "isDynamicEntry": true,
            "imports": ["_react-BBB.js", "_shared-DDD.js"]
          },
          "app/blog/[slug]/page.tsx": {
            "file": "assets/_slug_-III.js",
            "src": "app/blog/[slug]/page.tsx",
            "isDynamicEntry": true,
            "imports": ["_shared-DDD.js"],
            "dynamicImports": ["app/page.tsx"]
          },
          "public/logo.svg": { "file": "assets/logo-JJJ.svg", "src": "public/logo.svg" }
        }
        """;

    [Fact]
    public void SourceFiles_ListsEveryChunkSource()
    {
        var manifest = ViteManifest.Parse(Manifest);

        Assert.Contains("app/page.tsx", manifest.SourceFiles);
        Assert.Contains("app/blog/[slug]/page.tsx", manifest.SourceFiles);
        Assert.Contains("app/layout.tsx", manifest.SourceFiles);
    }

    [Fact]
    public void AssetsFor_IncludesEntryScriptAndCssOfModulesAndTheirStaticImports()
    {
        var manifest = ViteManifest.Parse(Manifest);

        var assets = manifest.AssetsFor(["app/layout.tsx", "app/page.tsx"]);

        Assert.Equal(["/assets/client-entry-AAA.js"], assets.Scripts);
        Assert.Equal(
            ["/assets/client-entry-CCC.css", "/assets/layout-GGG.css", "/assets/shared-EEE.css"],
            assets.Stylesheets);
        Assert.Equal(
            ["/assets/react-BBB.js", "/assets/layout-FFF.js", "/assets/index-HHH.js", "/assets/shared-DDD.js"],
            assets.ModulePreloads);
    }

    [Fact]
    public void AssetsFor_DoesNotFollowDynamicImports()
    {
        var manifest = ViteManifest.Parse(Manifest);

        var assets = manifest.AssetsFor(["app/blog/[slug]/page.tsx"]);

        Assert.DoesNotContain("/assets/index-HHH.js", assets.ModulePreloads);
        Assert.Contains("/assets/_slug_-III.js", assets.ModulePreloads);
    }

    [Fact]
    public void AssetsFor_UnknownModule_IsSkipped()
    {
        var manifest = ViteManifest.Parse(Manifest);

        var assets = manifest.AssetsFor(["app/missing/page.tsx"]);

        Assert.Equal(["/assets/client-entry-AAA.js"], assets.Scripts);
    }

    [Fact]
    public void Parse_ManifestWithoutEntry_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ViteManifest.Parse("""{ "a.js": { "file": "a.js" } }"""));

        Assert.Contains("entry", ex.Message);
    }

    [Fact]
    public void Version_IsStableForSameContentAndChangesWithContent()
    {
        Assert.Equal(ViteManifest.Parse(Manifest).Version, ViteManifest.Parse(Manifest).Version);
        Assert.NotEqual(ViteManifest.Parse(Manifest).Version, ViteManifest.Parse(Manifest.Replace("AAA", "ZZZ")).Version);
    }
}
