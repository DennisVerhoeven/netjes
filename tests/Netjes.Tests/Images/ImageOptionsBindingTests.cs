using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Netjes.Tests.Images;

public class ImageOptionsBindingTests
{
    private static NetjesImageOptions Bind(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddNetjes();
        return services.BuildServiceProvider().GetRequiredService<IOptions<NetjesOptions>>().Value.Images;
    }

    [Fact]
    public void ConfiguredWidthsAndQualities_ReplaceTheDefaults()
    {
        var images = Bind(new()
        {
            ["Netjes:Images:Widths:0"] = "640",
            ["Netjes:Images:Widths:1"] = "1080",
            ["Netjes:Images:Qualities:0"] = "60",
        });

        Assert.Equal([640, 1080], images.Widths);
        Assert.Equal([60], images.Qualities);
    }

    [Fact]
    public void WithoutConfiguration_TheDefaultsApply()
    {
        var images = Bind([]);

        Assert.Equal(16, images.Widths.Length);
        Assert.Equal([75], images.Qualities);
    }
}
